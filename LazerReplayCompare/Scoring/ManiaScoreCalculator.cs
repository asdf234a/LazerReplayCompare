using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

internal static class ManiaScoreCalculator
{
    private const double ComboBase = 4;

    public static readonly HitResult[] JudgementResults =
    {
        HitResult.Perfect,
        HitResult.Great,
        HitResult.Good,
        HitResult.Ok,
        HitResult.Meh,
        HitResult.Miss,
    };

    public static List<ReplayTimelineFrame> ScoreJudgements(IReadOnlyList<ManiaJudgement> judgements, double scoreMultiplier)
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
                Hits: new Dictionary<string, int>(hits, StringComparer.Ordinal),
                DebugInfo: new ReplayFrameDebugInfo(
                    Column: judgement.Column,
                    Kind: judgement.Kind.ToString(),
                    ObjectTime: judgement.ObjectTime,
                    Offset: judgement.Time - judgement.ObjectTime,
                    Result: judgement.Result.ToString())));
        }

        return frames;
    }

    public static (long Score, double Accuracy) ComputeFinalSummary(IReadOnlyList<ManiaJudgement> judgements, double scoreMultiplier)
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

    public static double GetComboScoreChange(HitResult result, int comboAfterJudgement)
    {
        var baseScore = result == HitResult.Perfect ? 300 : GetBaseScoreForResult(result);
        var comboMultiplier = Math.Min(Math.Max(0.5, Math.Log(Math.Max(1, comboAfterJudgement), ComboBase)), Math.Log(400, ComboBase));
        return baseScore * comboMultiplier;
    }

    public static int GetBaseScoreForResult(HitResult result)
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
}
