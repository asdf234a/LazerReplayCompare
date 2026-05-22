using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

internal sealed class ManiaRawJudgeEngine
{
    public List<ManiaJudgement> Judge(
        ManiaBeatmap beatmap,
        (double press, double release)[][] keyPairs,
        double rate,
        double overallDifficulty)
    {
        var windows = ManiaWindows.ForDifficulty(overallDifficulty, rate);
        var judgements = new List<ManiaJudgement>();
        var noteStates = CreateNoteStates(beatmap);
        var activeHoldByColumn = new NoteState?[beatmap.Columns];
        var pressCursorByColumn = new int[beatmap.Columns];
        var inputEvents = ManiaInputEventBuilder.CreateRawInputEvents(keyPairs);

        foreach (var keyEvent in inputEvents)
        {
            var columnNotes = noteStates[keyEvent.Column];
            ExpirePressQueueColumn(columnNotes, ref pressCursorByColumn[keyEvent.Column], judgements, keyEvent.Time, windows, inputEvents, activeHoldByColumn);
            ExpireTailColumn(columnNotes, judgements, keyEvent.Time, windows);

            if (keyEvent.IsPress)
                HandlePress(keyEvent, columnNotes, ref pressCursorByColumn[keyEvent.Column], judgements, windows, inputEvents, activeHoldByColumn);
            else
                HandleRelease(keyEvent, noteStates[keyEvent.Column], judgements, windows, inputEvents, activeHoldByColumn);
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

        return judgements;
    }

    private static void HandlePress(
        RawInputEvent keyEvent,
        IReadOnlyList<NoteState> columnNotes,
        ref int pressCursor,
        List<ManiaJudgement> judgements,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        NoteState?[] activeHoldByColumn)
    {
        if (keyEvent.Consumed)
            return;

        var note = GetPressQueueFront(columnNotes, ref pressCursor);
        if (note == null)
            return;

        if (TryConsumeFrontWithEarlierPress(columnNotes, note, judgements, windows, inputEvents, keyEvent, activeHoldByColumn))
        {
            pressCursor++;
            note = GetPressQueueFront(columnNotes, ref pressCursor);
            if (note == null)
                return;
        }

        var offset = keyEvent.Time - note.Note.StartTime;
        if (!CanConsumePress(offset, windows))
            return;

        ApplyHeadJudgement(note, keyEvent, windows.ResultFor(offset), judgements, activeHoldByColumn);

        AdvancePressCursor(columnNotes, ref pressCursor);
    }

    private static void HandleRelease(
        RawInputEvent keyEvent,
        IReadOnlyList<NoteState> columnNotes,
        List<ManiaJudgement> judgements,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        NoteState?[] activeHoldByColumn)
    {
        if (keyEvent.Consumed)
            return;

        var note = activeHoldByColumn[keyEvent.Column] ??
            FindTailReleaseCandidate(columnNotes, keyEvent, windows, inputEvents);
        if (note == null || note.TailJudged || !note.Note.EndTime.HasValue)
            return;

        if (keyEvent.Time < note.Note.EndTime.Value - windows.Miss * TailReleaseLenience)
        {
            note.BodyBroken = true;
            note.BodyBreakTime = keyEvent.Time;
            note.IsHolding = false;
            keyEvent.Consumed = true;
            keyEvent.ConsumedByObjectId = note.Id;
            if (ReferenceEquals(activeHoldByColumn[keyEvent.Column], note))
                activeHoldByColumn[keyEvent.Column] = null;
            return;
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

    private static List<NoteState>[] CreateNoteStates(ManiaBeatmap beatmap)
    {
        var result = new List<NoteState>[beatmap.Columns];
        for (var c = 0; c < result.Length; c++)
            result[c] = new List<NoteState>();

        var noteId = 0;
        foreach (var group in beatmap.Notes.GroupBy(note => note.Column))
        {
            var notes = group.OrderBy(note => note.StartTime).ToArray();
            for (var i = 0; i < notes.Length; i++)
                result[group.Key].Add(new NoteState(++noteId, notes[i]));
        }

        return result;
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

    private static bool CanConsumePress(double offset, ManiaWindows windows)
    {
        return offset < 0
            ? offset >= -windows.Miss
            : offset <= windows.Meh;
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

            var nextPressObjectTime = NextUnjudgedPressObjectTime(notes, cursor);
            var expireTime = Math.Min(note.Note.StartTime + windows.Miss, nextPressObjectTime ?? double.PositiveInfinity);
            if (time < expireTime)
                break;

            if (TryRescueWithUnconsumedPress(notes, note, judgements, windows, inputEvents, activeHoldByColumn, nextPressObjectTime))
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
                Kind: note.Note.EndTime.HasValue ? JudgementKind.HoldHead : JudgementKind.Note)
            {
                ScoreTime = expireTime,
            });
            cursor++;
        }
    }

    private static double? NextUnjudgedPressObjectTime(IReadOnlyList<NoteState> notes, int cursor)
    {
        for (var i = cursor + 1; i < notes.Count; i++)
        {
            if (!notes[i].HeadJudged)
                return notes[i].Note.StartTime;
        }

        return null;
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
        if (currentOffset <= 0 || !CanConsumePress(currentOffset, windows))
            return false;

        var earlierPress = FindCandidatePress(
            inputEvents,
            note,
            windows,
            e => e.Time < currentPress.Time,
            preferLaterOnTie: true);

        if (earlierPress == null)
            return false;

        var earlierOffset = earlierPress.Time - note.Note.StartTime;
        if (Math.Abs(earlierOffset) > Math.Abs(currentOffset) - FrontEarlierPressPreference)
            return false;

        ApplyHeadJudgement(note, earlierPress, windows.ResultFor(earlierOffset), judgements, activeHoldByColumn);

        return true;
    }

    private static bool TryRescueWithUnconsumedPress(
        IReadOnlyList<NoteState> notes,
        NoteState note,
        List<ManiaJudgement> judgements,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents,
        NoteState?[] activeHoldByColumn,
        double? nextPressObjectTime)
    {
        if (note.HeadJudged || HasEarlierUnjudgedPressObject(notes, note))
            return false;

        var press = FindCandidatePress(
            inputEvents,
            note,
            windows,
            e => !nextPressObjectTime.HasValue || e.Time < nextPressObjectTime.Value,
            preferLaterOnTie: false);

        if (press == null)
            return false;

        var offset = press.Time - note.Note.StartTime;
        ApplyHeadJudgement(note, press, windows.ResultFor(offset), judgements, activeHoldByColumn);

        return true;
    }

    private static void ApplyHeadJudgement(
        NoteState note,
        RawInputEvent press,
        HitResult result,
        List<ManiaJudgement> judgements,
        NoteState?[] activeHoldByColumn)
    {
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

        if (!note.Note.EndTime.HasValue)
            return;

        note.PressTime = press.Time;
        note.IsHolding = note.HeadHit;
        if (note.HeadHit)
            activeHoldByColumn[note.Note.Column] = note;
    }

    private static RawInputEvent? FindCandidatePress(
        IReadOnlyList<RawInputEvent> inputEvents,
        NoteState note,
        ManiaWindows windows,
        Func<RawInputEvent, bool> extraFilter,
        bool preferLaterOnTie)
    {
        var candidates = inputEvents
            .Where(e => e.IsPress &&
                !e.Consumed &&
                e.Column == note.Note.Column &&
                CanConsumePress(e.Time - note.Note.StartTime, windows) &&
                extraFilter(e))
            .OrderBy(e => Math.Abs(e.Time - note.Note.StartTime));

        return preferLaterOnTie
            ? candidates.ThenByDescending(e => e.Time).FirstOrDefault()
            : candidates.ThenBy(e => e.Time).FirstOrDefault();
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
            {
                var expireTime = note.Note.EndTime.Value + windows.Miss * TailReleaseLenience;
                ApplyTailJudgement(note, judgements, expireTime, windows, forceMiss: true);
            }
        }
    }

