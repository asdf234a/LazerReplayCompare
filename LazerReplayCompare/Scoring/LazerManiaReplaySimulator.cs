using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mania.Replays;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// Drawable-free port of how osu!lazer plays back an osu!mania replay:
///  - FrameStabilityContainer + FramedReplayInputHandler time stepping (every replay frame is visited exactly,
///    gaps are filled with &lt;= 1/60s steps),
///  - ReplayState press/release diffing (releases first, then presses),
///  - per step: input first, then hit object updates (UpdateAfterChildren → UpdateResult(false)),
///  - DrawableNote / DrawableHoldNote(+Head/Tail/Body) judgement logic and OrderedHitPolicy,
///  - lazer's own HitWindows (mods applied via GetPlayableBeatmap) and ScoreProcessor.
/// </summary>
internal sealed class LazerManiaReplaySimulator
{
    /// <param name="Offset">For user-triggered results: input time minus object time (before tail leniency scaling).</param>
    /// <param name="Scale">Leniency divisor applied to <paramref name="Offset"/> when judging (1.5 for hold tails).</param>
    /// <param name="CapAtMeh">Whether the result was capped at Meh (hold tail after a missed head or broken body).</param>
    public sealed record Judged(double Time, HitObject HitObject, HitResult Type, double? Offset = null, double Scale = 1, bool CapAtMeh = false);

    private sealed class Entity
    {
        public required HitObject HitObject;
        public bool Judged;
        public HitResult Type;
        public bool IsHit => Judged && Type.IsHit();
    }

    private sealed class Obj
    {
        public required int Column;
        public int Index;
        public required Entity Self;
        public Entity? Head, Tail, Body;          // hold notes only
        public bool Holding;                       // HoldNoteJudgementResult.IsHolding(now)
        public bool IsHoldingBindable;             // DrawableHoldNote.isHolding (refreshed in Update)
        public bool IsHold => Head != null;
        public double StartTime => Self.HitObject.StartTime;
        public double EndTime => Self.HitObject.GetEndTime();
        public bool AllJudged => Self.Judged && (!IsHold || (Head!.Judged && Tail!.Judged && Body!.Judged));
        public IEnumerable<Entity> Nested => IsHold ? new[] { Head!, Tail!, Body! } : Array.Empty<Entity>();
    }

    private readonly List<Obj>[] columns;
    private readonly List<Judged> results = new();
    private readonly List<Obj>[] pressReceivers;
    private double now;
    private (double offset, double scale, bool cap)? pendingOffset;
    private readonly int[] firstActive;
    private const double lookahead = 1000;

    public IReadOnlyList<Judged> Results => results;

    public LazerManiaReplaySimulator(IBeatmap playable, int columnCount)
    {
        columns = Enumerable.Range(0, columnCount).Select(_ => new List<Obj>()).ToArray();
        pressReceivers = Enumerable.Range(0, columnCount).Select(_ => new List<Obj>()).ToArray();

        foreach (var ho in playable.HitObjects.Cast<ManiaHitObject>())
        {
            var obj = new Obj { Column = ho.Column, Self = new Entity { HitObject = ho } };
            if (ho is HoldNote hold)
            {
                obj.Head = new Entity { HitObject = hold.Head };
                obj.Tail = new Entity { HitObject = hold.Tail };
                obj.Body = new Entity { HitObject = hold.Body };
            }
            columns[ho.Column].Add(obj);
        }

        foreach (var c in columns)
        {
            c.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            for (int i = 0; i < c.Count; i++) c[i].Index = i;
        }
        firstActive = new int[columnCount];
    }

