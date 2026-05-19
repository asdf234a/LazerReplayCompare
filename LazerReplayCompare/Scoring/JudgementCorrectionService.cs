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
    private const double WindowBiasCorrectionMs = 850;
    private const double WindowBiasPriorityWeight = 140;
    private const double ScoreDeltaCostWeight = 0.38;
    private const double MisjudgeProbabilityCostWeight = 950;
    private const double LocalShapePenaltyWeight = 520;
    private const double KindConstraintPenaltyWeight = 760;
    private const double SwapLocalPenaltyWeight = 220;
    private const long AggressiveCorrectionScoreDelta = 2500;

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
        var useConservativePlausibility = currentDelta <= AggressiveCorrectionScoreDelta;
        var indexes = Enumerable.Range(0, judgements.Count)
            .Where(index => judgements[index].Result == source &&
                (!useConservativePlausibility || IsPlausibleConversion(judgements[index], source, target)));

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
            var windowBias = LocalConversionBias(working, index, source, target);
            var misjudgeProbability = MisjudgeProbability(working, index, source, target, priority);
            var localShapePenalty = LocalShapePenalty(working, index, source, target);
            var kindConstraintPenalty = KindConstraintPenalty(working[index], source, target);
            var scoreImprovement = Math.Max(0, currentDelta - scoreDelta);
            var maxComboImprovement = Math.Max(0, currentMaxComboDelta - maxComboDelta);
            var cost = scoreDelta * ScoreDeltaCostWeight +
                maxComboDelta * MaxComboDeltaWeight -
                priority * ScoreAwarePriorityWeight -
                windowBias * WindowBiasPriorityWeight -
                misjudgeProbability * MisjudgeProbabilityCostWeight +
                localShapePenalty * LocalShapePenaltyWeight +
                kindConstraintPenalty * KindConstraintPenaltyWeight -
                scoreImprovement * 0.08 -
                maxComboImprovement * MaxComboDeltaWeight;

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
                .ThenByDescending(index => MisjudgeProbability(judgements, index, source, target, CorrectionPriority(correctionPriorities, index)))
                .ThenByDescending(index => LocalConversionBias(judgements, index, source, target))
                .ThenByDescending(index => ComboMergePotential(judgements, index))
                .ThenBy(index => Math.Abs(ManiaScoreCalculator.GetBaseScoreForResult(target) - ManiaScoreCalculator.GetBaseScoreForResult(source)))
                .ThenBy(index => judgements[index].Time);
        }

        if (target == HitResult.Miss && source != HitResult.Miss)
        {
            return (needMoreScore
                    ? ordered.ThenBy(index => ComboLengthAt(judgements, index))
                    : ordered.ThenByDescending(index => ComboLengthAt(judgements, index)))
                .ThenByDescending(index => MisjudgeProbability(judgements, index, source, target, CorrectionPriority(correctionPriorities, index)))
                .ThenByDescending(index => LocalConversionBias(judgements, index, source, target))
                .ThenByDescending(index => judgements[index].Time);
        }

        return (needMoreScore
                ? ordered.ThenByDescending(index => ComboLengthAt(judgements, index))
                : ordered.ThenBy(index => ComboLengthAt(judgements, index)))
            .ThenByDescending(index => MisjudgeProbability(judgements, index, source, target, CorrectionPriority(correctionPriorities, index)))
            .ThenByDescending(index => LocalConversionBias(judgements, index, source, target))
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

        var bestCost = (double)bestDelta;
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

                    if (!IsPlausibleConversion(corrected[left], corrected[left].Result, corrected[right].Result) ||
                        !IsPlausibleConversion(corrected[right], corrected[right].Result, corrected[left].Result))
                        continue;

                    (corrected[left], corrected[right]) = (corrected[right], corrected[left]);
                    var delta = Math.Abs(ManiaScoreCalculator.ComputeFinalSummary(corrected, scoreMultiplier).Score - targetScore);
                    var localPenalty = SwapLocalShapePenalty(corrected, left, right);
                    var cost = delta + localPenalty * SwapLocalPenaltyWeight;
                    (corrected[left], corrected[right]) = (corrected[right], corrected[left]);

                    if (cost < bestCost)
                    {
                        bestCost = cost;
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

    private static double LocalConversionBias(IReadOnlyList<ManiaJudgement> judgements, int index, HitResult source, HitResult target)
    {
        var sourceScore = ManiaScoreCalculator.GetBaseScoreForResult(source);
        var targetScore = ManiaScoreCalculator.GetBaseScoreForResult(target);
        if (sourceScore == targetScore)
            return 0;

        var time = judgements[index].Time;
        var start = time - WindowBiasCorrectionMs;
        var end = time + WindowBiasCorrectionMs;
        var sourceCount = 0;
        var targetCount = 0;
        var total = 0;
        var scoreSum = 0;

        for (var i = 0; i < judgements.Count; i++)
        {
            var judgement = judgements[i];
            if (judgement.Time < start)
                continue;
            if (judgement.Time > end)
                break;

            total++;
            scoreSum += ManiaScoreCalculator.GetBaseScoreForResult(judgement.Result);
            if (judgement.Result == source)
                sourceCount++;
            else if (judgement.Result == target)
                targetCount++;
        }

        if (total == 0)
            return 0;

        var sourceRatio = sourceCount / (double)total;
        var targetRatio = targetCount / (double)total;
        var averageScore = scoreSum / (double)total;
        var normalizedAverage = averageScore / Math.Max(1, ManiaScoreCalculator.GetBaseScoreForResult(HitResult.Perfect));
        var localSurplus = Math.Max(0, sourceRatio - targetRatio * 0.7);

        if (targetScore < sourceScore)
        {
            var overGenerousScore = Math.Max(0, normalizedAverage - 0.82) * 2.5;
            return localSurplus + overGenerousScore;
        }

        var underGenerousScore = Math.Max(0, 0.72 - normalizedAverage) * 2.5;
        return localSurplus + underGenerousScore;
    }

    private static double MisjudgeProbability(IReadOnlyList<ManiaJudgement> judgements, int index, HitResult source, HitResult target, double priority)
    {
        var judgement = judgements[index];
        var offset = Math.Abs(judgement.Time - judgement.ObjectTime);
        var boundaryScore = BoundaryUncertainty(offset, source);
        var kindScore = KindUncertainty(judgement.Kind, source, target);
        var localScore = Math.Clamp(priority / 8.0, 0, 1);
        var jumpPenalty = Math.Clamp((ResultDistance(source, target) - 1) * 0.18, 0, 0.65);
        var localBias = Math.Clamp(LocalConversionBias(judgements, index, source, target) / 3.0, 0, 1);

        return Math.Clamp(
            boundaryScore * 0.42 +
            kindScore * 0.18 +
            localScore * 0.22 +
            localBias * 0.18 -
            jumpPenalty,
            0,
            1);
    }

    private static double BoundaryUncertainty(double offset, HitResult source)
    {
        var boundaries = new[]
        {
            22.5,
            64.5,
            97.5,
            127.5,
            151.5,
            188.5,
        };

        var nearestDistance = boundaries.Min(boundary => Math.Abs(offset - boundary));
        var edgeScore = 1 - Math.Clamp(nearestDistance / 22.0, 0, 1);

        if (source == HitResult.Miss)
        {
            var missBoundary = Math.Abs(offset - boundaries[^1]);
            return Math.Clamp(1 - missBoundary / 85.0, 0.18, 1);
        }

        return Math.Clamp(edgeScore, 0.1, 1);
    }

    private static double KindUncertainty(JudgementKind kind, HitResult source, HitResult target)
    {
        var baseScore = kind switch
        {
            JudgementKind.HoldTail => 0.72,
            JudgementKind.HoldHead => 0.62,
            _ => 0.54,
        };

        if (kind == JudgementKind.HoldTail && ResultDistance(source, target) > 2)
            baseScore -= 0.22;

        if (target == HitResult.Miss && kind == JudgementKind.HoldTail)
            baseScore -= 0.12;

        return Math.Clamp(baseScore, 0, 1);
    }

    private static double LocalShapePenalty(IReadOnlyList<ManiaJudgement> judgements, int index, HitResult source, HitResult target)
    {
        var time = judgements[index].Time;
        var start = time - WindowBiasCorrectionMs;
        var end = time + WindowBiasCorrectionMs;
        var sourceCount = 0;
        var targetCount = 0;
        var total = 0;
        var sameNeighbor = 0;

        for (var i = 0; i < judgements.Count; i++)
        {
            var judgement = judgements[i];
            if (judgement.Time < start)
                continue;
            if (judgement.Time > end)
                break;

            total++;
            if (judgement.Result == source)
                sourceCount++;
            else if (judgement.Result == target)
                targetCount++;
        }

        if (index > 0 && judgements[index - 1].Result == target)
            sameNeighbor++;
        if (index + 1 < judgements.Count && judgements[index + 1].Result == target)
            sameNeighbor++;

        if (total == 0)
            return 0;

        var targetRatio = targetCount / (double)total;
        var sourceRatio = sourceCount / (double)total;
        var isolatedPenalty = sameNeighbor == 0 && ResultDistance(source, target) > 1 ? 0.35 : 0;
        var distortionPenalty = Math.Max(0, targetRatio - sourceRatio) * 0.8;

        return distortionPenalty + isolatedPenalty;
    }

    private static double KindConstraintPenalty(ManiaJudgement judgement, HitResult source, HitResult target)
    {
        var distance = ResultDistance(source, target);
        var penalty = 0d;

        if (distance >= 3)
            penalty += 0.55;

        if (judgement.Kind == JudgementKind.HoldTail)
        {
            if (distance >= 2)
                penalty += 0.25;
            if (target == HitResult.Perfect &&
                ManiaScoreCalculator.GetBaseScoreForResult(source) <= ManiaScoreCalculator.GetBaseScoreForResult(HitResult.Meh))
                penalty += 0.35;
        }

        if (judgement.Kind == JudgementKind.HoldHead && target == HitResult.Perfect && source == HitResult.Miss)
            penalty += 0.28;

        return Math.Clamp(penalty, 0, 1.4);
    }

    private static bool IsPlausibleConversion(ManiaJudgement judgement, HitResult source, HitResult target)
    {
        var sourceScore = ManiaScoreCalculator.GetBaseScoreForResult(source);
        var targetScore = ManiaScoreCalculator.GetBaseScoreForResult(target);
        var distance = ResultDistance(source, target);
        if (distance == 0)
            return true;

        var offset = Math.Abs(judgement.Time - judgement.ObjectTime);

        if (targetScore < sourceScore)
        {
            if (target == HitResult.Miss && source == HitResult.Perfect && offset <= (judgement.Kind == JudgementKind.HoldTail ? 64 : 35))
                return false;
            if (target == HitResult.Miss && source == HitResult.Great && offset <= 42)
                return false;
        }
        else
        {
            if (source == HitResult.Miss && offset > 230)
                return false;
            if (source == HitResult.Miss && target == HitResult.Perfect)
                return false;
        }

        return true;
    }

    private static double SwapLocalShapePenalty(IReadOnlyList<ManiaJudgement> judgements, int left, int right)
    {
        var leftPenalty = LocalShapePenalty(judgements, left, judgements[right].Result, judgements[left].Result);
        var rightPenalty = LocalShapePenalty(judgements, right, judgements[left].Result, judgements[right].Result);
        return leftPenalty + rightPenalty;
    }

    private static int ResultDistance(HitResult left, HitResult right)
    {
        var leftIndex = Array.IndexOf(ManiaScoreCalculator.JudgementResults, left);
        var rightIndex = Array.IndexOf(ManiaScoreCalculator.JudgementResults, right);
        if (leftIndex < 0 || rightIndex < 0)
            return 0;

        return Math.Abs(leftIndex - rightIndex);
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