    private static NoteState? FindTailReleaseCandidate(
        IReadOnlyList<NoteState> notes,
        RawInputEvent release,
        ManiaWindows windows,
        IReadOnlyList<RawInputEvent> inputEvents)
    {
        return notes
            .Where(note =>
            {
                if (!note.Note.EndTime.HasValue || note.TailJudged)
                    return false;

                var endTime = note.Note.EndTime.Value;
                if (release.Time < endTime - windows.Miss * TailReleaseLenience ||
                    release.Time > endTime + windows.Miss * TailReleaseLenience)
                    return false;

                if (!note.HeadJudged && !note.PressTime.HasValue)
                    return false;

                return IsReleasePairAvailableForTail(note, release, inputEvents);
            })
            .Where(note => !note.BodyBroken || release.Time >= note.Note.EndTime!.Value - windows.Miss * TailReleaseLenience)
            .OrderBy(note => note.HeadHit ? 0 : 1)
            .ThenBy(note => Math.Abs(release.Time - note.Note.EndTime!.Value))
            .FirstOrDefault();
    }

    private static bool IsReleasePairAvailableForTail(NoteState note, RawInputEvent release, IReadOnlyList<RawInputEvent> inputEvents)
    {
        var pairedPress = inputEvents.FirstOrDefault(e => e.PairId == release.PairId && e.IsPress);
        if (pairedPress == null)
            return true;

        if (note.BodyBroken)
        {
            var breakTime = note.BodyBreakTime ?? note.PressTime ?? note.Note.StartTime;
            return pairedPress.Time > breakTime;
        }

        return !pairedPress.Consumed || pairedPress.ConsumedByObjectId == note.Id;
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
    private const double FrontEarlierPressPreference = 8;
}
