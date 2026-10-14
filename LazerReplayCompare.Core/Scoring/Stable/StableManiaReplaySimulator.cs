// The judgement core is ported from YAVSRG's osu!mania ruleset and gameplay event processor
// (prelude, https://github.com/YAVSRG/YAVSRG: Rulesets/osu!mania.fs, Scoring/OsuHolds.fs, Scoring/HitMechanics.fs,
// Scoring/GameplayEventProcessor.fs), MIT licence, Copyright (c) percyqaz.
// The hold note rules after a break (re-press counted as the head, capped at GOOD; early heads counted as pressed
// 1 ms before the tail) follow zzzzv/mania-judge, MIT licence.
// Everything else (undo window, tail window edges, which object a press belongs to after a missed head or a drop, and
// the combo rules) was measured against osu!stable with recorded replays and live judgement logs.

using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mania.Replays;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// osu!stable osu!mania judgement for a replay: one judgement per object (hold notes included), plus stable's combo
/// (hold notes give combo for every press and every 100 ms held, and break it when let go).
/// </summary>
internal sealed class StableManiaReplaySimulator
{
    /// <summary>One replay frame reduced to the column key states after it.</summary>
    public readonly record struct KeyFrame(double Time, bool[] Keys);

    /// <summary>How a hold note judgement came about, so its result can be re-evaluated with nudged timing.</summary>
    /// <param name="HeadDelta">Head error used for the judgement (the re-press after a break when <paramref name="Regrab"/>).</param>
    /// <param name="ReleaseDelta">Release error (the late release window edge when never released).</param>
    /// <param name="Undone">The head press was taken back once (results above GOOD are capped).</param>
    public sealed record HoldTiming(double HeadDelta, double ReleaseDelta, bool Missed, bool Overhold, bool Dropped, bool MissedHead, bool Undone, bool Regrab);

    /// <param name="NoteDelta">Hit error of a note judged by a press.</param>
    /// <param name="Hold">Timing of a hold note judged by its release rules (null for rule misses).</param>
    public sealed record Judgement(double Time, ManiaHitObject Object, HitResult Result, double? NoteDelta = null, HoldTiming? Hold = null);

    /// <summary>A combo change: +1 or a break back to 0. Breaks sort after increments at the same time.</summary>
    public readonly record struct ComboEvent(double Time, bool Increment);

    private enum Flag { Nothing, HitRequired, HitHoldRequired, HitAccepted, ReleaseRequired, ReleaseAccepted }

    private enum Hold { Nothing, Holding, Dropped, Regrabbed, MissedHeadDropped, MissedHeadRegrabbed }

    private static bool isDropped(Hold h) => h is Hold.Dropped or Hold.Regrabbed or Hold.MissedHeadDropped or Hold.MissedHeadRegrabbed;
    private static bool missedHead(Hold h) => h is Hold.MissedHeadDropped or Hold.MissedHeadRegrabbed;
    private static bool isHeld(Hold h) => h is Hold.Holding or Hold.Regrabbed or Hold.MissedHeadRegrabbed;

    private sealed class Row
    {
        public required double Time;
        public required Flag[] Status;
        public required double[] Delta;
        public required ManiaHitObject?[] Objects;
    }

    // A tail release between the MISS and MEH windows still counts as the tail within this long after the head's late
    // window (and an early drop that close to the head lets a next object take the re-press).
    private const double head_window_carry = 7;

    // A re-press after letting go before the head shrinks the tail's early window by this much, when let go after the head's time.
    private const double undo_tail_shrink = 2;

    // A dropped hold note let go near its tail only takes a press after its end while the next object is at most this late.
    private const double next_object_tolerance = 10;

    // Combo tick interval while a hold note is held (map time).
    private const double combo_tick = 100;

    private readonly Row[] rows;
    private readonly int[][] columnRows;   // per column: indices of the rows with something in that column
    private readonly int[][] releaseRows;  // per column: indices of the rows where a hold note ends in that column
    private readonly int keys;
    private readonly (Hold state, int head)[] holds;
    private readonly bool[] keyState;
    private int expired;
    private readonly List<Judgement> judgements = new();

    // Windows in map time (clipped: floor + 0.5), Perfect..Miss.
    private readonly (double early, double late)[] noteWindows;
    private readonly double w100, w50, w0, overhold200, overhold100;
    private readonly double baseP, baseG, baseGd, baseO;
    private readonly double rawMiss;
    private readonly double noteEarly, noteLate, releaseEarly, releaseLate;

    private readonly double?[] firstDrop;
    private readonly bool[] lateTaken;
    private readonly bool[] heldThrough;   // the key was still down from an earlier object when this hold's head window ran out
    private readonly bool[] earlyDrop;     // the hold was let go while its head window (plus carry) was still open
    private readonly bool[] undone;
    private readonly int[] undoneHead;     // row index of the hold the undo belongs to
    private readonly bool[] undoTail;      // the undo came from a release inside the tail's early (MISS..MEH) window
    private readonly bool[] undoAfterHead; // the undo release came after the head's time
    private readonly double?[] regrabAt;