    public void Run(IReadOnlyList<ManiaReplayFrame> frames, double gameplayStartTime, double hostStep = 50)
    {
        // Emulates GameplayClockContainer → FrameStabilityContainer → FramedReplayInputHandler.
        const double sixty = 1000.0 / 60;
        int frameIndex = -1;
        var lastPressed = new List<ManiaAction>();
        double manual = double.NaN;
        double host = Math.Min(gameplayStartTime, 0) - 2000;
        double end = frames.Count > 0 ? frames[^1].Time : 0;
        foreach (var c in columns) if (c.Count > 0) end = Math.Max(end, c[^1].EndTime);
        end += 5000;

        double frameTime(int i) => i < 0 ? double.NegativeInfinity : i >= frames.Count ? double.PositiveInfinity : frames[i].Time;

        while (host <= end)
        {
            while (true)
            {
                double proposed = host;
                if (double.IsNaN(manual)) manual = proposed;                       // firstConsumption
                else if (manual < gameplayStartTime) proposed = Math.Min(gameplayStartTime, proposed);
                else if (Math.Abs(manual - proposed) > sixty * 1.2) proposed = Math.Min(proposed, manual + sixty);

                // SetFrameFromTime: advance at most one frame and land exactly on it.
                if (frameTime(frameIndex + 1) <= proposed) { proposed = frameTime(frameIndex + 1); frameIndex++; }
                proposed = Math.Clamp(proposed, frameTime(frameIndex), frameTime(frameIndex + 1));

                manual = proposed;
                step(manual, frameIndex >= 0 ? frames[frameIndex].Actions : new List<ManiaAction>(), lastPressed);
                if (Math.Abs(manual - host) <= 0) break;                           // caught up
            }
            host += hostStep;
        }
    }

    private void step(double time, List<ManiaAction> pressed, List<ManiaAction> lastPressed)
    {
        now = time;

        // ReplayState<T>.Apply → RulesetInputManager: releases first, then presses.
        ManiaAction[] released = [], newlyPressed = [];
        if (lastPressed.Count == 0) newlyPressed = pressed.ToArray();
        else if (pressed.Count == 0) released = lastPressed.ToArray();
        else if (!lastPressed.SequenceEqual(pressed))
        {
            released = lastPressed.Except(pressed).ToArray();
            newlyPressed = pressed.Except(lastPressed).ToArray();
        }
        lastPressed.Clear();
        lastPressed.AddRange(pressed);

        foreach (var a in released) onReleased((int)a);
        foreach (var a in newlyPressed) onPressed((int)a);

        // Hit object updates: per column, later objects update first (HitObjectContainer ordering);
        // nested (head, tail, body) before the parent's UpdateAfterChildren.
        for (int c = 0; c < columns.Length; c++)
        {
            var column = columns[c];
            while (firstActive[c] < column.Count && column[firstActive[c]].AllJudged) firstActive[c]++;
            int last = firstActive[c];
            while (last < column.Count && column[last].StartTime - lookahead <= time) last++;
            for (int i = last - 1; i >= firstActive[c]; i--)
            {
                var o = column[i];
                if (o.AllJudged) continue;
                if (o.IsHold) updateHold(o); else updateNote(o.Self, 1, o);
            }
        }
    }

    private bool isHittable(Obj o, double time)
    {
        // OrderedHitPolicy.IsHittable: next object in the column must not have started.
        var list = columns[o.Column];
        return o.Index + 1 >= list.Count || time < list[o.Index + 1].StartTime;
    }

    private static HitWindows windows(Entity e) => e.HitObject.HitWindows;

    private void onPressed(int column)
    {
        var receivers = pressReceivers[column];
        receivers.Clear();
        var list = columns[column];
        for (int i = firstActive[column]; i < list.Count && list[i].StartTime - lookahead <= now; i++)   // earlier objects handle input first
        {
            var o = list[i];
            if (o.AllJudged) continue;
            receivers.Add(o);
            if (o.IsHold ? holdPressed(o) : notePressed(o)) break;
        }
    }

    private void onReleased(int column)
    {
        foreach (var o in pressReceivers[column])
            if (o.IsHold) holdReleased(o);
        pressReceivers[column].Clear();
    }

    private bool notePressed(Obj o)
    {
        if (!isHittable(o, now)) return false;
        if (o.Self.Judged) return false;
        noteCheckForResult(o, o.Self, true, now - o.Self.HitObject.GetEndTime(), 1, capped: r => r);
        return o.Self.Judged;
    }

    private bool holdPressed(Obj o)
    {
        if (o.AllJudged) return false;
        if (!isHittable(o, now)) return false;
        var tail = o.Tail!.HitObject;
        if (now > tail.StartTime && !tail.HitWindows.CanBeHit(now - tail.StartTime)) return false;

        // beginHoldAt
        if (now - o.Head!.HitObject.StartTime >= -windows(o.Head).WindowFor(HitResult.Miss))
            o.Holding = true;

        if (o.Head.Judged) return false;
        noteCheckForResult(o, o.Head, true, now - o.Head.HitObject.StartTime, 1, capped: r => r);
        return o.Head.Judged;
    }

