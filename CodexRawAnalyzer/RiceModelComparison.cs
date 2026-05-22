using System.Collections;
using System.Globalization;
using System.Text;
using LazerReplayCompare;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using RawScoreBatchAnalyzer.Realm;

internal static class RiceModelComparison
{
    public static async Task<int> RunAsync(string[] args, string workspace)
    {
        var replayPath = GetArg(args, "--replay");
        if (string.IsNullOrWhiteSpace(replayPath))
            replayPath = args.FirstOrDefault(arg => arg.EndsWith(".osr", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(replayPath) || !File.Exists(replayPath))
        {
            Console.Error.WriteLine("Missing replay. Usage: --compare-models --replay <path.osr> [--osu <osu-lazer folder>]");
            return 2;
        }

        var osuRoot = GetArg(args, "--osu");
        if (string.IsNullOrWhiteSpace(osuRoot))
            osuRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "osulazer");

        var reportDir = Path.Combine(workspace, "analysis-data", "reports");
        Directory.CreateDirectory(reportDir);

        var beatmapHash = ReadBeatmapHash(replayPath);
        var beatmapPath = GetArg(args, "--beatmap");
        if (string.IsNullOrWhiteSpace(beatmapPath))
        {
            var index = BeatmapRealmIndex.Build(Path.Combine(osuRoot, "client.realm"), Path.Combine(osuRoot, "files"));
            if (!index.TryGetBeatmapPath(beatmapHash, out beatmapPath))
                throw new InvalidOperationException($"Beatmap not found in realm index: {beatmapHash}");
        }

        await using var stream = File.OpenRead(replayPath);
        var score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream);
        var beatmap = RiceBeatmap.Parse(beatmapPath);
        var rate = ModUtility.GetPlaybackRate(score);
        if (rate <= 0)
            rate = 1.0;

        var od = ModUtility.GetAdjustedOverallDifficulty(score, beatmap.OverallDifficulty);
        var windows = RiceWindows.ForDifficulty(od, rate);
        var keyPairs = ManiaReplayInputExtractor.ExtractKeyPairs(score, beatmap.Columns, ModUtility.HasMirrorMod(score));
        var events = CreateInputEvents(keyPairs);

        var current = JudgeCurrentLike(beatmap, events.Select(e => e.Clone()).ToList(), windows);
        var earliest = JudgeEarliestValid(beatmap, events.Select(e => e.Clone()).ToList(), windows);

        var currentSummary = Score(current);
        var earliestSummary = Score(earliest);
        var targetHits = score.ScoreInfo.Statistics;
        var replayName = Path.GetFileName(replayPath);
        var csvPath = Path.Combine(reportDir, $"rice-model-compare-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        WriteComparisonCsv(csvPath, current, earliest);

        Console.WriteLine($"Replay: {replayName}");
        Console.WriteLine($"Beatmap: {beatmapPath}");
        Console.WriteLine($"Notes={beatmap.Notes.Count}, Columns={beatmap.Columns}, OD={beatmap.OverallDifficulty:F2}->{od:F2}, Rate={rate:F4}, Presses={events.Count(e => e.IsPress)}");
        Console.WriteLine($"OSR: score={score.ScoreInfo.TotalScore}, acc={score.ScoreInfo.Accuracy:P4}, maxCombo={score.ScoreInfo.MaxCombo}, hits={FormatHits(targetHits)}");
        PrintModel("current", current, currentSummary, score);
        PrintModel("earliest", earliest, earliestSummary, score);

        var diffs = current.Zip(earliest, (c, e) => (Current: c, Earliest: e))
            .Where(pair => pair.Current.PressId != pair.Earliest.PressId || pair.Current.Result != pair.Earliest.Result)
            .ToArray();

        Console.WriteLine($"Different note/press matches: {diffs.Length}");
        foreach (var pair in diffs.Take(40))
        {
            Console.WriteLine(
                $"#{pair.Current.NoteIndex} c{pair.Current.Column + 1} t={pair.Current.NoteTime:F1} " +
                $"current={FormatMatch(pair.Current)} earliest={FormatMatch(pair.Earliest)}");
        }

        Console.WriteLine($"CSV: {csvPath}");
        return 0;
    }

