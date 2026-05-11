using System.Collections;
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
    private const double ComboBase = 4;

    public List<ReplayTimelineFrame> Build(Score score, string beatmapPath, double rate = 1.0, double scoreMultiplier = 1.0, bool applyCorrections = true)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("A .osu beatmap path is required to simulate mania timelines.");

        var beatmap = ManiaBeatmap.Parse(beatmapPath);
        if (beatmap.Mode != 3)
            throw new NotSupportedException("The replay does not contain score headers, and only osu!mania timeline simulation is currently supported.");

        var safeRate = rate > 0 ? rate : 1.0;
        var keyPairs = ExtractKeyPairs(score, beatmap.Columns);
        var judgements = Judge(beatmap, keyPairs, safeRate);

        if (judgements.Count == 0)
            return new List<ReplayTimelineFrame>();

        var finalJudgements = applyCorrections
            ? CorrectJudgementDistribution(judgements, score, scoreMultiplier)
            : judgements;

        return ScoreJudgements(finalJudgements, scoreMultiplier);
    }

    // Returns per-column ordered list of (pressRealTime, releaseRealTime) pairs.
    // releaseRealTime is +Infinity if still held at end of replay.
    private static (double press, double release)[][] ExtractKeyPairs(Score score, int columns)
    {
        var events = new List<(double press, double release)>[columns];
        for (var c = 0; c < columns; c++)
            events[c] = new List<(double, double)>();

        var pressStart = new double?[columns];
        var previous = new bool[columns];

        foreach (var frame in score.Replay.Frames.OrderBy(f => f.Time))
        {
            var pressed = GetPressedColumns(frame, columns);
            for (var c = 0; c < columns; c++)
            {
                if (pressed[c] && !previous[c])
                    pressStart[c] = frame.Time;
                else if (!pressed[c] && previous[c] && pressStart[c].HasValue)
                {
                    events[c].Add((pressStart[c]!.Value, frame.Time));
                    pressStart[c] = null;
                }
            }
            previous = pressed;
        }

        for (var c = 0; c < columns; c++)
        {
            if (pressStart[c].HasValue)
                events[c].Add((pressStart[c]!.Value, double.PositiveInfinity));
        }

        return events.Select(e => e.ToArray()).ToArray();
    }

    private static bool[] GetPressedColumns(object frame, int columns)
    {
        var pressed = new bool[columns];
        var actions = frame.GetType().GetField("Actions")?.GetValue(frame) as IEnumerable;
        if (actions == null)
            return pressed;

        foreach (var action in actions)
        {
            var name = action?.ToString() ?? string.Empty;
            if (!name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(name[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBasedColumn))
                continue;
            var col = oneBasedColumn - 1;
            if (col >= 0 && col < columns)
                pressed[col] = true;
        }

        return pressed;
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
                var note = FindBestPressCandidate(noteStates[keyEvent.Column], keyEvent.Time, windows);
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
                var note = activeHoldByColumn[keyEvent.Column];
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
                events.Add(new KeyEvent(pair.press, c, true));
                if (!double.IsPositiveInfinity(pair.release))
                    events.Add(new KeyEvent(pair.release, c, false));
            }
        }

        return events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.IsPress ? 1 : 0)
            .ToList();
    }

    private static NoteState? FindBestPressCandidate(IReadOnlyList<NoteState> notes, double pressTime, ManiaWindows windows)
    {
        return notes
            .Where(note =>
            {
                if (note.HeadJudged)
                {
                    return note.Note.EndTime.HasValue &&
                        !note.TailJudged &&
                        pressTime > note.Note.StartTime &&
                        pressTime <= note.Note.EndTime.Value + windows.Meh;
                }

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

        if (result > HitResult.Meh && !note.HeadHit)
            result = HitResult.Meh;

        note.TailJudged = true;
        judgements.Add(new ManiaJudgement(releaseTime, note.Note.Column, result, endTime, JudgementKind.HoldTail));
    }

    private const double TailReleaseLenience = 1.5;

    private static List<ReplayTimelineFrame> ScoreJudgements(IReadOnlyList<ManiaJudgement> judgements, double scoreMultiplier)
    {
        var safeScoreMultiplier = scoreMultiplier > 0 ? scoreMultiplier : 1.0;
        var maximumComboPortion = 0d;
        for (var combo = 1; combo <= judgements.Count; combo++)
            maximumComboPortion += GetComboScoreChange(HitResult.Perfect, combo);

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

        var currentCombo = 0;
        var highestCombo = 0;
        var currentBaseScore = 0d;
        var currentMaximumBaseScore = 0d;
        var currentAccuracyJudgementCount = 0;
        var currentComboPortion = 0d;

        foreach (var judgement in judgements)
        {
            var key = judgement.Result.ToString();
            hits[key] = hits.GetValueOrDefault(key) + 1;

            if (judgement.Result == HitResult.Miss)
                currentCombo = 0;
            else
                currentCombo++;

            highestCombo = Math.Max(highestCombo, currentCombo);

            currentMaximumBaseScore += GetBaseScoreForResult(HitResult.Perfect);
            currentAccuracyJudgementCount++;
            currentBaseScore += GetBaseScoreForResult(judgement.Result);

            currentComboPortion += GetComboScoreChange(judgement.Result, currentCombo);

            var accuracy = currentMaximumBaseScore > 0 ? currentBaseScore / currentMaximumBaseScore : 1;
            var comboProgress = maximumComboPortion > 0 ? currentComboPortion / maximumComboPortion : 1;
            var accuracyProgress = (double)currentAccuracyJudgementCount / judgements.Count;
            var rawScore = 150000 * comboProgress + 850000 * Math.Pow(accuracy, 2 + 2 * accuracy) * accuracyProgress;
            var score = (long)Math.Round(rawScore * safeScoreMultiplier);

            frames.Add(new ReplayTimelineFrame(
                Time: judgement.Time,
                Index: frames.Count + 1,
                Score: score,
                Combo: currentCombo,
                MaxCombo: highestCombo,
                Accuracy: accuracy,
                Hits: new Dictionary<string, int>(hits, StringComparer.Ordinal)));
        }

        return frames;
    }

    private static IReadOnlyList<ManiaJudgement> CorrectJudgementDistribution(IReadOnlyList<ManiaJudgement> judgements, Score score, double scoreMultiplier)
    {
        var targetCounts = GetTargetJudgementCounts(score);
        if (targetCounts.Values.Sum() == 0)
            return judgements;

        var corrected = judgements.ToArray();
        var currentCounts = CountJudgements(corrected);
        var targetTotal = targetCounts.Values.Sum();

        if (targetTotal != corrected.Length)
            targetCounts = ScaleTargetCounts(targetCounts, corrected.Length);

        var differences = JudgementResults.ToDictionary(
            result => result,
            result => targetCounts.GetValueOrDefault(result) - currentCounts.GetValueOrDefault(result));

        if (differences.Values.All(value => value == 0))
            return corrected;

        var targetScore = score.ScoreInfo.TotalScore;
        var currentSummary = ComputeFinalSummary(corrected, scoreMultiplier);
        var needMoreScore = currentSummary.Score < targetScore;
        var correctionPriorities = BuildCorrectionPriorities(corrected);

        foreach (var target in JudgementResults.OrderByDescending(GetBaseScoreForResult))
        {
            while (differences[target] > 0)
            {
                var source = PickSourceResult(differences, target, needMoreScore);
                if (source == null)
                    return corrected;

                var index = PickJudgementIndexToConvert(corrected, source.Value, target, needMoreScore, correctionPriorities);
                if (index < 0)
                    return corrected;

                corrected[index] = corrected[index] with { Result = target };
                differences[target]--;
                differences[source.Value]++;
            }
        }

        return ImproveScoreWithSameDistribution(corrected, targetScore, scoreMultiplier, correctionPriorities);
    }

    private static Dictionary<HitResult, int> GetTargetJudgementCounts(Score score)
    {
        return JudgementResults.ToDictionary(
            result => result,
            result => score.ScoreInfo.Statistics.GetValueOrDefault(result));
    }

    private static Dictionary<HitResult, int> CountJudgements(IEnumerable<ManiaJudgement> judgements)
    {
        var counts = JudgementResults.ToDictionary(result => result, _ => 0);
        foreach (var judgement in judgements)
        {
            if (counts.ContainsKey(judgement.Result))
                counts[judgement.Result]++;
        }

        return counts;
    }

    private static Dictionary<HitResult, int> ScaleTargetCounts(Dictionary<HitResult, int> targetCounts, int desiredTotal)
    {
        var currentTotal = targetCounts.Values.Sum();
        if (currentTotal <= 0 || desiredTotal <= 0)
            return targetCounts;

        var scaled = new Dictionary<HitResult, int>();
        var remainders = new List<(HitResult Result, double Remainder)>();
        var assigned = 0;

        foreach (var result in JudgementResults)
        {
            var exact = targetCounts.GetValueOrDefault(result) * desiredTotal / (double)currentTotal;
            var whole = (int)Math.Floor(exact);
            scaled[result] = whole;
            assigned += whole;
            remainders.Add((result, exact - whole));
        }

        foreach (var result in remainders.OrderByDescending(item => item.Remainder).Select(item => item.Result))
        {
            if (assigned >= desiredTotal)
                break;

            scaled[result]++;
            assigned++;
        }

        return scaled;
    }

    private static HitResult? PickSourceResult(Dictionary<HitResult, int> differences, HitResult target, bool needMoreScore)
    {
        var sources = JudgementResults
            .Where(result => differences.GetValueOrDefault(result) < 0 && result != target);

        return (needMoreScore
                ? sources.OrderBy(GetBaseScoreForResult)
                : sources.OrderByDescending(GetBaseScoreForResult))
            .ThenBy(result => Math.Abs(GetBaseScoreForResult(result) - GetBaseScoreForResult(target)))
            .FirstOrDefault();
    }

    private static int PickJudgementIndexToConvert(IReadOnlyList<ManiaJudgement> judgements, HitResult source, HitResult target, bool needMoreScore, Dictionary<int, double> correctionPriorities)
    {
        var indexes = Enumerable.Range(0, judgements.Count)
            .Where(index => judgements[index].Result == source && correctionPriorities.ContainsKey(index));

        if (source == HitResult.Miss && target != HitResult.Miss)
        {
            return indexes
                .OrderByDescending(index => CorrectionPriority(correctionPriorities, index))
                .ThenByDescending(index => ComboMergePotential(judgements, index))
                .ThenBy(index => Math.Abs(GetBaseScoreForResult(target) - GetBaseScoreForResult(source)))
                .ThenBy(index => judgements[index].Time)
                .FirstOrDefault(-1);
        }

        if (target == HitResult.Miss && source != HitResult.Miss)
        {
            return indexes
                .OrderByDescending(index => CorrectionPriority(correctionPriorities, index))
                .ThenBy(index => ComboLengthAt(judgements, index))
                .ThenByDescending(index => judgements[index].Time)
                .FirstOrDefault(-1);
        }

        return (needMoreScore
                ? indexes.OrderByDescending(index => CorrectionPriority(correctionPriorities, index)).ThenByDescending(index => ComboLengthAt(judgements, index))
                : indexes.OrderByDescending(index => CorrectionPriority(correctionPriorities, index)).ThenBy(index => ComboLengthAt(judgements, index)))
            .ThenBy(index => judgements[index].Time)
            .FirstOrDefault(-1);
    }

    private static IReadOnlyList<ManiaJudgement> ImproveScoreWithSameDistribution(IReadOnlyList<ManiaJudgement> judgements, long targetScore, double scoreMultiplier, Dictionary<int, double> correctionPriorities)
    {
        if (targetScore <= 0 || judgements.Count < 2)
            return judgements;

        var corrected = judgements.ToArray();
        var bestDelta = Math.Abs(ComputeFinalSummary(corrected, scoreMultiplier).Score - targetScore);
        if (bestDelta <= 300)
            return corrected;

        for (var pass = 0; pass < 10; pass++)
        {
            var candidateIndexes = PickScoreSwapCandidates(corrected, correctionPriorities);
            var bestLeft = -1;
            var bestRight = -1;

            foreach (var left in candidateIndexes)
            {
                foreach (var right in candidateIndexes)
                {
                    if (left >= right || corrected[left].Result == corrected[right].Result)
                        continue;

                    (corrected[left], corrected[right]) = (corrected[right], corrected[left]);
                    var delta = Math.Abs(ComputeFinalSummary(corrected, scoreMultiplier).Score - targetScore);
                    (corrected[left], corrected[right]) = (corrected[right], corrected[left]);

                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestLeft = left;
                        bestRight = right;
                    }
                }
            }

            if (bestLeft < 0 || bestRight < 0)
                break;

            (corrected[bestLeft], corrected[bestRight]) = (corrected[bestRight], corrected[bestLeft]);

            if (bestDelta <= 300)
                break;
        }

        return corrected;
    }

    private static int[] PickScoreSwapCandidates(IReadOnlyList<ManiaJudgement> judgements, Dictionary<int, double> correctionPriorities)
    {
        var indexes = correctionPriorities.Keys.ToArray();

        return indexes
            .OrderByDescending(index => CorrectionPriority(correctionPriorities, index))
            .ThenByDescending(index => ScoreLeverage(judgements, index))
            .Take(140)
            .Concat(indexes
                .OrderByDescending(index => CorrectionPriority(correctionPriorities, index))
                .ThenBy(index => ScoreLeverage(judgements, index))
                .Take(100))
            .Concat(indexes
                .Where(index => judgements[index].Result == HitResult.Miss)
                .OrderByDescending(index => CorrectionPriority(correctionPriorities, index))
                .ThenByDescending(index => ComboMergePotential(judgements, index))
                .Take(80))
            .Distinct()
            .ToArray();
    }

    private static Dictionary<int, double> BuildCorrectionPriorities(IReadOnlyList<ManiaJudgement> judgements)
    {
        if (judgements.Count == 0)
            return new Dictionary<int, double>();

        var times = judgements.Select(judgement => judgement.Time).ToArray();
        var densities = new int[judgements.Count];
        var volatility = new double[judgements.Count];
        for (var i = 0; i < judgements.Count; i++)
        {
            densities[i] = CountJudgementsInWindow(times, times[i] - DensityCorrectionWindowMs, times[i] + DensityCorrectionWindowMs);
            volatility[i] = ComputeJudgementVolatility(judgements, times, i);
        }

        var sortedDensities = densities.OrderBy(value => value).ToArray();
        var percentileIndex = Math.Clamp((int)Math.Floor(sortedDensities.Length * (1 - DenseCorrectionRatio)), 0, sortedDensities.Length - 1);
        var threshold = Math.Max(MinDenseCorrectionCount, sortedDensities[percentileIndex]);

        var sortedVolatility = volatility.OrderBy(value => value).ToArray();
        var volatilityIndex = Math.Clamp((int)Math.Floor(sortedVolatility.Length * (1 - VolatileCorrectionRatio)), 0, sortedVolatility.Length - 1);
        var volatilityThreshold = Math.Max(MinVolatileCorrectionScore, sortedVolatility[volatilityIndex]);

        var priorities = new Dictionary<int, double>();
        for (var i = 0; i < judgements.Count; i++)
        {
            var densityScore = densities[i] >= threshold
                ? densities[i] / (double)Math.Max(1, threshold)
                : 0;

            var volatilityScore = volatility[i] >= volatilityThreshold
                ? volatility[i] / Math.Max(1, volatilityThreshold)
                : 0;

            if (densityScore <= 0 && volatilityScore <= 0)
                continue;

            priorities[i] = densityScore + volatilityScore * VolatilityPriorityWeight;
        }

        return priorities;
    }

    private static double ComputeJudgementVolatility(IReadOnlyList<ManiaJudgement> judgements, double[] sortedTimes, int index)
    {
        var left = LowerBound(sortedTimes, judgements[index].Time - VolatilityCorrectionWindowMs);
        var right = UpperBound(sortedTimes, judgements[index].Time + VolatilityCorrectionWindowMs);
        var count = right - left;
        if (count <= 1)
            return 0;

        var weightedError = 0d;
        var severeCount = 0;
        for (var i = left; i < right; i++)
        {
            var severity = JudgementErrorSeverity(judgements[i].Result);
            weightedError += severity;
            if (severity >= 3)
                severeCount++;
        }

        return weightedError + severeCount * 2.0;
    }

    private static int JudgementErrorSeverity(HitResult result)
    {
        return result switch
        {
            HitResult.Miss => 5,
            HitResult.Meh => 4,
            HitResult.Ok => 3,
            HitResult.Good => 2,
            HitResult.Great => 1,
            _ => 0,
        };
    }

    private static int CountJudgementsInWindow(double[] sortedTimes, double startTime, double endTime)
    {
        var left = LowerBound(sortedTimes, startTime);
        var right = UpperBound(sortedTimes, endTime);
        return Math.Max(0, right - left);
    }

    private static int LowerBound(double[] values, double target)
    {
        var lo = 0;
        var hi = values.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (values[mid] < target) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static int UpperBound(double[] values, double target)
    {
        var lo = 0;
        var hi = values.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (values[mid] <= target) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static int ScoreLeverage(IReadOnlyList<ManiaJudgement> judgements, int index)
    {
        return judgements[index].Result == HitResult.Miss
            ? ComboMergePotential(judgements, index)
            : ComboLengthAt(judgements, index);
    }

    private static double CorrectionPriority(Dictionary<int, double> priorities, int index)
    {
        return priorities.TryGetValue(index, out var priority) ? priority : 0;
    }

    private static double ComputeTargetDelta(IReadOnlyList<ManiaJudgement> judgements, long targetScore, double targetAccuracy, double scoreMultiplier)
    {
        var summary = ComputeFinalSummary(judgements, scoreMultiplier);
        var scoreDelta = Math.Abs(summary.Score - targetScore) / Math.Max(1d, targetScore);
        var accuracyDelta = targetAccuracy > 0
            ? Math.Abs(summary.Accuracy - targetAccuracy) / Math.Max(0.000001, targetAccuracy)
            : 0;

        return scoreDelta + accuracyDelta * 2.0;
    }

    private static (long Score, double Accuracy) ComputeFinalSummary(IReadOnlyList<ManiaJudgement> judgements, double scoreMultiplier)
    {
        var safeScoreMultiplier = scoreMultiplier > 0 ? scoreMultiplier : 1.0;
        var maximumComboPortion = 0d;
        for (var combo = 1; combo <= judgements.Count; combo++)
            maximumComboPortion += GetComboScoreChange(HitResult.Perfect, combo);

        var currentCombo = 0;
        var currentBaseScore = 0d;
        var currentMaximumBaseScore = 0d;
        var currentAccuracyJudgementCount = 0;
        var currentComboPortion = 0d;

        foreach (var judgement in judgements)
        {
            if (judgement.Result == HitResult.Miss)
                currentCombo = 0;
            else
                currentCombo++;

            currentMaximumBaseScore += GetBaseScoreForResult(HitResult.Perfect);
            currentAccuracyJudgementCount++;
            currentBaseScore += GetBaseScoreForResult(judgement.Result);
            currentComboPortion += GetComboScoreChange(judgement.Result, currentCombo);
        }

        var accuracy = currentMaximumBaseScore > 0 ? currentBaseScore / currentMaximumBaseScore : 1;
        var comboProgress = maximumComboPortion > 0 ? currentComboPortion / maximumComboPortion : 1;
        var accuracyProgress = (double)currentAccuracyJudgementCount / judgements.Count;
        var rawScore = 150000 * comboProgress + 850000 * Math.Pow(accuracy, 2 + 2 * accuracy) * accuracyProgress;
        return ((long)Math.Round(rawScore * safeScoreMultiplier), accuracy);
    }

    private static readonly HitResult[] JudgementResults =
    {
        HitResult.Perfect,
        HitResult.Great,
        HitResult.Good,
        HitResult.Ok,
        HitResult.Meh,
        HitResult.Miss,
    };

    private const double DensityCorrectionWindowMs = 550;
    private const double DenseCorrectionRatio = 0.48;
    private const int MinDenseCorrectionCount = 10;
    private const double VolatilityCorrectionWindowMs = 700;
    private const double VolatileCorrectionRatio = 0.28;
    private const double MinVolatileCorrectionScore = 8;
    private const double VolatilityPriorityWeight = 2.5;

    private static int ComboMergePotential(IReadOnlyList<ManiaJudgement> judgements, int missIndex)
    {
        return CountHitsLeft(judgements, missIndex) + CountHitsRight(judgements, missIndex) + 1;
    }

    private static int ComboLengthAt(IReadOnlyList<ManiaJudgement> judgements, int index)
    {
        return CountHitsLeft(judgements, index) + CountHitsRight(judgements, index) + 1;
    }

    private static int CountHitsLeft(IReadOnlyList<ManiaJudgement> judgements, int index)
    {
        var count = 0;
        for (var i = index - 1; i >= 0 && judgements[i].Result != HitResult.Miss; i--)
            count++;

        return count;
    }

    private static int CountHitsRight(IReadOnlyList<ManiaJudgement> judgements, int index)
    {
        var count = 0;
        for (var i = index + 1; i < judgements.Count && judgements[i].Result != HitResult.Miss; i++)
            count++;

        return count;
    }

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
        catch
        {
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

    private static double GetComboScoreChange(HitResult result, int comboAfterJudgement)
    {
        var baseScore = result == HitResult.Perfect ? 300 : GetBaseScoreForResult(result);
        var comboMultiplier = Math.Min(Math.Max(0.5, Math.Log(Math.Max(1, comboAfterJudgement), ComboBase)), Math.Log(400, ComboBase));
        return baseScore * comboMultiplier;
    }

    private static int GetBaseScoreForResult(HitResult result)
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

    private sealed record ManiaJudgement(double Time, int Column, HitResult Result, double ObjectTime, JudgementKind Kind);

    private enum JudgementKind
    {
        Note,
        HoldHead,
        HoldTail,
    }

    private sealed record KeyEvent(double Time, int Column, bool IsPress);

    private sealed class NoteState(ManiaNote note)
    {
        public ManiaNote Note { get; } = note;
        public bool HeadJudged { get; set; }
        public bool HeadHit { get; set; }
        public bool TailJudged { get; set; }
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