    // combo
    private readonly List<(double Time, int Order, bool Increment)> comboEvents = new();
    private readonly HashSet<ManiaHitObject> brokenHolds = new();   // hold notes that already broke combo (head miss, letting go)
    private readonly (int Row, double At)?[] pendingHeadMiss;
    private readonly int?[] heldHeadMiss;
    private readonly int[] tickRow;
    private readonly double[] tickFrom, tickAnchorTime;

    // rule-boundary tracking
    private int currentEvent = -1;
    private int currentColumn = -1;
    private readonly Dictionary<int, double> sensitiveEvents = new();

    public IReadOnlyList<Judgement> Judgements => judgements;

    /// <summary>Combo changes in time order (after <see cref="Run"/>).</summary>
    public IReadOnlyList<ComboEvent> Combo { get; private set; } = [];

    public int MaxCombo { get; private set; }

    /// <summary>
    /// Key events (numbered as in <see cref="Transitions"/>) for which a rule decision was taken within 1 ms of its
    /// threshold, with the closest distance seen: nudging them may change the outcome.
    /// </summary>
    public IReadOnlyDictionary<int, double> SensitiveEvents => sensitiveEvents;

    /// <param name="mode">1: Hard Rock, -1: Easy, 0: neither.</param>
    /// <param name="rate">Playback rate (DT/HT); windows keep their real-time length.</param>
    public StableManiaReplaySimulator(IBeatmap playable, int columns, double overallDifficulty, int mode, double rate)
    {
        keys = columns;
        holds = new (Hold, int)[keys];
        for (int k = 0; k < keys; k++) holds[k] = (Hold.Nothing, -1);
        keyState = new bool[keys];

        // --- windows (osu!mania.fs): everything is kept in map time, so the rate only scales the windows ---
        double od = Math.Round(overallDifficulty * 10) / 10;
        double apply(double w) => mode switch { 1 => w / 1.4, -1 => w * 1.4, _ => w };
        static double clipped(double w) => Math.Floor(w) + 0.5;
        double win(double raw) => clipped(apply(raw) * rate);

        double P = win(16), G = win(64 - 3 * od), Gd = win(97 - 3 * od);
        double O = win(127 - 3 * od), M = win(151 - 3 * od), X = win(188 - 3 * od);
        noteWindows = new[] { (-P, P), (-G, G), (-Gd, Gd), (-O, O - 1), (-M, O - 1), (-X, O - 1) };

        double lnWin(double raw) => clipped(Math.Floor(apply(raw) * rate));
        double lnBase(double raw) => Math.Floor(apply(raw) * rate);
        baseP = lnBase(16); baseG = lnBase(64 - 3 * od); baseGd = lnBase(97 - 3 * od); baseO = lnBase(127 - 3 * od);
        w100 = lnWin(127 - 3 * od); w50 = lnWin(151 - 3 * od); w0 = lnWin(188 - 3 * od);
        rawMiss = apply(188 - 3 * od) * rate;

        double overhold(double baseValue)
        {
            double b = Math.Floor(baseValue - od * 3) + 0.5;
            return (mode switch { -1 => Math.Floor(b * 1.4) - 0.5, 1 => Math.Floor(b / 1.4) + 1.5, _ => b }) * rate;
        }

        overhold200 = overhold(43);
        overhold100 = overhold(103);

        noteEarly = -X; noteLate = O - 1;
        releaseEarly = -w0; releaseLate = w100 - 1;

        firstDrop = new double?[keys];
        lateTaken = new bool[keys];
        heldThrough = new bool[keys];
        earlyDrop = new bool[keys];
        undone = new bool[keys];
        undoneHead = new int[keys];
        undoTail = new bool[keys];
        undoAfterHead = new bool[keys];
        regrabAt = new double?[keys];
        pendingHeadMiss = new (int, double)?[keys];
        heldHeadMiss = new int?[keys];
        tickRow = Enumerable.Repeat(-1, keys).ToArray();
        tickFrom = new double[keys];
        tickAnchorTime = new double[keys];

        // --- rows (HitFlagData.create_gameplay) ---
        var byTime = new SortedDictionary<double, Row>();
        Row row(double t)
        {
            if (!byTime.TryGetValue(t, out var r))
                byTime[t] = r = new Row { Time = t, Status = new Flag[keys], Delta = new double[keys], Objects = new ManiaHitObject?[keys] };
            return r;
        }

        foreach (var ho in playable.HitObjects.Cast<ManiaHitObject>())
        {
            var start = row(Math.Round(ho.StartTime));
            start.Status[ho.Column] = ho is HoldNote ? Flag.HitHoldRequired : Flag.HitRequired;
            start.Delta[ho.Column] = noteLate;
            start.Objects[ho.Column] = ho;

            if (ho is HoldNote hold)
            {
                var end = row(Math.Round(hold.EndTime));
                end.Status[ho.Column] = Flag.ReleaseRequired;
                end.Delta[ho.Column] = releaseLate;
                end.Objects[ho.Column] = ho;
            }
        }

        rows = byTime.Values.ToArray();
        columnRows = Enumerable.Range(0, keys).Select(k => Enumerable.Range(0, rows.Length).Where(i => rows[i].Status[k] != Flag.Nothing).ToArray()).ToArray();
        releaseRows = Enumerable.Range(0, keys).Select(k => columnRows[k].Where(i => rows[i].Status[k] == Flag.ReleaseRequired).ToArray()).ToArray();
    }