    private static void PrintModel(string name, IReadOnlyList<MatchJudgement> judgements, ScoreSummary summary, Score score)
    {
        var hits = CountHits(judgements);
        Console.WriteLine(
            $"{name}: score={summary.Score} delta={summary.Score - score.ScoreInfo.TotalScore}, " +
            $"acc={summary.Accuracy:P4} delta={summary.Accuracy - score.ScoreInfo.Accuracy:+0.000000;-0.000000;0.000000}, " +
            $"maxCombo={summary.MaxCombo} delta={summary.MaxCombo - score.ScoreInfo.MaxCombo}, hits={FormatHits(hits)}");
    }

    private static string FormatMatch(MatchJudgement match)
    {
        if (match.PressId == null)
            return $"{match.Result}@miss";

        return $"{match.Result}@p{match.PressId}({match.PressTime:F1},off={match.Offset:+0.0;-0.0;0.0})";
    }

    private static List<MatchJudgement> JudgeEarliestValid(RiceBeatmap beatmap, List<InputEvent> events, RiceWindows windows)
    {
        var notesByColumn = BuildNoteStates(beatmap);
        var cursorByColumn = new int[beatmap.Columns];
        var judgements = new List<MatchJudgement>();

        foreach (var input in events)
        {
            var notes = notesByColumn[input.Column];
            ExpireColumn(notes, ref cursorByColumn[input.Column], judgements, input.Time, windows);

            if (!input.IsPress || input.Consumed)
                continue;

            var note = Front(notes, ref cursorByColumn[input.Column]);
            if (note == null)
                continue;

            var offset = input.Time - note.Time;
            if (!CanConsumePress(offset, windows))
                continue;

            Consume(note, input, windows.ResultFor(offset), judgements);
            Advance(notes, ref cursorByColumn[input.Column]);
        }

        for (var column = 0; column < notesByColumn.Length; column++)
            ExpireColumn(notesByColumn[column], ref cursorByColumn[column], judgements, double.PositiveInfinity, windows);

        return judgements.OrderBy(j => j.NoteIndex).ToList();
    }

    private static List<MatchJudgement> JudgeCurrentLike(RiceBeatmap beatmap, List<InputEvent> events, RiceWindows windows)
    {
        var notesByColumn = BuildNoteStates(beatmap);
        var cursorByColumn = new int[beatmap.Columns];
        var judgements = new List<MatchJudgement>();

        foreach (var input in events)
        {
            var notes = notesByColumn[input.Column];
            ExpireColumn(notes, ref cursorByColumn[input.Column], judgements, input.Time, windows);

            if (!input.IsPress || input.Consumed)
                continue;

            var note = Front(notes, ref cursorByColumn[input.Column]);
            if (note == null)
                continue;

            if (TryConsumeFrontWithEarlierPress(notes, note, judgements, windows, events, input))
            {
                cursorByColumn[input.Column]++;
                note = Front(notes, ref cursorByColumn[input.Column]);
                if (note == null)
                    continue;
            }

            note = SelectPressCandidate(notes, cursorByColumn[input.Column], note, input, windows, events);
            var offset = input.Time - note.Time;
            if (!CanConsumePress(offset, windows))
                continue;

            Consume(note, input, windows.ResultFor(offset), judgements);
            Advance(notes, ref cursorByColumn[input.Column]);
        }

        for (var column = 0; column < notesByColumn.Length; column++)
            ExpireColumn(notesByColumn[column], ref cursorByColumn[column], judgements, double.PositiveInfinity, windows);

        return judgements.OrderBy(j => j.NoteIndex).ToList();
    }

