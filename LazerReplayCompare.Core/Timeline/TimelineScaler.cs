using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// Scales a raw timeline so that its final value for the given metric matches the replay's final value.
/// </summary>
public static class TimelineScaler
{
    public static string NormalizeMetric(string? value)
    {
        if (string.Equals(value, "Acc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Accuracy", StringComparison.OrdinalIgnoreCase))
            return "Acc";
        if (string.Equals(value, "Bms", StringComparison.OrdinalIgnoreCase))
            return "Bms";
        if (string.Equals(value, "PpAcc", StringComparison.OrdinalIgnoreCase))
            return "PpAcc";
        if (string.Equals(value, "PpScore", StringComparison.OrdinalIgnoreCase))
            return "PpScore";
        if (string.Equals(value, "V1Acc", StringComparison.OrdinalIgnoreCase))
            return "V1Acc";
        return "Score";
    }

    /// <summary>
    /// Returns the scale factor (replay final value / raw final value) and applies it to the frames.
    /// Score and Acc are scaled in place; hit-based metrics (Bms, PpAcc, PpScore, V1Acc) cannot be
    /// expressed through judgement counts, so consumers multiply their frame value by the returned factor.
    /// </summary>
    public static double Apply(List<ReplayTimelineFrame> frames, Score score, string metric)
    {
        if (frames.Count == 0)
            return 1.0;

        var last = frames.MaxBy(frame => frame.Index)!;
        var rawFinal = GetFrameValue(last, metric);
        var target = GetReplayValue(score, metric);
        var factor = rawFinal > 0 && target > 0 ? target / rawFinal : 1.0;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            frames[i] = metric switch
            {
                "Score" => frame with { Score = (long)Math.Round(frame.Score * factor) },
                "Acc" => frame with { Accuracy = Math.Clamp(frame.Accuracy * factor, 0, 1) },
                _ => frame,
            };
        }

        return factor;
    }

    private static double GetFrameValue(ReplayTimelineFrame frame, string metric)
    {
        return metric switch
        {
            "Score" => frame.Score,
            "Acc" => frame.Accuracy,
            _ => GetHitMetric(key => frame.Hits.GetValueOrDefault(key), metric),
        };
    }

    private static double GetReplayValue(Score score, string metric)
    {
        var info = score.ScoreInfo;
        return metric switch
        {
            "Score" => info.TotalScore,
            "Acc" => info.Accuracy,
            _ => GetHitMetric(key => Enum.TryParse<HitResult>(key, out var result) ? info.Statistics.GetValueOrDefault(result) : 0, metric),
        };
    }

    // Must match the overlay formulas in static/*/index.js.
    private static double GetHitMetric(Func<string, int> hit, string metric)
    {
        double perfect = hit("Perfect"), great = hit("Great"), good = hit("Good"), ok = hit("Ok"), meh = hit("Meh"), miss = hit("Miss");
        var total = perfect + great + good + ok + meh + miss;

        return metric switch
        {
            "Bms" => perfect * 2 + great,
            "PpScore" => perfect * 100 + great * 93.75 + good * 62.5 + ok * 31.25 + meh * 15.625,
            "PpAcc" => total > 0 ? (perfect * 100 + great * 93.75 + good * 62.5 + ok * 31.25 + meh * 15.625) / total : 0,
            "V1Acc" => total > 0 ? (perfect * 100 + great * 100 + good * (200 / 3.0) + ok * (100 / 3.0) + meh * (50 / 3.0)) / total : 0,
            _ => 0,
        };
    }
}