    /// <summary>Replay frames reduced to frames where some column changes, in time order.</summary>
    public static List<KeyFrame> ToKeyFrames(IEnumerable<ManiaReplayFrame> frames, int columns)
    {
        var result = new List<KeyFrame>();
        var current = new bool[columns];
        foreach (var frame in frames.OrderBy(f => f.Time))
        {
            var next = new bool[columns];
            foreach (var action in frame.Actions)
                if ((int)action >= 0 && (int)action < columns) next[(int)action] = true;

            if (!next.SequenceEqual(current))
                result.Add(new KeyFrame(frame.Time, next));
            current = next;
        }

        return result;
    }

    /// <summary>Every key transition as (frame index, column), numbered in processing order.</summary>
    public static List<(int Frame, int Column)> Transitions(IReadOnlyList<KeyFrame> frames)
    {
        var result = new List<(int, int)>();
        var current = new bool[frames.Count > 0 ? frames[0].Keys.Length : 0];
        for (int i = 0; i < frames.Count; i++)
        {
            for (int k = 0; k < current.Length; k++)
                if (current[k] != frames[i].Keys[k]) result.Add((i, k));
            current = frames[i].Keys;
        }

        return result;
    }

    /// <summary>
    /// <paramref name="frames"/> with some key transitions (numbered as in <see cref="Transitions"/>) moved by the given
    /// number of ms; everything else keeps its timing. Null when a move would reorder a transition past another one of
    /// the same column.
    /// </summary>
    public static List<KeyFrame>? Nudge(IReadOnlyList<KeyFrame> frames, IReadOnlyDictionary<int, double> shifts)
    {
        var events = new List<(double Time, int Order, int Column, bool Down)>();
        var current = new bool[frames.Count > 0 ? frames[0].Keys.Length : 0];
        int n = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            for (int k = 0; k < current.Length; k++)
            {
                if (current[k] == frames[i].Keys[k]) continue;
                events.Add((frames[i].Time + shifts.GetValueOrDefault(n), i, k, frames[i].Keys[k]));
                n++;
            }

            current = frames[i].Keys;
        }

        if (shifts.Keys.Any(t => t < 0 || t >= events.Count)) return null;

        // Same-column transitions must keep their order (a press stays before its release).
        var lastTime = new double[current.Length];
        Array.Fill(lastTime, double.NegativeInfinity);
        foreach (var e in events)
        {
            if (e.Time <= lastTime[e.Column]) return null;
            lastTime[e.Column] = e.Time;
        }

        var ordered = events.OrderBy(e => e.Time).ThenBy(e => e.Order).ThenBy(e => e.Column).ToList();
        var result = new List<KeyFrame>();
        var keys = new bool[current.Length];
        for (int i = 0; i < ordered.Count;)
        {
            int j = i;
            var next = (bool[])keys.Clone();
            // transitions of one original frame stay together; a moved one forms a frame of its own
            while (j < ordered.Count && ordered[j].Time == ordered[i].Time && ordered[j].Order == ordered[i].Order)
            {
                next[ordered[j].Column] = ordered[j].Down;
                j++;
            }

            result.Add(new KeyFrame(ordered[i].Time, next));
            keys = next;
            i = j;
        }