    private static NoteState? SelectPressCandidate(
        IReadOnlyList<NoteState> notes,
        int cursor,
        NoteState front,
        InputEvent press,
        RiceWindows windows,
        IReadOnlyList<InputEvent> inputEvents)
    {
        var pressTime = press.Time;
        var frontOffset = pressTime - front.Time;
        var next = GetNext(notes, cursor);
        if (next != null && pressTime < next.Time)
            return front;

        if (HasAlternativePressForFront(front, press, windows, inputEvents))
            return front;

        if (Math.Abs(frontOffset) <= PressLookaheadFrontKeepWindow(windows))
            return front;

        var best = front;
        var bestCost = CandidateCost(front, pressTime, 0);
        var checkedCandidates = 0;

        for (var i = cursor; i < notes.Count && checkedCandidates < PressLookaheadCandidateCount; i++)
        {
            var candidate = notes[i];
            if (candidate.Judged)
                continue;

            checkedCandidates++;
            var offset = pressTime - candidate.Time;
            if (offset < -windows.Miss)
                break;
            if (!CanConsumePress(offset, windows))
                continue;

            var distanceFromFront = checkedCandidates - 1;
            var cost = CandidateCost(candidate, pressTime, distanceFromFront);
            if (cost < bestCost)
            {
                best = candidate;
                bestCost = cost;
            }
        }

        if (ReferenceEquals(best, front))
            return front;

        var bestOffset = pressTime - best.Time;
        if (Math.Abs(bestOffset) > windows.Great)
            return front;

        var switchMargin = PressLookaheadSwitchMargin;
        if (!HasAlternativePressForFront(front, press, windows, inputEvents))
            switchMargin += OrphanFrontSwitchPenalty;

        if (Math.Abs(bestOffset) + switchMargin >= Math.Abs(frontOffset))
            return front;

        return best;
    }

    private static bool TryConsumeFrontWithEarlierPress(
        IReadOnlyList<NoteState> notes,
        NoteState note,
        List<MatchJudgement> judgements,
        RiceWindows windows,
        IReadOnlyList<InputEvent> inputEvents,
        InputEvent currentPress)
    {
        if (note.Judged || HasEarlierUnjudgedNote(notes, note))
            return false;

        var currentOffset = currentPress.Time - note.Time;
        if (currentOffset <= 0 || Math.Abs(currentOffset) > windows.Meh)
            return false;

        var earlierPress = inputEvents
            .Where(e => e.IsPress &&
                !e.Consumed &&
                e.Column == note.Column &&
                e.Time < currentPress.Time &&
                CanConsumePress(e.Time - note.Time, windows))
            .OrderBy(e => e.Time)
            .FirstOrDefault();

        if (earlierPress == null)
            return false;

        var earlierOffset = earlierPress.Time - note.Time;
        var currentResult = windows.ResultFor(currentOffset);
        var earlierResult = windows.ResultFor(earlierOffset);
        if (!ShouldPreferSequentialEarlierPress(currentResult, earlierResult))
            return false;

        Consume(note, earlierPress, earlierResult, judgements);
        return true;
    }

    private static bool ShouldPreferSequentialEarlierPress(HitResult currentResult, HitResult earlierResult)
    {
        if (earlierResult < HitResult.Good)
            return false;

        return JudgementRank(earlierResult) >= JudgementRank(currentResult) - 2;
    }

    private static int JudgementRank(HitResult result)
    {
        return result switch
        {
            HitResult.Perfect => 5,
            HitResult.Great => 4,
            HitResult.Good => 3,
            HitResult.Ok => 2,
            HitResult.Meh => 1,
            _ => 0,
        };
    }

    private static bool HasAlternativePressForFront(NoteState front, InputEvent currentPress, RiceWindows windows, IReadOnlyList<InputEvent> inputEvents)
    {
        return inputEvents.Any(e =>
            e.IsPress &&
            !e.Consumed &&
            e.Id != currentPress.Id &&
            e.Column == currentPress.Column &&
            CanConsumePress(e.Time - front.Time, windows));
    }

