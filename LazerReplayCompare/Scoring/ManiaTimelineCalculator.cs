using System.Globalization;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public sealed class ManiaTimelineCalculator
{
    public List<ReplayTimelineFrame> Build(
        Score score,
        string beatmapPath,
        double rate = 1.0,
        double scoreMultiplier = 1.0,
        CorrectionMode correctionMode = CorrectionMode.Corrected)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("A .osu beatmap path is required to simulate mania timelines.");

        var beatmap = ManiaBeatmap.Parse(beatmapPath);
        if (beatmap.Mode != 3)
            throw new NotSupportedException("The replay does not contain score headers, and only osu!mania timeline simulation is currently supported.");

        var safeRate = rate > 0 ? rate : 1.0;
        var keyPairs = ManiaReplayInputExtractor.ExtractKeyPairs(score, beatmap.Columns, ModUtility.HasMirrorMod(score));
        var adjustedOverallDifficulty = ModUtility.GetAdjustedOverallDifficulty(score, beatmap.OverallDifficulty);
        var judgements = JudgeRaw(beatmap, keyPairs, safeRate, adjustedOverallDifficulty);

        if (judgements.Count == 0)
            return new List<ReplayTimelineFrame>();

        var finalJudgements = correctionMode == CorrectionMode.Corrected
            ? JudgementCorrectionService.CorrectJudgementDistribution(judgements, score, scoreMultiplier)
            : judgements;

        return ManiaScoreCalculator.ScoreJudgements(finalJudgements, scoreMultiplier);
    }

    private static List<ManiaJudgement> JudgeRaw(ManiaBeatmap beatmap, (double press, double release)[][] keyPairs, double rate, double overallDifficulty)
    {
        var windows = ManiaWindows.ForDifficulty(overallDifficulty, rate);
        var judgements = new List<ManiaJudgement>();
        var noteStates = CreateNoteStates(beatmap);
        var activeHoldByColumn = new NoteState?[beatmap.Columns];
        var pressCursorByColumn = new int[beatmap.Columns];
        var inputEvents = CreateRawInputEvents(keyPairs, noteStates);

        foreach (var keyEvent in inputEvents)
        {
            var columnNotes = noteStates[keyEvent.Column];
            ExpirePressQueueColumn(columnNotes, ref pressCursorByColumn[keyEvent.Column], judgements, keyEvent.Time, windows, inputEvents, activeHoldByColumn);
            ExpireTailColumn(columnNotes, judgements, keyEvent.Time, windows);

            if (keyEvent.IsPress)
            {
                if (keyEvent.Consumed)
                    continue;

                var note = GetPressQueueFront(columnNotes, ref pressCursorByColumn[keyEvent.Column]);
                if (note == null)
                    continue;

                if (TryConsumeFrontWithEarlierPress(columnNotes, note, judgements, windows, inputEvents, keyEvent, activeHoldByColumn))
                {
                    pressCursorByColumn[keyEvent.Column]++;
                    note = GetPressQueueFront(columnNotes, ref pressCursorByColumn[keyEvent.Column]);
                    if (note == null)
                        continue;
                }

                note = SelectPressCandidate(columnNotes, pressCursorByColumn[keyEvent.Column], note, keyEvent, windows, inputEvents);
                var offset = keyEvent.Time - note.Note.StartTime;
                if (offset < -windows.Miss || Math.Abs(offset) > windows.Miss)
                    continue;

                var result = windows.ResultFor(offset);
                keyEvent.Consumed = true;
                keyEvent.ConsumedByObjectId = note.Id;

                note.HeadJudged = true;
                note.HeadHit = result != HitResult.Miss;

                judgements.Add(new ManiaJudgement(
                    Time: keyEvent.Time,
                    Column: keyEvent.Column,
                    Result: result,
                    ObjectTime: note.Note.StartTime,
                    Kind: note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note));

                if (note.Note.EndTime.HasValue)
                {
                    note.PressTime = keyEvent.Time;
                    note.IsHolding = note.HeadHit;
                    if (note.HeadHit)
                        activeHoldByColumn[keyEvent.Column] = note;
                }

                AdvancePressCursor(columnNotes, ref pressCursorByColumn[keyEvent.Column]);
            }
            else
            {
                if (keyEvent.Consumed)
                    continue;

                var note = activeHoldByColumn[keyEvent.Column] ??
                    FindTailReleaseCandidate(noteStates[keyEvent.Column], keyEvent.Time, windows);
                if (note == null || note.TailJudged || !note.Note.EndTime.HasValue)
                    continue;

                if (keyEvent.Time < note.Note.EndTime.Value - windows.Miss * TailReleaseLenience)
                {
                    note.BodyBroken = true;
                    note.IsHolding = false;
                    if (ReferenceEquals(activeHoldByColumn[keyEvent.Column], note))
                        activeHoldByColumn[keyEvent.Column] = null;
                    continue;
                }

                if (keyEvent.Time <= note.Note.EndTime.Value + windows.Miss * TailReleaseLenience)
                {
                    ApplyTailJudgement(note, judgements, keyEvent.Time, windows);
                    keyEvent.Consumed = true;
                    keyEvent.ConsumedByObjectId = note.Id;
                }

                if (ReferenceEquals(activeHoldByColumn[keyEvent.Column], note))
                    activeHoldByColumn[keyEvent.Column] = null;
            }
        }

        foreach (var activeHold in activeHoldByColumn)
        {
            if (activeHold?.Note.EndTime.HasValue == true && !activeHold.TailJudged)
                ApplyTailJudgement(activeHold, judgements, activeHold.Note.EndTime.Value, windows);
        }

        for (var column = 0; column < noteStates.Length; column++)
        {
            ExpirePressQueueColumn(noteStates[column], ref pressCursorByColumn[column], judgements, double.PositiveInfinity, windows, inputEvents, activeHoldByColumn);
            ExpireTailColumn(noteStates[column], judgements, double.PositiveInfinity, windows);
        }

        return judgements
            .OrderBy(j => j.Time)
            .ThenBy(j => j.Column)
            .ToList();
    }

    private static List<ManiaJudgement> Judge(ManiaBeatmap beatmap, (double press, double release)[][] keyPairs, double rate)
    {
        var windows = ManiaWindows.ForDifficulty(beatmap.OverallDifficulty, rate);
        var judgements = new List<ManiaJudgement>();
        var noteStates = CreateNoteStates(beatmap);
        var activeHoldByColumn = new NoteState?[beatmap.Columns];

        foreach (var keyEvent in CreateKeyEvents(keyPairs))
        {
            ProcessPassiveMisses(noteStates, judgements, keyEvent.Time, windows);

            if (keyEvent.IsPress)
            {
                var note = FindBestPressCandidate(noteStates[keyEvent.Column], keyEvent.Time, keyEvent.NextPressTime, windows);
                if (note == null)
                    continue;

                if (!note.HeadJudged)
                {
                    var offset = keyEvent.Time - note.Note.StartTime;
                    var result = Math.Abs(offset) <= windows.Miss ? windows.ResultFor(offset) : HitResult.Miss;

                    note.HeadJudged = true;
                    note.HeadHit = result != HitResult.Miss;

                    judgements.Add(new ManiaJudgement(
                        Time: keyEvent.Time,
                        Column: keyEvent.Column,
                        Result: result,
                        ObjectTime: note.Note.StartTime,
                        Kind: note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note));
                }

                if (note.Note.EndTime.HasValue)
                {
                    note.PressTime = keyEvent.Time;
                    activeHoldByColumn[keyEvent.Column] = note;
                }
            }
            else
            {
                var note = activeHoldByColumn[keyEvent.Column] ??
                    FindTailReleaseCandidate(noteStates[keyEvent.Column], keyEvent.Time, windows);
                if (note == null || note.TailJudged || !note.Note.EndTime.HasValue)
                    continue;

                if (keyEvent.Time >= note.Note.EndTime.Value - windows.Miss * TailReleaseLenience)
                    ApplyTailJudgement(note, judgements, keyEvent.Time, windows);

                activeHoldByColumn[keyEvent.Column] = null;
            }
        }

        foreach (var activeHold in activeHoldByColumn)
        {
            if (activeHold?.Note.EndTime.HasValue == true && !activeHold.TailJudged)
                ApplyTailJudgement(activeHold, judgements, activeHold.Note.EndTime.Value, windows);
        }

        ProcessPassiveMisses(noteStates, judgements, double.PositiveInfinity, windows);

        return judgements
            .OrderBy(j => j.Time)
            .ThenBy(j => j.Column)
            .ToList();
    }

    private static List<NoteState>[] CreateNoteStates(ManiaBeatmap beatmap)
    {
        var result = new List<NoteState>[beatmap.Columns];
        for (var c = 0; c < result.Length; c++)
            result[c] = new List<NoteState>();

        foreach (var group in beatmap.Notes.GroupBy(note => note.Column))
        {
            var notes = group.OrderBy(note => note.StartTime).ToArray();
            for (var i = 0; i < notes.Length; i++)
                result[group.Key].Add(new NoteState(notes[i]));
        }

        return result;
    }

    private static List<KeyEvent> CreateKeyEvents((double press, double release)[][] keyPairs)
    {
        var events = new List<KeyEvent>();

        for (var c = 0; c < keyPairs.Length; c++)
        {
            foreach (var pair in keyPairs[c])
            {
                events.Add(new KeyEvent(pair.press, c, true, null));
                if (!double.IsPositiveInfinity(pair.release))
                    events.Add(new KeyEvent(pair.release, c, false, null));
            }
        }

        var ordered = events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.IsPress ? 1 : 0)
            .ToList();

        var nextPressByColumn = new double?[keyPairs.Length];
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var current = ordered[i];
            ordered[i] = current with { NextPressTime = nextPressByColumn[current.Column] };
            if (current.IsPress)
                nextPressByColumn[current.Column] = current.Time;
        }

        return ordered;
    }

    private static List<LegacyKeyEvent> CreateLegacyKeyEvents((double press, double release)[][] keyPairs)
    {
        var events = new List<LegacyKeyEvent>();

        for (var c = 0; c < keyPairs.Length; c++)
        {
            foreach (var pair in keyPairs[c])
            {
                events.Add(new LegacyKeyEvent(pair.press, c, true));
                if (!double.IsPositiveInfinity(pair.release))
                    events.Add(new LegacyKeyEvent(pair.release, c, false));
            }
        }

        return events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.IsPress ? 1 : 0)
            .ToList();
    }

    private static List<RawInputEvent> CreateRawInputEvents((double press, double release)[][] keyPairs, IReadOnlyList<NoteState>[] noteStates)
    {
        var events = new List<RawInputEvent>();
        var id = 0;

        for (var c = 0; c < keyPairs.Length; c++)
        {
            foreach (var pair in keyPairs[c])
            {
                events.Add(new RawInputEvent(++id, pair.press, c, true));
                if (!double.IsPositiveInfinity(pair.release))
                    events.Add(new RawInputEvent(++id, pair.release, c, false));
            }
        }

        return events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.IsPress ? 1 : 0)
            .ThenBy(e => GetInputTieBreakDistance(e, noteStates))
            .ThenBy(e => e.Column)
            .ToList();
    }

    private static double GetInputTieBreakDistance(RawInputEvent inputEvent, IReadOnlyList<NoteState>[] noteStates)
    {
        if (inputEvent.Column < 0 || inputEvent.Column >= noteStates.Length)
            return double.PositiveInfinity;

        var notes = noteStates[inputEvent.Column];
        if (inputEvent.IsPress)
        {
            var note = notes.FirstOrDefault(note => !note.HeadJudged);
            return note == null ? double.PositiveInfinity : Math.Abs(inputEvent.Time - note.Note.StartTime);
        }

        var tail = notes
            .Where(note => note.Note.EndTime.HasValue && !note.TailJudged)
            .OrderBy(note => Math.Abs(inputEvent.Time - note.Note.EndTime!.Value))
            .FirstOrDefault();

        return tail == null ? double.PositiveInfinity : Math.Abs(inputEvent.Time - tail.Note.EndTime!.Value);
    }

    private static NoteState? FindBestPressCandidate(IReadOnlyList<NoteState> notes, double pressTime, double? nextPressTime, ManiaWindows windows)
    {
        return notes
            .Where(note =>
            {
                if (note.HeadJudged)
                    return false;

                var offset = pressTime - note.Note.StartTime;
                if (Math.Abs(offset) <= windows.Miss)
                    return true;

                return note.Note.EndTime.HasValue &&
                    pressTime > note.Note.StartTime + windows.Miss &&
                    pressTime <= note.Note.EndTime.Value + windows.Meh;
            })
            .OrderBy(note => LocalLookaheadCost(notes, note, pressTime, nextPressTime, windows))
            .FirstOrDefault();
    }

    private static NoteState? FindFrontPressCandidate(IReadOnlyList<NoteState> notes, double pressTime, ManiaWindows windows)
    {
        var note = notes.FirstOrDefault(note => !note.HeadJudged);
        if (note == null)
            return null;

        var offset = pressTime - note.Note.StartTime;
        if (offset < -windows.Miss)
            return null;

        return Math.Abs(offset) <= windows.Miss ? note : null;
    }

    private static NoteState? GetPressQueueFront(IReadOnlyList<NoteState> notes, ref int cursor)
    {
        while (cursor < notes.Count && notes[cursor].HeadJudged)
            cursor++;

        return cursor < notes.Count ? notes[cursor] : null;
    }

    private static void AdvancePressCursor(IReadOnlyList<NoteState> notes, ref int cursor)
    {
        while (cursor < notes.Count && notes[cursor].HeadJudged)
            cursor++;
    }

    private static NoteState SelectPressCandidate(
        IReadOnlyList<NoteState> notes,
        int cursor,
        NoteState front,
        RawInputEvent press,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents)
    {
        var pressTime = press.Time;
        var frontOffset = pressTime - front.Note.StartTime;
        if (Math.Abs(frontOffset) <= windows.Great)
            return front;

        var profile = GetPressMatchingProfile(notes, cursor, pressTime, windows);
        var best = front;
        var bestCost = CandidateCost(front, pressTime, 0, profile);
        var checkedCandidates = 0;

        for (var i = cursor; i < notes.Count && checkedCandidates < PressLookaheadCandidateCount; i++)
        {
            var candidate = notes[i];
            if (candidate.HeadJudged)
                continue;

            checkedCandidates++;
            var offset = pressTime - candidate.Note.StartTime;
            if (offset < -windows.Miss)
                break;
            if (Math.Abs(offset) > windows.Miss)
                continue;

            var distanceFromFront = checkedCandidates - 1;
            var cost = CandidateCost(candidate, pressTime, distanceFromFront, profile);
            if (cost < bestCost)
            {
                best = candidate;
                bestCost = cost;
            }
        }

        if (ReferenceEquals(best, front))
            return front;

        var bestOffset = pressTime - best.Note.StartTime;
        if (Math.Abs(bestOffset) > windows.Great)
            return front;

        var switchMargin = profile.SwitchMargin;
        if (!HasGoodAlternativePressForFront(front, press, windows, inputEvents, Math.Abs(frontOffset)))
            switchMargin += profile.OrphanPenalty;

        if (Math.Abs(bestOffset) + switchMargin >= Math.Abs(frontOffset))
            return front;

        return best;
    }

    private static double CandidateCost(NoteState note, double pressTime, int distanceFromFront, PressMatchingProfile profile)
    {
        return Math.Abs(pressTime - note.Note.StartTime) + distanceFromFront * profile.StepPenalty;
    }

    private static bool HasGoodAlternativePressForFront(
        NoteState front,
        RawInputEvent currentPress,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        double currentFrontOffsetAbs)
    {
        var maxAcceptableOffset = Math.Min(windows.Good, currentFrontOffsetAbs + AlternativePressQualitySlack);

        return inputEvents.Any(e =>
            e.IsPress &&
            !e.Consumed &&
            e.Id != currentPress.Id &&
            e.Column == currentPress.Column &&
            Math.Abs(e.Time - front.Note.StartTime) <= maxAcceptableOffset);
    }

    private static PressMatchingProfile GetPressMatchingProfile(IReadOnlyList<NoteState> notes, int cursor, double pressTime, ManiaWindows windows)
    {
        var nearby = 0;
        var first = double.PositiveInfinity;
        var last = double.NegativeInfinity;

        for (var i = Math.Max(0, cursor - 1); i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.HeadJudged)
                continue;

            var distance = Math.Abs(note.Note.StartTime - pressTime);
            if (distance > DensityWindow)
            {
                if (note.Note.StartTime > pressTime)
                    break;

                continue;
            }

            nearby++;
            first = Math.Min(first, note.Note.StartTime);
            last = Math.Max(last, note.Note.StartTime);
        }

        var span = double.IsPositiveInfinity(first) ? DensityWindow : Math.Max(1, last - first);
        var notesPerSecond = nearby / span * 1000;
        var density = Math.Clamp((notesPerSecond - 12) / 18, 0, 1);
        var odPressure = Math.Clamp((windows.Miss - windows.Meh) / 40, 0, 1);
        var pressure = Math.Clamp(density * 0.85 + odPressure * 0.15, 0, 1);

        return new PressMatchingProfile(
            StepPenalty: PressLookaheadStepPenalty + pressure * 10,
            SwitchMargin: PressLookaheadSwitchMargin + pressure * 24,
            FrontEarlierPreference: FrontEarlierPressPreference + pressure * 5,
            OrphanPenalty: OrphanFrontSwitchPenalty + pressure * 8);
    }

    private static void ExpirePressQueueColumn(
        IReadOnlyList<NoteState> notes,
        ref int cursor,
        List<ManiaJudgement> judgements,
        double time,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        NoteState?[] activeHoldByColumn)
    {
        while (cursor < notes.Count)
        {
            var note = notes[cursor];
            if (note.HeadJudged)
            {
                cursor++;
                continue;
            }

            if (time <= note.Note.StartTime + windows.Miss)
                break;

            if (TryRescueWithUnconsumedPress(notes, note, judgements, windows, inputEvents, activeHoldByColumn))
            {
                cursor++;
                continue;
            }

            note.HeadJudged = true;
            note.HeadHit = false;
            note.IsHolding = false;
            judgements.Add(new ManiaJudgement(
                Time: note.Note.StartTime + windows.Miss,
                Column: note.Note.Column,
                Result: HitResult.Miss,
                ObjectTime: note.Note.StartTime,
                Kind: note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note));
            cursor++;
        }
    }

    private static bool TryConsumeFrontWithEarlierPress(
        IReadOnlyList<NoteState> notes,
        NoteState note,
        List<ManiaJudgement> judgements,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        RawInputEvent currentPress,
        NoteState?[] activeHoldByColumn)
    {
        if (note.HeadJudged || HasEarlierUnjudgedPressObject(notes, note))
            return false;

        var currentOffset = currentPress.Time - note.Note.StartTime;
        if (currentOffset <= 0 || Math.Abs(currentOffset) > windows.Miss)
            return false;

        var earlierPress = inputEvents
            .Where(e => e.IsPress &&
                !e.Consumed &&
                e.Column == note.Note.Column &&
                e.Time < currentPress.Time &&
                Math.Abs(e.Time - note.Note.StartTime) <= windows.Miss)
            .OrderBy(e => Math.Abs(e.Time - note.Note.StartTime))
            .ThenByDescending(e => e.Time)
            .FirstOrDefault();

        if (earlierPress == null)
            return false;

        var earlierOffset = earlierPress.Time - note.Note.StartTime;
        var profile = GetPressMatchingProfile(notes, IndexOfNote(notes, note), currentPress.Time, windows);
        if (Math.Abs(earlierOffset) > Math.Abs(currentOffset) - profile.FrontEarlierPreference)
            return false;

        var result = windows.ResultFor(earlierOffset);
        earlierPress.Consumed = true;
        earlierPress.ConsumedByObjectId = note.Id;
        note.HeadJudged = true;
        note.HeadHit = result != HitResult.Miss;

        judgements.Add(new ManiaJudgement(
            Time: earlierPress.Time,
            Column: note.Note.Column,
            Result: result,
            ObjectTime: note.Note.StartTime,
            Kind: note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note));

        if (note.Note.EndTime.HasValue)
        {
            note.PressTime = earlierPress.Time;
            note.IsHolding = note.HeadHit;
            if (note.HeadHit)
                activeHoldByColumn[note.Note.Column] = note;
        }

        return true;
    }

    private static bool TryRescueWithUnconsumedPress(
        IReadOnlyList<NoteState> notes,
        NoteState note,
        List<ManiaJudgement> judgements,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        NoteState?[] activeHoldByColumn)
    {
        if (note.HeadJudged || HasEarlierUnjudgedPressObject(notes, note))
            return false;

        var press = inputEvents
            .Where(e => e.IsPress &&
                !e.Consumed &&
                e.Column == note.Note.Column &&
                Math.Abs(e.Time - note.Note.StartTime) <= windows.Miss &&
                !IsPressBetterFitForLaterObject(notes, note, e, windows))
            .OrderBy(e => Math.Abs(e.Time - note.Note.StartTime))
            .ThenBy(e => e.Time)
            .FirstOrDefault();

        if (press == null)
            return false;

        var offset = press.Time - note.Note.StartTime;
        var result = windows.ResultFor(offset);

        press.Consumed = true;
        press.ConsumedByObjectId = note.Id;
        note.HeadJudged = true;
        note.HeadHit = result != HitResult.Miss;

        judgements.Add(new ManiaJudgement(
            Time: press.Time,
            Column: note.Note.Column,
            Result: result,
            ObjectTime: note.Note.StartTime,
            Kind: note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note));

        if (note.Note.EndTime.HasValue)
        {
            note.PressTime = press.Time;
            note.IsHolding = note.HeadHit;
            if (note.HeadHit)
                activeHoldByColumn[note.Note.Column] = note;
        }

        return true;
    }

    private static bool IsPressBetterFitForLaterObject(IReadOnlyList<NoteState> notes, NoteState note, RawInputEvent press, ManiaWindows windows)
    {
        var currentOffset = Math.Abs(press.Time - note.Note.StartTime);
        var index = IndexOfNote(notes, note);
        if (index < 0)
            return false;

        for (var i = index + 1; i < notes.Count && i <= index + RescueLaterObjectCheckCount; i++)
        {
            var later = notes[i];
            if (later.HeadJudged)
                continue;

            var laterOffset = Math.Abs(press.Time - later.Note.StartTime);
            if (laterOffset > windows.Miss)
            {
                if (later.Note.StartTime > press.Time + windows.Miss)
                    break;

                continue;
            }

            if (laterOffset + RescueLaterObjectMargin < currentOffset)
                return true;
        }

        return false;
    }

    private static int IndexOfNote(IReadOnlyList<NoteState> notes, NoteState note)
    {
        for (var i = 0; i < notes.Count; i++)
        {
            if (ReferenceEquals(notes[i], note))
                return i;
        }

        return -1;
    }

    private static bool HasEarlierUnjudgedPressObject(IReadOnlyList<NoteState> notes, NoteState note)
    {
        foreach (var candidate in notes)
        {
            if (ReferenceEquals(candidate, note))
                return false;

            if (!candidate.HeadJudged)
                return true;
        }

        return false;
    }

    private static void ExpireTailColumn(IReadOnlyList<NoteState> notes, List<ManiaJudgement> judgements, double time, ManiaWindows windows)
    {
        foreach (var note in notes)
        {
            if (!note.Note.EndTime.HasValue || note.TailJudged)
                continue;

            if (time > note.Note.EndTime.Value + windows.Miss * TailReleaseLenience)
                ApplyTailJudgement(note, judgements, note.Note.EndTime.Value + windows.Miss * TailReleaseLenience, windows, forceMiss: true);
        }
    }

    private static NoteState? FindLegacyPressCandidate(IReadOnlyList<NoteState> notes, double pressTime, ManiaWindows windows)
    {
        return notes
            .Where(note =>
            {
                if (note.HeadJudged)
                    return false;

                var offset = pressTime - note.Note.StartTime;
                if (Math.Abs(offset) <= windows.Miss)
                    return true;

                return note.Note.EndTime.HasValue &&
                    pressTime > note.Note.StartTime + windows.Miss &&
                    pressTime <= note.Note.EndTime.Value + windows.Meh;
            })
            .OrderBy(note => DistanceToCandidate(note, pressTime))
            .FirstOrDefault();
    }

    private static NoteState? FindTailReleaseCandidate(IReadOnlyList<NoteState> notes, double releaseTime, ManiaWindows windows)
    {
        return notes
            .Where(note =>
            {
                if (!note.Note.EndTime.HasValue || note.TailJudged)
                    return false;

                var endTime = note.Note.EndTime.Value;
                if (releaseTime < endTime - windows.Miss * TailReleaseLenience ||
                    releaseTime > endTime + windows.Miss * TailReleaseLenience)
                    return false;

                return note.HeadJudged || note.PressTime.HasValue;
            })
            .Where(note => !note.BodyBroken || releaseTime >= note.Note.EndTime!.Value - windows.Miss * TailReleaseLenience)
            .OrderBy(note => note.HeadHit ? 0 : 1)
            .ThenBy(note => Math.Abs(releaseTime - note.Note.EndTime!.Value))
            .FirstOrDefault();
    }

    private static double LocalLookaheadCost(IReadOnlyList<NoteState> notes, NoteState candidate, double pressTime, double? nextPressTime, ManiaWindows windows)
    {
        var cost = DistanceToCandidate(candidate, pressTime);
        if (!nextPressTime.HasValue)
            return cost;

        var nextNote = notes
            .Where(note => !ReferenceEquals(note, candidate) && !note.HeadJudged)
            .OrderBy(note => note.Note.StartTime)
            .FirstOrDefault(note => note.Note.StartTime >= candidate.Note.StartTime);

        if (nextNote == null)
            return cost;

        var nextDistance = Math.Abs(nextPressTime.Value - nextNote.Note.StartTime);
        return cost + Math.Min(nextDistance, windows.Miss * 2) * LocalLookaheadWeight;
    }

    private static double DistanceToCandidate(NoteState note, double pressTime)
    {
        if (note.HeadJudged && note.Note.EndTime.HasValue)
            return Math.Abs(pressTime - note.Note.EndTime.Value);

        return Math.Abs(pressTime - note.Note.StartTime);
    }

    private static void ProcessPassiveMisses(IReadOnlyList<NoteState>[] noteStates, List<ManiaJudgement> judgements, double time, ManiaWindows windows)
    {
        foreach (var notes in noteStates)
        {
            foreach (var note in notes)
            {
                if (!note.HeadJudged && time > note.Note.StartTime + windows.Miss)
                {
                    note.HeadJudged = true;
                    note.HeadHit = false;
                    note.IsHolding = false;
                    judgements.Add(new ManiaJudgement(note.Note.StartTime + windows.Miss, note.Note.Column, HitResult.Miss, note.Note.StartTime, note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note));
                }

                if (note.Note.EndTime.HasValue && !note.TailJudged && time > note.Note.EndTime.Value + windows.Miss * TailReleaseLenience)
                    ApplyTailJudgement(note, judgements, note.Note.EndTime.Value + windows.Miss * TailReleaseLenience, windows, forceMiss: true);
            }
        }
    }

    private static void ApplyTailJudgement(NoteState note, List<ManiaJudgement> judgements, double releaseTime, ManiaWindows windows, bool forceMiss = false)
    {
        var endTime = note.Note.EndTime!.Value;
        var result = forceMiss ? HitResult.Miss : windows.ResultForTail(releaseTime - endTime);

        if (result > HitResult.Meh && (!note.HeadHit || note.BodyBroken))
            result = HitResult.Meh;

        note.TailJudged = true;
        note.TailConsumed = true;
        note.IsHolding = false;
        judgements.Add(new ManiaJudgement(releaseTime, note.Note.Column, result, endTime, JudgementKind.HoldTail));
    }

    private const double TailReleaseLenience = 1.5;
    private const double LocalLookaheadWeight = 0.25;
    private const double FrontEarlierPressPreference = 8;
    private const int PressLookaheadCandidateCount = 3;
    private const double PressLookaheadStepPenalty = 18;
    private const double PressLookaheadSwitchMargin = 78;
    private const double OrphanFrontSwitchPenalty = 5;
    private const double AlternativePressQualitySlack = 10;
    private const double DensityWindow = 450;
    private const int RescueLaterObjectCheckCount = 2;
    private const double RescueLaterObjectMargin = 12;

    private readonly record struct PressMatchingProfile(
        double StepPenalty,
        double SwitchMargin,
        double FrontEarlierPreference,
        double OrphanPenalty);

    private static List<ReplayTimelineFrame>? ScoreJudgementsWithLazerProcessor(IReadOnlyList<ManiaJudgement> judgements, Score score, string beatmapPath)
    {
        try
        {
            var ruleset = new ManiaRuleset();
            var processor = ruleset.CreateScoreProcessor();
            var working = new FlatWorkingBeatmap(beatmapPath);
            var playable = working.GetPlayableBeatmap(score.ScoreInfo.Ruleset, score.ScoreInfo.Mods);
            processor.ApplyBeatmap(playable);

            var hitObjects = EnumerateJudgeableObjects(playable).ToArray();
            if (hitObjects.Length < judgements.Count)
                return null;

            var frames = new List<ReplayTimelineFrame>(judgements.Count);
            var hits = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Miss"] = 0,
                ["Meh"] = 0,
                ["Ok"] = 0,
                ["Good"] = 0,
                ["Great"] = 0,
                ["Perfect"] = 0,
            };

            for (var i = 0; i < judgements.Count; i++)
            {
                var judgement = judgements[i];
                var hitObject = hitObjects[i];
                var result = new JudgementResult(hitObject, hitObject.Judgement)
                {
                    Type = judgement.Result,
                };

                processor.ApplyResult(result);

                var key = judgement.Result.ToString();
                hits[key] = hits.GetValueOrDefault(key) + 1;

                frames.Add(new ReplayTimelineFrame(
                    Time: judgement.Time,
                    Index: frames.Count + 1,
                    Score: processor.TotalScore.Value,
                    Combo: processor.Combo.Value,
                    MaxCombo: processor.HighestCombo.Value,
                    Accuracy: processor.Accuracy.Value,
                    Hits: new Dictionary<string, int>(hits, StringComparer.Ordinal)));
            }

            var finalFrame = frames.LastOrDefault();
            if (finalFrame == null)
                return null;

            // If the processor wasn't fed with real DrawableRuleset-produced results,
            // some current osu! builds keep accuracy at the default value. In that case
            // this path is not a faithful engine replay and must not be used.
            if (Math.Abs(finalFrame.Accuracy - score.ScoreInfo.Accuracy) > 0.0001)
                return null;

            return frames;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return null;
        }
    }

    private static IEnumerable<HitObject> EnumerateJudgeableObjects(IBeatmap beatmap)
    {
        return beatmap.HitObjects
            .SelectMany(Flatten)
            .Where(hitObject => hitObject.HitWindows != null || hitObject.NestedHitObjects.Count == 0)
            .OrderBy(hitObject => hitObject.StartTime);
    }

    private static IEnumerable<HitObject> Flatten(HitObject hitObject)
    {
        if (hitObject.NestedHitObjects.Count == 0)
        {
            yield return hitObject;
            yield break;
        }

        foreach (var nested in hitObject.NestedHitObjects)
        {
            foreach (var flattened in Flatten(nested))
                yield return flattened;
        }
    }

    private sealed record KeyEvent(double Time, int Column, bool IsPress, double? NextPressTime);
    private sealed record LegacyKeyEvent(double Time, int Column, bool IsPress);

    private sealed class RawInputEvent(int id, double time, int column, bool isPress)
    {
        public int Id { get; } = id;
        public double Time { get; } = time;
        public int Column { get; } = column;
        public bool IsPress { get; } = isPress;
        public bool Consumed { get; set; }
        public int? ConsumedByObjectId { get; set; }
    }

    private sealed class NoteState(ManiaNote note)
    {
        private static int nextId;

        public int Id { get; } = Interlocked.Increment(ref nextId);
        public ManiaNote Note { get; } = note;
        public bool HeadJudged { get; set; }
        public bool HeadHit { get; set; }
        public bool TailJudged { get; set; }
        public bool TailConsumed { get; set; }
        public bool IsHolding { get; set; }
        public bool BodyBroken { get; set; }
        public double? PressTime { get; set; }
    }

    private sealed record ManiaWindows(double Perfect, double Great, double Good, double Ok, double Meh, double Miss)
    {
        public static ManiaWindows ForDifficulty(double overallDifficulty, double speedMultiplier)
        {
            var multiplier = speedMultiplier > 0 ? speedMultiplier : 1.0;
            return new ManiaWindows(
                Perfect: Window(overallDifficulty, 22.4, 19.4, 13.9, multiplier),
                Great: Window(overallDifficulty, 64, 49, 34, multiplier),
                Good: Window(overallDifficulty, 97, 82, 67, multiplier),
                Ok: Window(overallDifficulty, 127, 112, 97, multiplier),
                Meh: Window(overallDifficulty, 151, 136, 121, multiplier),
                Miss: Window(overallDifficulty, 188, 173, 158, multiplier));
        }

        public HitResult ResultFor(double offset)
        {
            var absolute = Math.Abs(offset);
            if (absolute <= Perfect) return HitResult.Perfect;
            if (absolute <= Great) return HitResult.Great;
            if (absolute <= Good) return HitResult.Good;
            if (absolute <= Ok) return HitResult.Ok;
            if (absolute <= Meh) return HitResult.Meh;
            return HitResult.Miss;
        }

        // Tail windows are 1.5x more lenient than head windows.
        public HitResult ResultForTail(double offset)
        {
            var absolute = Math.Abs(offset);
            if (absolute <= Perfect * 1.5) return HitResult.Perfect;
            if (absolute <= Great  * 1.5) return HitResult.Great;
            if (absolute <= Good   * 1.5) return HitResult.Good;
            if (absolute <= Ok     * 1.5) return HitResult.Ok;
            if (absolute <= Meh    * 1.5) return HitResult.Meh;
            return HitResult.Miss;
        }

        private static double Window(double difficulty, double easy, double normal, double hard, double multiplier)
        {
            var value = difficulty > 5
                ? normal + (hard - normal) * (difficulty - 5) / 5
                : easy + (normal - easy) * difficulty / 5;

            return Math.Floor(value * multiplier) + 0.5;
        }
    }

    private sealed record ManiaNote(int Column, double StartTime, double? EndTime);

    private sealed class ManiaBeatmap
    {
        public int Mode { get; private init; }
        public int Columns { get; private init; }
        public double OverallDifficulty { get; private init; }
        public List<ManiaNote> Notes { get; } = new();

        public static ManiaBeatmap Parse(string beatmapPath)
        {
            var mode = 0;
            var columns = 4;
            var overallDifficulty = 5d;
            var notes = new List<ManiaNote>();
            var section = string.Empty;

            foreach (var rawLine in File.ReadLines(beatmapPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line;
                    continue;
                }

                if (section == "[General]")
                {
                    if (TryReadKeyValue(line, "Mode", out var value))
                        mode = ParseInt(value, mode);
                }
                else if (section == "[Difficulty]")
                {
                    if (TryReadKeyValue(line, "CircleSize", out var value))
                        columns = Math.Max(1, ParseInt(value, columns));
                    else if (TryReadKeyValue(line, "OverallDifficulty", out value))
                        overallDifficulty = ParseDouble(value, overallDifficulty);
                }
                else if (section == "[HitObjects]")
                {
                    var parts = line.Split(',');
                    if (parts.Length < 5)
                        continue;

                    var x = ParseInt(parts[0], 0);
                    var time = ParseDouble(parts[2], 0);
                    var type = ParseInt(parts[3], 0);
                    var isNote = (type & 1) != 0;
                    var isHold = (type & 128) != 0;
                    if (!isNote && !isHold)
                        continue;

                    double? endTime = null;
                    if (isHold && parts.Length > 5)
                        endTime = ParseDouble(parts[5].Split(':')[0], time);

                    var column = Math.Clamp((int)Math.Floor(x * columns / 512d), 0, columns - 1);
                    notes.Add(new ManiaNote(column, time, endTime));
                }
            }

            var beatmap = new ManiaBeatmap
            {
                Mode = mode,
                Columns = columns,
                OverallDifficulty = overallDifficulty,
            };

            beatmap.Notes.AddRange(notes.OrderBy(n => n.StartTime).ThenBy(n => n.Column));
            return beatmap;
        }

        private static bool TryReadKeyValue(string line, string key, out string value)
        {
            value = string.Empty;
            var prefix = key + ":";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            value = line[prefix.Length..].Trim();
            return true;
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }
    }
}