    private void holdReleased(Obj o)
    {
        if (o.AllJudged) return;
        if (!o.IsHoldingBindable) return;
        if (!o.Tail!.Judged)
            noteCheckForResult(o, o.Tail, true, now - o.Tail.HitObject.StartTime, TailNote.RELEASE_WINDOW_LENIENCE, capped: tailCap(o));
        triggerBody(o, o.Tail.IsHit);
        o.Holding = false;
    }

    private Func<HitResult, HitResult> tailCap(Obj o) => r =>
    {
        bool hasComboBreak = !o.Head!.IsHit || (o.Body!.Judged && !o.Body.IsHit);
        return r > HitResult.Meh && hasComboBreak ? HitResult.Meh : r;
    };

    private void updateNote(Entity e, double timeOffsetScale, Obj? owner = null)
    {
        if (e.Judged) return;
        double offset = (now - e.HitObject.GetEndTime()) / timeOffsetScale;
        if (!windows(e).CanBeHit(offset))
            apply(owner!, e, e.HitObject.Judgement.MinResult);
    }

    private void updateHold(Obj o)
    {
        // DrawableHoldNote.Update (before children): refresh isHolding.
        o.IsHoldingBindable = o.Holding;
        // Children: head, tail (DrawableNote auto-miss), body (no auto-judgement).
        updateNote(o.Head!, 1, o);
        updateNote(o.Tail!, TailNote.RELEASE_WINDOW_LENIENCE, o);
        // Parent UpdateAfterChildren → CheckForResult.
        if (o.Self.Judged) return;
        if (o.Tail!.Judged)
        {
            if (o.Tail.IsHit) apply(o, o.Self, o.Self.HitObject.Judgement.MaxResult);
            else { apply(o, o.Self, o.Self.HitObject.Judgement.MinResult); o.Holding = false; }
            if (!o.Body!.Judged) triggerBody(o, o.Tail.IsHit);
            o.Holding = false;
        }
    }

    private void triggerBody(Obj o, bool hit)
    {
        if (o.Body!.Judged) return;
        apply(o, o.Body, hit ? o.Body.HitObject.Judgement.MaxResult : o.Body.HitObject.Judgement.MinResult);
    }

    private void noteCheckForResult(Obj owner, Entity e, bool userTriggered, double offset, double scale, Func<HitResult, HitResult> capped)
    {
        offset /= scale;
        if (!userTriggered)
        {
            if (!windows(e).CanBeHit(offset)) apply(owner, e, e.HitObject.Judgement.MinResult);
            return;
        }
        var result = windows(e).ResultFor(offset);
        if (result == HitResult.None) return;
        pendingOffset = (offset * scale, scale, capped(HitResult.Perfect) != HitResult.Perfect);
        apply(owner, e, capped(result));
        pendingOffset = null;
    }

    private void apply(Obj owner, Entity e, HitResult type)
    {
        if (e.Judged) return;
        e.Judged = true;
        e.Type = type;
        results.Add(pendingOffset is { } po ? new Judged(now, e.HitObject, type, po.offset, po.scale, po.cap) : new Judged(now, e.HitObject, type));
        pendingOffset = null;
        if (type.IsHit()) handleHit(owner.Column, e.HitObject.StartTime);
    }

    private void handleHit(int column, double targetTime)
    {
        // OrderedHitPolicy.HandleHit: force-miss everything (incl. nested) that ends before the hit object starts.
        var list = columns[column];
        for (int i = firstActive[column]; i < list.Count; i++)
        {
            var o = list[i];
            if (o.EndTime >= targetTime) break;
            if (!o.Self.Judged)
            {
                apply(o, o.Self, o.Self.HitObject.Judgement.MinResult);
                if (o.IsHold) o.Holding = false;
            }
            foreach (var n in o.Nested)
            {
                if (n.HitObject.GetEndTime() >= targetTime) break;
                if (!n.Judged) apply(o, n, n.HitObject.Judgement.MinResult);
            }
        }
    }
}