    private static bool HasEarlierUnjudgedNote(IReadOnlyList<NoteState> notes, NoteState note)
    {
        foreach (var candidate in notes)
        {
            if (ReferenceEquals(candidate, note))
                return false;

            if (!candidate.Judged)
                return true;
        }

        return false;
    }

    private static void ExpireColumn(IReadOnlyList<NoteState> notes, ref int cursor, List<MatchJudgement> judgements, double time, RiceWindows windows)
    {
        while (cursor < notes.Count)
        {
            var note = notes[cursor];
            if (note.Judged)
            {
                cursor++;
                continue;
            }

            var next = GetNext(notes, cursor);
            var expiredByWindow = time > note.Time + windows.Miss;
            var expiredByNext = next != null && time >= next.Time;
            if (!expiredByWindow && !expiredByNext)
                break;

            note.Judged = true;
            judgements.Add(MatchJudgement.Miss(note));
            cursor++;
        }
    }

    private static NoteState? GetNext(IReadOnlyList<NoteState> notes, int cursor)
    {
        for (var i = cursor + 1; i < notes.Count; i++)
        {
            if (!notes[i].Judged)
                return notes[i];
        }

        return null;
    }

    private static NoteState? Front(IReadOnlyList<NoteState> notes, ref int cursor)
    {
        while (cursor < notes.Count && notes[cursor].Judged)
            cursor++;

        return cursor < notes.Count ? notes[cursor] : null;
    }

    private static void Advance(IReadOnlyList<NoteState> notes, ref int cursor)
    {
        while (cursor < notes.Count && notes[cursor].Judged)
            cursor++;
    }

    private static void Consume(NoteState note, InputEvent input, HitResult result, List<MatchJudgement> judgements)
    {
        input.Consumed = true;
        input.ConsumedByNoteIndex = note.Index;
        note.Judged = true;
        judgements.Add(new MatchJudgement(note.Index, note.Column, note.Time, input.Id, input.Time, input.Time - note.Time, result));
    }

    private static List<NoteState>[] BuildNoteStates(RiceBeatmap beatmap)
    {
        var states = new List<NoteState>[beatmap.Columns];
        for (var i = 0; i < states.Length; i++)
            states[i] = new List<NoteState>();

        foreach (var note in beatmap.Notes.Where(note => !note.EndTime.HasValue))
            states[note.Column].Add(new NoteState(note.Index, note.Column, note.Time));

        foreach (var column in states)
            column.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Index.CompareTo(b.Index));

