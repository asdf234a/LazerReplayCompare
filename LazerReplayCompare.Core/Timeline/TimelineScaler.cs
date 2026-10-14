using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// Scales a raw timeline so that its final value for the given metric matches the replay's final value.
/// </summary>
public static class TimelineScaler
{
    public static string NormalizeMetric(string? value) => MetricRegistry.Normalize(value, MetricRegistry.Default.Key);

    /// <summary>
    /// Returns the scale factor (replay final value / raw final value) and applies it to the frames.
    /// Score and accuracy are scaled in place; hit-based metrics cannot be expressed through judgement counts, so
    /// consumers multiply their frame value by the returned factor (see <see cref="MetricDefinition.ScalesInFrames"/>).
    /// </summary>
    public static double Apply(List<ReplayTimelineFrame> frames, Score score, string metric)
    {
        var info = score.ScoreInfo;
        return Apply(frames, metric, info.TotalScore, info.Accuracy,
            key => Enum.TryParse<HitResult>(key, out var result) ? info.Statistics.GetValueOrDefault(result) : 0);
    }

    /// <summary>
    /// <see cref="Apply(List{ReplayTimelineFrame}, Score, string)"/> onto explicit final values: score, accuracy and
    /// judgement counts in the same units as the frames.
    /// </summary>
    public static double Apply(List<ReplayTimelineFrame> frames, string metric, double targetScore, double targetAccuracy, Func<string, double> targetHits)
    {
        if (frames.Count == 0)
            return 1.0;

        var definition = MetricRegistry.Find(metric) ?? MetricRegistry.Default;
        var last = frames.MaxBy(frame => frame.Index)!;
        var rawFinal = evaluate(definition, last);
        var target = definition.Evaluate(targetScore, targetAccuracy, targetHits);
        var factor = rawFinal > 0 && target > 0 ? target / rawFinal : 1.0;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            frames[i] = definition.Source switch
            {
                MetricSource.Score => frame with { Score = (long)Math.Round(frame.Score * factor) },
                MetricSource.Accuracy => frame with { Accuracy = Math.Clamp(frame.Accuracy * factor, 0, definition.Maximum ?? double.MaxValue) },
                _ => frame,
            };
        }

        return factor;
    }

    private static double evaluate(MetricDefinition definition, ReplayTimelineFrame frame)
        => definition.Evaluate(frame.Score, frame.Accuracy, key => frame.Hits.GetValueOrDefault(key));
}
