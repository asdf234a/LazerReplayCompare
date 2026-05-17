using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

internal static class JudgementCorrectionService
{
    private const double DensityCorrectionWindowMs = 550;
    private const double DenseCorrectionRatio = 0.48;
    private const int MinDenseCorrectionCount = 10;
    private const double VolatilityCorrectionWindowMs = 700;
    private const double VolatileCorrectionRatio = 0.28;
    private const double MinVolatileCorrectionScore = 8;
    private const double VolatilityPriorityWeight = 2.5;
    private const int ScoreAwareCandidateLimit = 120;
    private const double ScoreAwarePriorityWeight = 180;
    private const double MaxComboDeltaWeight = 18;

    public static IReadOnlyList<ManiaJudgement> CorrectJudgementDistribution(IReadOnlyList<ManiaJudgement> judgements, Score score, double scoreMultiplier)
    {
        var targetCounts = GetTargetJudgementCounts(score);
        if (targetCounts.Values.Sum() == 0)
            return judgements;

        var corrected = judgements.ToArray();
        var currentCounts = CountJudgements(corrected);
        var targetTotal = targetCounts.Values.Sum();

        if (targetTotal != corrected.Length)
            targetCounts = ScaleTargetCounts(targetCounts, corrected.Length);

        var differences = ManiaScoreCalculator.JudgementResults.ToDictionary(
            result => result,
            result => targetCounts.GetValueOrDefault(result) - currentCounts.GetValueOrDefault(result));

        if (differences.Values.All(value => value == 0))
            return corrected;

        var targetScore = score.ScoreInfo.TotalScore;
        var currentSummary = ManiaScoreCalculator.ComputeFinalSummary(corrected, scoreMultiplier);
        var needMoreScore = currentSummary.Score < targetScore;
        var correctionPriorities = BuildCorrectionPriorities(corrected);

        foreach (var target in ManiaScoreCalculator.JudgementResults.OrderByDescending(ManiaScoreCalculator.GetBaseScoreForResult))
        {
            while (differences[target] > 0)
            {
                var source = PickSourceResult(differences, target, needMoreScore);
                if (source == null)
                    return corrected;

                var index = PickJudgementIndexToConvert(corrected, source.Value, target, targetScore, score.ScoreInfo.MaxCombo, scoreMultiplier, correctionPriorities);
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
        return ManiaScoreCalculator.JudgementResults.ToDictionary(
            result => result,
            result => score.ScoreInfo.Statistics.GetValueOrDefault(result));
    }

    private static Dictionary<HitResult, int> CountJudgements(IEnumerable<ManiaJudgement> judgements)
    {
        var counts = ManiaScoreCalculator.JudgementResults.ToDictionary(result => result, _ => 0);
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

        foreach (var result in ManiaScoreCalculator.JudgementResults)
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
        var sources = ManiaScoreCalculator.JudgementResults
            .Where(result => differences.GetValueOrDefault(result) < 0 && result != target);

        return (needMoreScore
                ? sources.OrderBy(ManiaScoreCalculator.GetBaseScoreForResult)
                : sources.OrderByDescending(ManiaScoreCalculator.GetBaseScoreForResult))
            .ThenBy(result => Math.Abs(ManiaScoreCalculator.GetBaseScoreForResult(result) - ManiaScoreCalculator.GetBaseScoreForResult(target)))
            .FirstOrDefault();
    }

    private static int PickJudgementIndexToConvert(IReadOnlyList<ManiaJudgement> judgements, HitResult source, HitResult target, long targetScore, int targetMaxCombo, double scoreMultiplier, Dictionary<int, double> correctionPriorities)
    {
        var currentScore = ManiaScoreCalculator.ComputeFinalSummary(judgements, scoreMultiplier).Score;
        var currentDelta = Math.Abs(currentScore - targetScore);
        var currentMaxComboDelta = Math.Abs(ComputeMaxCombo(judgements) - targetMaxCombo);
        var indexes = Enumerable.Range(0, judgements.Count)
            .Where(index => judgements[index].Result == source && correctionPriorities.ContainsKey(index));

        var candidates = OrderConversionCandidates(judgements, indexes, source, target, currentScore < targetScore, correctionPriorities)
            .Take(ScoreAwareCandidateLimit)
            .ToArray();

        if (candidates.Length == 0)
            return -1;

        var working = judgements.ToArray();
        var bestIndex = -1;
        var bestCost = double.PositiveInfinity;

        foreach (var index in candidates)
        {
            var original = working[index];
            working[index] = original with { Result = target };
            var scoreDelta = Math.Abs(ManiaScoreCalculator.ComputeFinalSummary(working, scoreMultiplier).Score - targetScore);
            var maxComboDelta = Math.Abs(ComputeMaxCombo(working) - targetMaxCombo);
            working[index] = original;

            var priority = CorrectionPriority(correctionPriorities, index);
            var scoreImprovement = Math.Max(0, currentDelta - scoreDelta);
            var maxComboImprovement = Math.Max(0, currentMaxComboDelta - maxComboDelta);
            var cost = scoreDelta + maxComboDelta * MaxComboDeltaWeight - priority * ScoreAwarePriorityWeight - scoreImprovement * 0.08 - maxComboImprovement * MaxComboDeltaWeight;

            if (cost < bestCost)
            {
                bestCost = cost;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static IOrderedEnumerable<int> OrderConversionCandidates(
        IReadOnlyList<ManiaJudgement> judgements,
        IEnumerable<int> indexes,
        HitResult source,
        HitResult target,
        bool needMoreScore,
        Dictionary<int, double> correctionPriorities)
    {
        var ordered = indexes.OrderByDescending(index => CorrectionPriority(correctionPriorities, index));

        if (source == HitResult.Miss && target != HitResult.Miss)
        {
            return ordered
                .ThenByDescending(index => ComboMergePotential(judgements, index))
                .ThenBy(index => Math.Abs(ManiaScoreCalculator.GetBaseScoreForResult(target) - ManiaScoreCalculator.GetBaseScoreForResult(source)))
                .ThenBy(index => judgements[index].Time);
        }

        if (target == HitResult.Miss && source != HitResult.Miss)
        {
            return (needMoreScore
                    ? ordered.ThenBy(index => ComboLengthAt(judgements, index))
                    : ordered.ThenByDescending(index => ComboLengthAt(judgements, index)))
                .ThenByDescending(index => judgements[index].Time);
        }

        return (needMoreScore
                ? ordered.ThenByDescending(index => ComboLengthAt(judgements, index))
                : ordered.ThenBy(index => ComboLengthAt(judgements, index)))
            .ThenBy(index => judgements[index].Time);
    }

    private static IReadOnlyList<ManiaJudgement> ImproveScoreWithSameDistribution(IReadOnlyList<ManiaJudgement> judgements, long targetScore, double scoreMultiplier, Dictionary<int, double> correctionPriorities)
    {
        if (targetScore <= 0 || judgements.Count < 2)
            return judgements;

        var corrected = judgements.ToArray();
        var bestDelta = Math.Abs(ManiaScoreCalculator.ComputeFinalSummary(corrected, scoreMultiplier).Score - targetScore);
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
                    var delta = Math.Abs(ManiaScoreCalculator.ComputeFinalSummary(corrected, scoreMultiplier).Score - targetScore);
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

    private static int ComboMergePotential(IReadOnlyList<ManiaJudgement> judgements, int missIndex)
    {
        return CountHitsLeft(judgements, missIndex) + CountHitsRight(judgements, missIndex) + 1;
    }

    private static int ComputeMaxCombo(IReadOnlyList<ManiaJudgement> judgements)
    {
        var combo = 0;
        var maxCombo = 0;
        foreach (var judgement in judgements)
        {
            if (judgement.Result == HitResult.Miss)
                combo = 0;
            else
                combo++;

            maxCombo = Math.Max(maxCombo, combo);
        }

        return maxCombo;
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
}