        return states;
    }

    private static List<InputEvent> CreateInputEvents((double press, double release)[][] keyPairs)
    {
        var events = new List<InputEvent>();
        var id = 0;
        for (var column = 0; column < keyPairs.Length; column++)
        {
            foreach (var pair in keyPairs[column])
            {
                events.Add(new InputEvent(++id, pair.press, column, true));
                if (!double.IsPositiveInfinity(pair.release))
                    events.Add(new InputEvent(++id, pair.release, column, false));
            }
        }

        return events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.IsPress ? 1 : 0)
            .ThenBy(e => e.Column)
            .ToList();
    }

    private static ScoreSummary Score(IReadOnlyList<MatchJudgement> judgements)
    {
        var maximumComboPortion = 0d;
        for (var combo = 1; combo <= judgements.Count; combo++)
            maximumComboPortion += ComboScoreChange(HitResult.Perfect, combo);

        var currentCombo = 0;
        var maxCombo = 0;
        var currentBaseScore = 0d;
        var currentMaximumBaseScore = 0d;
        var currentAccuracyJudgementCount = 0;
        var currentComboPortion = 0d;
        long score = 0;
        var accuracy = 1d;

        foreach (var judgement in judgements.OrderBy(j => j.NoteIndex))
        {
            if (judgement.Result == HitResult.Miss)
                currentCombo = 0;
            else
                currentCombo++;

            maxCombo = Math.Max(maxCombo, currentCombo);
            currentMaximumBaseScore += BaseScore(HitResult.Perfect);
            currentAccuracyJudgementCount++;
            currentBaseScore += BaseScore(judgement.Result);
            currentComboPortion += ComboScoreChange(judgement.Result, currentCombo);

            accuracy = currentMaximumBaseScore > 0 ? currentBaseScore / currentMaximumBaseScore : 1;
            var comboProgress = maximumComboPortion > 0 ? currentComboPortion / maximumComboPortion : 1;
            var accuracyProgress = (double)currentAccuracyJudgementCount / judgements.Count;
            var rawScore = 150000 * comboProgress + 850000 * Math.Pow(accuracy, 2 + 2 * accuracy) * accuracyProgress;
            score = (long)Math.Round(rawScore);
        }

        return new ScoreSummary(score, accuracy, maxCombo);
    }

    private static double ComboScoreChange(HitResult result, int comboAfterJudgement)
    {
        var baseScore = result == HitResult.Perfect ? 300 : BaseScore(result);
        var comboMultiplier = Math.Min(Math.Max(0.5, Math.Log(Math.Max(1, comboAfterJudgement), 4)), Math.Log(400, 4));
        return baseScore * comboMultiplier;
    }

    private static int BaseScore(HitResult result)
    {
        return result switch
        {
            HitResult.Perfect => 305,
            HitResult.Great => 300,
            HitResult.Good => 200,
            HitResult.Ok => 100,
            HitResult.Meh => 50,
            _ => 0,
        };
    }

    private static Dictionary<HitResult, int> CountHits(IEnumerable<MatchJudgement> judgements)
    {
        var counts = new Dictionary<HitResult, int>
        {
            [HitResult.Perfect] = 0,
            [HitResult.Great] = 0,
            [HitResult.Good] = 0,
            [HitResult.Ok] = 0,
            [HitResult.Meh] = 0,
            [HitResult.Miss] = 0,
        };

        foreach (var judgement in judgements)
            counts[judgement.Result] = counts.GetValueOrDefault(judgement.Result) + 1;

        return counts;
    }

    private static string FormatHits(IReadOnlyDictionary<HitResult, int> hits)
    {
        return $"P={hits.GetValueOrDefault(HitResult.Perfect)},Gr={hits.GetValueOrDefault(HitResult.Great)},Gd={hits.GetValueOrDefault(HitResult.Good)},Ok={hits.GetValueOrDefault(HitResult.Ok)},Meh={hits.GetValueOrDefault(HitResult.Meh)},Miss={hits.GetValueOrDefault(HitResult.Miss)}";
    }

    private static void WriteComparisonCsv(string path, IReadOnlyList<MatchJudgement> current, IReadOnlyList<MatchJudgement> earliest)
    {
        var currentByNote = current.ToDictionary(j => j.NoteIndex);
        var earliestByNote = earliest.ToDictionary(j => j.NoteIndex);
        var noteIndexes = currentByNote.Keys.Union(earliestByNote.Keys).Order().ToArray();

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("NoteIndex,Column,NoteTime,CurrentPressId,CurrentPressTime,CurrentOffset,CurrentResult,EarliestPressId,EarliestPressTime,EarliestOffset,EarliestResult,Changed");
        foreach (var noteIndex in noteIndexes)
        {
            currentByNote.TryGetValue(noteIndex, out var c);
            earliestByNote.TryGetValue(noteIndex, out var e);
            var changed = c?.PressId != e?.PressId || c?.Result != e?.Result;
            writer.WriteLine(string.Join(',',
                noteIndex,
                (c ?? e)!.Column + 1,
                Format((c ?? e)!.NoteTime),
                c?.PressId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Format(c?.PressTime),
                Format(c?.Offset),
                c?.Result.ToString() ?? string.Empty,
                e?.PressId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Format(e?.PressTime),
                Format(e?.Offset),
                e?.Result.ToString() ?? string.Empty,
                changed ? "yes" : "no"));
        }
    }

    private static string Format(double? value)
    {
        return value.HasValue ? value.Value.ToString("F3", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static string ReadBeatmapHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        _ = reader.ReadByte();
        _ = reader.ReadInt32();
        return ReadOsuString(reader);
    }

    private static string ReadOsuString(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        if (marker == 0)
            return string.Empty;
        if (marker != 0x0b)
            throw new InvalidDataException($"Invalid osu string marker: {marker}");

        var length = ReadUleb128(reader);
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static ulong ReadUleb128(BinaryReader reader)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var value = reader.ReadByte();
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
                return result;
            shift += 7;
        }
    }

    private static double CandidateCost(NoteState note, double pressTime, int distanceFromFront)
    {
        return Math.Abs(pressTime - note.Time) + distanceFromFront * PressLookaheadStepPenalty;
    }

    private static double PressLookaheadFrontKeepWindow(RiceWindows windows)
    {
        return windows.Ok + (windows.Meh - windows.Ok) * 0.5;
    }

    private static bool CanConsumePress(double offset, RiceWindows windows)
    {
        return offset < 0
            ? offset >= -windows.Miss
            : offset <= windows.Meh;
    }

    private const int PressLookaheadCandidateCount = 3;
    private const double PressLookaheadStepPenalty = 18;
    private const double PressLookaheadSwitchMargin = 78;
    private const double OrphanFrontSwitchPenalty = 5;

    private sealed record ScoreSummary(long Score, double Accuracy, int MaxCombo);

    private sealed record MatchJudgement(
        int NoteIndex,
        int Column,
        double NoteTime,
        int? PressId,
        double? PressTime,
        double? Offset,
        HitResult Result)
    {
        public static MatchJudgement Miss(NoteState note)
        {
            return new MatchJudgement(note.Index, note.Column, note.Time, null, null, null, HitResult.Miss);
        }
    }

    private sealed class InputEvent(int id, double time, int column, bool isPress)
    {
        public int Id { get; } = id;
        public double Time { get; } = time;
        public int Column { get; } = column;
        public bool IsPress { get; } = isPress;
        public bool Consumed { get; set; }
        public int? ConsumedByNoteIndex { get; set; }

        public InputEvent Clone()
        {
            return new InputEvent(Id, Time, Column, IsPress);
        }
    }

    private sealed class NoteState(int index, int column, double time)
    {
        public int Index { get; } = index;
        public int Column { get; } = column;
        public double Time { get; } = time;
        public bool Judged { get; set; }
    }

    private sealed record RiceWindows(double Perfect, double Great, double Good, double Ok, double Meh, double Miss)
    {
        public static RiceWindows ForDifficulty(double overallDifficulty, double speedMultiplier)
        {
            var multiplier = speedMultiplier > 0 ? speedMultiplier : 1.0;
            return new RiceWindows(
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

        private static double Window(double difficulty, double easy, double normal, double hard, double multiplier)
        {
            var value = difficulty > 5
                ? normal + (hard - normal) * (difficulty - 5) / 5
                : easy + (normal - easy) * difficulty / 5;

            return Math.Floor(value * multiplier) + 0.5;
        }
    }

    private sealed record RiceNote(int Index, int Column, double Time, double? EndTime);

    private sealed class RiceBeatmap
    {
        public int Columns { get; private init; }
        public double OverallDifficulty { get; private init; }
        public List<RiceNote> Notes { get; } = new();

        public static RiceBeatmap Parse(string beatmapPath)
        {
            var columns = 4;
            var overallDifficulty = 5d;
            var notes = new List<RiceNote>();
            var section = string.Empty;
            var index = 0;

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

                if (section == "[Difficulty]")
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
                    notes.Add(new RiceNote(++index, column, time, endTime));
                }
            }

            var beatmap = new RiceBeatmap
            {
                Columns = columns,
                OverallDifficulty = overallDifficulty,
            };
            beatmap.Notes.AddRange(notes.OrderBy(n => n.Time).ThenBy(n => n.Column));
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
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;
        }
    }
}