        return result;
    }

    public void Run(IReadOnlyList<KeyFrame> frames)
    {
        var current = new bool[keys];
        int transition = 0;
        foreach (var frame in frames)
        {
            var next = frame.Keys;

            // KeyPressReader: per column, release or press; the key state updates after the whole frame.
            for (int k = 0; k < keys; k++)
            {
                if (current[k] == next[k]) continue;

                currentEvent = transition++;
                currentColumn = k;
                var before = ((Hold, int)[])holds.Clone();
                if (current[k]) keyUp(frame.Time, k);
                else keyDown(frame.Time, k);
                comboTransitions(before, frame.Time);

                if (current[k] && heldHeadMiss[k] is int hr)
                {
                    var obj = rows[hr].Objects[k]!;
                    if (brokenHolds.Add(obj)) comboEvents.Add((frame.Time, 1, false));
                    heldHeadMiss[k] = null;
                }
            }

            current = next;
            Array.Copy(next, keyState, keys);
        }

        currentEvent = -1;
        currentColumn = -1;
        var last = ((Hold, int)[])holds.Clone();
        missUnhitExpired(double.PositiveInfinity);
        comboTransitions(last, double.PositiveInfinity);
        for (int k = 0; k < keys; k++) stopTicks(k, double.PositiveInfinity);
        for (int k = 0; k < keys; k++)
        {
            if (pendingHeadMiss[k] is { } ph)
            {
                comboEvents.Add((ph.At, 1, false));
                brokenHolds.Add(rows[ph.Row].Objects[k]!);
            }
        }

        foreach (var j in judgements)
        {
            // A missed hold note that already broke combo (head miss, letting go) does not break it again.
            bool alreadyBroken = j.Object is HoldNote && brokenHolds.Contains(j.Object);
            if (j.Result == HitResult.Miss)
            {
                if (!alreadyBroken) comboEvents.Add((j.Time, 1, false));
            }
            else if (j.Object is not HoldNote)
                comboEvents.Add((j.Time, 0, true));
        }

        comboEvents.Sort((x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.Order.CompareTo(y.Order));
        int combo = 0;
        foreach (var e in comboEvents)
        {
            combo = e.Increment ? combo + 1 : 0;
            MaxCombo = Math.Max(MaxCombo, combo);
        }

        Combo = comboEvents.Select(e => new ComboEvent(e.Time, e.Increment)).ToList();
    }

    // ---- rule-boundary helpers: comparisons whose operands are within 1 ms mark the current key event ----

    private void near(double a, double b)
    {
        double distance = Math.Abs(a - b);
        if (currentEvent >= 0 && distance <= 1 + 1e-9)
            sensitiveEvents[currentEvent] = Math.Min(distance, sensitiveEvents.GetValueOrDefault(currentEvent, double.PositiveInfinity));
    }

    private bool lt(double a, double b) { near(a, b); return a < b; }
    private bool le(double a, double b) { near(a, b); return a <= b; }
    private bool gt(double a, double b) { near(a, b); return a > b; }
    private bool ge(double a, double b) { near(a, b); return a >= b; }

    // ---- judgement ----

    private static readonly HitResult[] results = { HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss };

    /// <summary>Result of a note hit with error <paramref name="delta"/> (map time).</summary>
    public HitResult NoteResult(double delta)
    {
        for (int j = 0; j < noteWindows.Length - 1; j++)
            if (delta >= noteWindows[j].early && delta <= noteWindows[j].late)
                return results[j];
        return HitResult.Miss;
    }

    // OsuHolds.ln_judgement with the osu! wiki's whole-millisecond windows
    private HitResult lnJudgement(double head, double release, bool overheld, bool dropped)
    {
        double r = Math.Abs(release), h = Math.Abs(head);
        if (overheld)
            return h < overhold200 ? HitResult.Good : h < overhold100 ? HitResult.Ok : HitResult.Meh;
        if (dropped)
            return release < -w50 ? HitResult.Miss : HitResult.Meh;
        if (release < -w50) return HitResult.Miss;

        double sum = r + h;
        if (h <= baseP * 1.2 && sum <= baseP * 2.4) return HitResult.Perfect;
        if (h <= baseG * 1.1 && sum <= baseG * 2.2) return HitResult.Great;
        if (h <= baseGd && sum <= baseGd * 2) return HitResult.Good;
        if (h <= baseO && sum <= baseO * 2) return HitResult.Ok;
        return HitResult.Meh;
    }

    /// <summary>Result of a hold note judged with <paramref name="t"/> (also used to re-evaluate nudged timing).</summary>
    public HitResult HoldResult(HoldTiming t)
    {
        var result = t.Missed && t.MissedHead && !t.Overhold ? HitResult.Miss : lnJudgement(t.HeadDelta, t.ReleaseDelta, t.Overhold, t.Dropped);
        // A dropped hold note with no release inside the release window is a miss.
        if (t.Dropped && t.Missed && !t.Overhold) result = HitResult.Miss;
        if ((t.Undone || t.Regrab) && result is HitResult.Perfect or HitResult.Great) result = HitResult.Good;
        return result;
    }

    private void releaseEvent(double time, int rowIndex, int k, double releaseDelta, bool missed, bool overhold, bool dropped, double headDelta, bool missedHeadFlag)
    {
        bool regrab = false;
        if (dropped && !missedHeadFlag && regrabAt[k] is double ra && !(missed && !overhold))
        {
            // mania-judge: after a break, the re-press acts as the head press (capped at GOOD).
            headDelta = ra - Math.Round(rows[rowIndex].Objects[k]!.StartTime);
            dropped = false;
            regrab = true;
        }

        regrabAt[k] = null;
        var timing = new HoldTiming(headDelta, releaseDelta, missed, overhold, dropped, missedHeadFlag, undone[k], regrab);
        undone[k] = false;
        firstDrop[k] = null;
        judgements.Add(new Judgement(time, rows[rowIndex].Objects[k]!, HoldResult(timing), Hold: timing));
    }

    private void ruleMiss(double time, ManiaHitObject hitObject) => judgements.Add(new Judgement(time, hitObject, HitResult.Miss));

    /// <summary>Settles the hold note whose head is at row <paramref name="head"/>: its release is no longer expected.</summary>
    private void acceptRelease(int head, int k)
    {
        int i = requiredReleaseFrom(head + 1, k);
        if (i >= 0) rows[i].Status[k] = Flag.ReleaseAccepted;
    }

    /// <summary>Position in <paramref name="list"/> (ascending row indices) of the first row at or after <paramref name="row"/>.</summary>
    private static int positionFrom(int[] list, int row)
    {
        int index = Array.BinarySearch(list, row);
        return index >= 0 ? index : ~index;
    }

    /// <summary>The first hold end in column <paramref name="k"/> at or after row <paramref name="from"/> (-1 if none).</summary>
    private int releaseFrom(int from, int k)
    {
        var list = releaseRows[k];
        int p = positionFrom(list, from);
        return p < list.Length ? list[p] : -1;
    }

    /// <summary>The first hold end still expecting its release in column <paramref name="k"/> at or after row <paramref name="from"/> (-1 if none).</summary>
    private int requiredReleaseFrom(int from, int k)
    {
        var list = releaseRows[k];
        for (int p = positionFrom(list, from); p < list.Length; p++)
            if (rows[list[p]].Status[k] == Flag.ReleaseRequired) return list[p];
        return -1;
    }

    private void killExistingHold(double now, int k)
    {
        var (state, head) = holds[k];
        if (state == Hold.Nothing) return;
        int i = releaseFrom(head, k);
        if (i < 0 || rows[i].Status[k] != Flag.ReleaseRequired) return;
        rows[i].Status[k] = Flag.ReleaseAccepted;
        holds[k] = (Hold.Nothing, head);
        releaseEvent(now, i, k, releaseLate, true, false, true, rows[head].Delta[k], missedHead(state));
    }

    private void missUnhitExpired(double now)
    {
        // late note and release windows coincide
        while (expired < rows.Length)
        {
            if (currentColumn >= 0 && rows[expired].Status[currentColumn] != Flag.Nothing) near(rows[expired].Time, now - noteLate);
            if (!(rows[expired].Time < now - noteLate)) break;

            var row = rows[expired];
            double t = row.Time + noteLate;
            for (int k = 0; k < keys; k++)
            {
                switch (row.Status[k])
                {
                    case Flag.HitRequired:
                        ruleMiss(t, row.Objects[k]!);
                        row.Status[k] = Flag.HitAccepted;
                        break;

                    case Flag.HitHoldRequired when undone[k] && undoTail[k] && undoneHead[k] == expired:
                        // let go inside the tail's early window, and not pressed again before the head window ran out
                        row.Status[k] = Flag.HitAccepted;
                        acceptRelease(expired, k);
                        holds[k] = (Hold.Nothing, expired);
                        undone[k] = false;
                        undoTail[k] = false;
                        ruleMiss(t, row.Objects[k]!);
                        break;

                    case Flag.HitHoldRequired:
                        if (pendingHeadMiss[k] is { } old)
                        {
                            comboEvents.Add((old.At, 1, false));
                            brokenHolds.Add(rows[old.Row].Objects[k]!);
                        }

                        // A head passed while the key is still held breaks combo only when that key is let go;
                        // otherwise it breaks at the end of the head's MEH window unless pressed by then.
                        if (keyState[k])
                        {
                            heldHeadMiss[k] = expired;
                            pendingHeadMiss[k] = null;
                        }
                        else
                            pendingHeadMiss[k] = (expired, row.Time - noteWindows[4].early);

                        holds[k] = (Hold.MissedHeadDropped, expired);
                        lateTaken[k] = false;
                        heldThrough[k] = keyState[k];
                        row.Status[k] = Flag.HitAccepted;
                        break;

                    case Flag.ReleaseRequired:
                    {
                        var (state, head) = holds[k];
                        bool overhold = state is Hold.Regrabbed or Hold.Holding or Hold.MissedHeadRegrabbed && keyState[k];
                        releaseEvent(t, expired, k, row.Delta[k], true, overhold, isDropped(state), rows[Math.Max(head, 0)].Delta[k], missedHead(state));
                        row.Status[k] = Flag.ReleaseAccepted;
                        if (head < expired) holds[k] = (Hold.Nothing, expired);
                        break;
                    }
                }
            }

            expired++;
        }

        // the column's next object, which this event may just have reached before its late window ran out
        if (currentColumn >= 0 && positionFrom(columnRows[currentColumn], expired) is var p && p < columnRows[currentColumn].Length)
            near(rows[columnRows[currentColumn][p]].Time, now - noteLate);
    }

    private void keyDown(double now, int k)
    {
        missUnhitExpired(now);
        clearStaleUndo(k);
        if (lateHead(now, k)) return;

        // HitMechanics.osu_mania: the earliest unhit note, unless a note hit early is still ahead (column lock).
        int found = -1;
        double foundDelta = 0;
        var candidates = columnRows[k];
        for (int p = positionFrom(candidates, expired); p < candidates.Length; p++)
        {
            int i = candidates[p];
            if (lt(rows[i].Time, now - noteLate)) continue;
            if (!le(rows[i].Time, now - noteEarly)) break;

            double delta = now - rows[i].Time;
            var st = rows[i].Status[k];
            if (st == Flag.HitAccepted && lt(delta, 0)) return;
            if (st is Flag.HitRequired or Flag.HitHoldRequired)
            {
                found = i;
                foundDelta = delta;
                break;
            }
        }

        // After a missed head or a drop, the column's hold note keeps presses up to the end of its release window:
        // before its end a press is a plain regrab; after its end it only takes the press when that press comes late
        // enough after its head (2 × MISS - 10), or the hold was dropped (any press), and something else would accept it.
        {
            var (cs, ci) = holds[k];
            if (cs is Hold.MissedHeadDropped or Hold.Dropped)
            {
                double end = holdEnd(ci, k), late = now - rows[ci].Time;
                double T = -2 * noteWindows[5].early - 10;
                bool headDone = cs == Hold.Dropped || gt(late, -noteWindows[5].early);
                if (headDone && le(now, end + releaseLate))
                {
                    // A first press on a missed head inside the tail window is left to the miss rule below.
                    bool tailMiss = cs == Hold.MissedHeadDropped && ge(now, end + noteWindows[4].early) && le(late, regrabTailLate())
                                    && !heldThrough[k] && !undone[k];
                    // Before a dropped hold's end, a next object strictly inside its raw MISS window takes the press after an early drop.
                    bool nextWins = cs == Hold.Dropped && found >= 0 && lt(Math.Abs(foundDelta), rawMiss) && earlyDrop[k];
                    if (le(now, end) && !tailMiss && !nextWins)
                    {
                        holds[k] = (cs == Hold.Dropped ? Hold.Regrabbed : Hold.MissedHeadRegrabbed, ci);
                        regrabAt[k] ??= now;
                        return;
                    }

                    bool dropAny = (cs == Hold.Dropped && !undone[k]) || (cs == Hold.MissedHeadDropped && heldThrough[k]);
                    if ((gt(late, T) || dropAny) && gt(now, end))
                    {
                        // A hold dropped near its tail only takes the press while the next object's time has not come yet.
                        bool nextLate = found >= 0 && gt(foundDelta, next_object_tolerance)
                                        && cs == Hold.Dropped && firstDrop[k] is double fdv && ge(fdv, 2 * noteWindows[4].early);
                        if (!nextLate)
                        {
                            if (found >= 0 || cs == Hold.Dropped || undone[k])
                            {
                                holds[k] = (cs == Hold.Dropped ? Hold.Regrabbed : Hold.MissedHeadRegrabbed, ci);
                                regrabAt[k] ??= now;
                            }

                            return;
                        }
                    }
                }
            }
        }

        if (found >= 0)
        {
            killExistingHold(now, k);
            var row = rows[found];
            bool isHoldHead = row.Status[k] != Flag.HitRequired;
            row.Status[k] = Flag.HitAccepted;
            row.Delta[k] = foundDelta;
            // mania-judge: a head pressed earlier than the MEH window counts as pressed 1 ms before the tail.
            if (isHoldHead && lt(row.Delta[k], noteWindows[4].early))
                row.Delta[k] = holdEnd(found, k) - 1 - row.Time;

            if (isHoldHead && undone[k] && ge(now, holdEnd(found, k) + noteWindows[4].early + utdFor(k)))
            {
                // Re-pressed inside the tail's early window after letting go before the head: the hold note is missed,
                // and the press itself goes on to the next object in the column.
                acceptRelease(found, k);
                holds[k] = (Hold.Nothing, found);
                undone[k] = false;
                ruleMiss(now, row.Objects[k]!);
                keyDown(now, k);
                return;
            }

            if (isHoldHead)
            {
                holds[k] = (Hold.Holding, found);
                firstDrop[k] = null;
                regrabAt[k] = null;
            }
            else
                judgements.Add(new Judgement(now, row.Objects[k]!, NoteResult(row.Delta[k]), NoteDelta: row.Delta[k]));
            return;
        }

        var (state, idx) = holds[k];
        if (lateHead(now, k)) return;

        // A press after the head's window but within the miss window still takes the head.
        if (state == Hold.MissedHeadDropped && le(now - rows[idx].Time, -noteWindows[5].early) && rows[idx].Delta[k] >= noteLate && !lateTaken[k])
        {
            rows[idx].Delta[k] = now - rows[idx].Time;
            holds[k] = (Hold.Holding, idx);
            lateTaken[k] = true;
            return;
        }

        // Pressed for the first time inside the tail's early window with the head already missed: the hold note is missed.
        if (state == Hold.MissedHeadDropped && ge(now, holdEnd(idx, k) + noteWindows[4].early)
            && le(now - rows[idx].Time, regrabTailLate()) && !heldThrough[k] && !undone[k])
        {
            acceptRelease(idx, k);
            holds[k] = (Hold.Nothing, idx);
            ruleMiss(now, rows[idx].Objects[k]!);
            return;
        }

        // No note to hit: regrab a dropped hold.
        if (state == Hold.MissedHeadDropped) holds[k] = (Hold.MissedHeadRegrabbed, idx);
        else if (state == Hold.Dropped)
        {
            holds[k] = (Hold.Regrabbed, idx);
            regrabAt[k] ??= now;
        }
    }

    /// <summary>A press on a hold whose head window (OK) has passed unhit. True when the press was used.</summary>
    private bool lateHead(double now, int k)
    {
        var (state, idx) = holds[k];
        if (state != Hold.MissedHeadDropped || lateTaken[k] || rows[idx].Delta[k] < noteLate) return false;
        double late = now - rows[idx].Time;
        bool inTail = ge(now, holdEnd(idx, k) + noteWindows[4].early);

        // Past the MEH window, a press that can already reach a next object in the column (after this hold note) belongs to it.
        if (gt(late, -noteWindows[4].early))
        {
            double holdEndTime = holdEnd(idx, k);
            var list = columnRows[k];
            for (int p = positionFrom(list, idx + 1); p < list.Length && le(rows[list[p]].Time, now - noteEarly); p++)
            {
                int i = list[p];
                if (rows[i].Time < holdEndTime) continue;
                if (rows[i].Status[k] is Flag.HitHoldRequired or Flag.HitRequired) return false;
            }
        }

        // After letting go before the head, a late re-press inside the tail window misses the hold note; the press
        // goes on to the next object.
        if (undone[k] && ge(now, holdEnd(idx, k) + noteWindows[4].early + utdFor(k)) && le(late, -noteWindows[5].early))
        {
            acceptRelease(idx, k);
            holds[k] = (Hold.Nothing, idx);
            undone[k] = false;
            ruleMiss(now, rows[idx].Objects[k]!);
            return false;
        }

        if (le(late, -noteWindows[4].early) || (le(late, -noteWindows[5].early) && !inTail))
        {
            rows[idx].Delta[k] = late;
            holds[k] = (Hold.Holding, idx);
            lateTaken[k] = true;
            undone[k] = false;
            return true;
        }

        if (le(late, -noteWindows[5].early) && !undone[k])
        {
            acceptRelease(idx, k);
            holds[k] = (Hold.Nothing, idx);
            ruleMiss(now, rows[idx].Objects[k]!);
            return true;
        }

        return false;
    }

    /// <summary>A first press on a missed head inside the tail window misses the hold note while it is no later than this (MISS + GREAT - 9).</summary>
    private double regrabTailLate() => -noteWindows[5].early - noteWindows[1].early - 9;

    private double holdEnd(int head, int k)
    {
        int i = releaseFrom(head + 1, k);
        return i >= 0 ? rows[i].Time : double.PositiveInfinity;
    }

    private double utdFor(int k) => undoAfterHead[k] ? undo_tail_shrink : 0;

    private void clearStaleUndo(int k)
    {
        if (!undone[k]) return;
        // the undone hold is settled once its release row is accepted
        int end = releaseFrom(undoneHead[k] + 1, k);
        if (end >= 0 && rows[end].Status[k] == Flag.ReleaseRequired) return;

        undone[k] = false;
        undoTail[k] = false;
    }

    private void keyUp(double now, int k)
    {
        missUnhitExpired(now);
        clearStaleUndo(k);
        var (state, head) = holds[k];

        // A key held through a missed head and let go inside the tail window settles that hold note (as a miss).
        if (state == Hold.MissedHeadDropped)
        {
            double end = holdEnd(head, k);
            if (ge(now, end - w50) && le(now, end + releaseLate))
            {
                acceptRelease(head, k);
                holds[k] = (Hold.Nothing, head);
                ruleMiss(now, rows[head].Objects[k]!);
                return;
            }
        }

        if (state is not (Hold.Holding or Hold.Regrabbed or Hold.MissedHeadRegrabbed)) return;

        int found = -1;
        double delta = 0;
        int tail = releaseFrom(head, k);
        if (tail >= 0 && rows[tail].Status[k] == Flag.ReleaseRequired && le(rows[tail].Time, now - Math.Min(releaseEarly, noteEarly)))
        {
            found = tail;
            delta = now - rows[tail].Time;
        }

        // Between the MISS and MEH windows a release still counts as the tail only within the head's late window (+ carry).
        bool tailRelease = ge(delta, -w50)
                           || (ge(delta, -w0) && ge(now, rows[head].Time) && le(now - rows[head].Time, noteLate + head_window_carry));

        // A release inside the undo window takes the head press back even when it also falls in the tail's early window.
        bool undoFromTail = false;
        if (state == Hold.Holding && lt(now, rows[head].Time + noteLate) && lt(delta, -w50) && tailRelease)
        {
            tailRelease = false;
            undoFromTail = true;
        }

        if (found >= 0 && tailRelease)
        {
            var row = rows[found];
            row.Status[k] = Flag.ReleaseAccepted;
            bool overhold = gt(delta, releaseLate);
            if (!overhold) row.Delta[k] = delta;
            releaseEvent(now, found, k, row.Delta[k], overhold, overhold, isDropped(state), rows[head].Delta[k], missedHead(state));
            holds[k] = (Hold.Nothing, head);
            return;
        }

        // Released before the end of the head's late window: the head press is taken back and the head can be hit again.
        if (state == Hold.Holding && lt(now, rows[head].Time + noteLate))
        {
            rows[head].Status[k] = Flag.HitHoldRequired;
            rows[head].Delta[k] = noteLate;
            holds[k] = (Hold.Nothing, head);
            undone[k] = true;
            undoneHead[k] = head;
            undoTail[k] = undoFromTail;
            undoAfterHead[k] = ge(now, rows[head].Time);
            return;
        }

        // Early release: the hold is dropped.
        if (state is Hold.Holding or Hold.Regrabbed)
        {
            if (firstDrop[k] == null)
            {
                int tailRow = requiredReleaseFrom(head, k);
                if (tailRow >= 0)
                {
                    firstDrop[k] = now - rows[tailRow].Time;
                    near(firstDrop[k]!.Value, 2 * noteWindows[4].early);
                }
            }

            if (state == Hold.Holding) earlyDrop[k] = le(now - rows[head].Time, noteLate + head_window_carry);
            holds[k] = (Hold.Dropped, head);
        }
        else if (state == Hold.MissedHeadRegrabbed) holds[k] = (Hold.MissedHeadDropped, head);
    }

    // ---- combo: notes +1, every press on a hold note +1, +1 every 100 ms (map time) while held, letting go breaks ----

    private void comboTransitions((Hold State, int Idx)[] before, double now)
    {
        for (int k = 0; k < keys; k++)
        {
            var (bs, bi) = before[k];
            var (state, ai) = holds[k];
            bool wasHeld = isHeld(bs), held = isHeld(state);
            bool sameHold = bi == ai;

            if (wasHeld && (!held || !sameHold))
            {
                stopTicks(k, now);
                // letting go of a hold (dropped, not settled) breaks combo, and so does letting go before the head (undo)
                bool undoRelease = state == Hold.Nothing && undone[k] && undoneHead[k] == bi;
                if (sameHold && (state is Hold.Dropped or Hold.MissedHeadDropped || undoRelease))
                {
                    comboEvents.Add((now, 1, false));
                    brokenHolds.Add(rows[bi].Objects[k]!);
                }
            }

            if (held && (!wasHeld || !sameHold))
            {
                // every new press on the hold (head, regrab, re-press after an undo) counts
                comboEvents.Add((now, 0, true));
                if (pendingHeadMiss[k] is { } ph)
                {
                    // pressed within the head's MEH window: no break; otherwise the break happened at head + MEH
                    if (ph.Row != ai || now > ph.At)
                    {
                        comboEvents.Add((ph.At, 1, false));
                        brokenHolds.Add(rows[ph.Row].Objects[k]!);
                    }

                    pendingHeadMiss[k] = null;
                }

                tickRow[k] = ai;
                tickFrom[k] = now;
                tickAnchorTime[k] = now;
            }
        }
    }

    private void stopTicks(int k, double now)
    {
        if (tickRow[k] < 0) return;
        double end = holdEnd(tickRow[k], k);
        for (double t = tickAnchorTime[k] + combo_tick; t < now && t <= end; t += combo_tick)
            if (t > tickFrom[k]) comboEvents.Add((t, 0, true));
        tickRow[k] = -1;
    }
}
