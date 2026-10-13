using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public sealed class ReplayTimelineBuilder
{
    private readonly ManiaTimelineCalculator maniaTimelineCalculator = new();

    public ReplayTimelineResponse Build(
        string replayPath,
        string? beatmapPath,
        double rate = 0,
        CorrectionMode correctionMode = CorrectionMode.Corrected,
        JudgementScoreOrder scoreOrder = JudgementScoreOrder.JudgementTime,
        string scaleMetric = "Score")
    {
        if (string.IsNullOrWhiteSpace(replayPath))
            throw new ArgumentException("Missing osr path.", nameof(replayPath));

        if (!File.Exists(replayPath))
            throw new FileNotFoundException("Replay file was not found.", replayPath);

        using var stream = File.OpenRead(replayPath);
        var score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream);

        var totalFrames = score.Replay.Frames.Count;
        var framesWithHeader = score.Replay.Frames.Count(f => f.Header != null);
        var frames = new List<ReplayTimelineFrame>();
        var lastHitCount = 0;

        foreach (var replayFrame in score.Replay.Frames.Where(frame => frame.Header != null).OrderBy(frame => frame.Time))
        {
            var header = replayFrame.Header!;
            var hits = ConvertStatistics(header.Statistics);
            var hitCount = GetHitCount(hits);

            if (hitCount <= lastHitCount)
                continue;

            frames.Add(new ReplayTimelineFrame(
                Time: replayFrame.Time,
                Index: hitCount,
                Score: header.TotalScore,
                Combo: header.Combo,
                MaxCombo: header.MaxCombo,
                Accuracy: header.Accuracy,
                Hits: hits));

            lastHitCount = hitCount;
        }

        var source = $"frame-header(total={totalFrames},withHeader={framesWithHeader})";
        string? appliedScaleMetric = null;
        var scaleFactor = 1.0;
        if (frames.Count == 0)
        {
            var detectedRate = ModUtility.GetPlaybackRate(score);
            var finalRate = rate > 0 ? rate : detectedRate;
            var scoreMultiplier = GetScoreMultiplier(score);
            frames = maniaTimelineCalculator.Build(score, beatmapPath ?? string.Empty, finalRate, scoreMultiplier, correctionMode, scoreOrder);
            var correctionLabel = correctionMode == CorrectionMode.Corrected
                ? "score-maxcombo-corrected-bounded-soft-continuity"
                : "raw-bounded-soft-continuity";

            if (correctionMode == CorrectionMode.Scaled)
            {
                appliedScaleMetric = TimelineScaler.NormalizeMetric(scaleMetric);
                scaleFactor = TimelineScaler.Apply(frames, score, appliedScaleMetric);
                correctionLabel += $",scaled(metric={appliedScaleMetric},factor={scaleFactor:F6})";
            }
            source = $"mania-simulated(rate={finalRate:F4},scoreMultiplier={scoreMultiplier:F4},matcher=lazer-event,{correctionLabel},totalFrames={totalFrames},withHeader={framesWithHeader})";
        }

        if (frames.Count == 0)
            throw new InvalidOperationException("Could not build a replay timeline from this replay.");

        return new ReplayTimelineResponse(
            ReplayPath: replayPath,
            BeatmapPath: beatmapPath ?? string.Empty,
            Source: source,
            Estimated: false,
            TotalNotes: frames.Last().Index,
            FinalScore: score.ScoreInfo.TotalScore,
            FinalAccuracy: score.ScoreInfo.Accuracy,
            FinalMaxCombo: score.ScoreInfo.MaxCombo,
            Timeline: frames.Select(frame => new object[] { frame.Time, frame.Score, frame.Combo }).ToList(),
            Frames: frames,
            ScaleMetric: appliedScaleMetric,
            ScaleFactor: scaleFactor);
    }

    private static Dictionary<string, int> ConvertStatistics(IReadOnlyDictionary<HitResult, int> statistics)
    {
        return statistics.ToDictionary(
            pair => pair.Key.ToString(),
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    private static int GetHitCount(Dictionary<string, int> hits)
    {
        return hits
            .Where(pair => IsJudgementResult(pair.Key))
            .Sum(pair => pair.Value);
    }

    private static bool IsJudgementResult(string key)
    {
        return key is
            "Miss" or
            "Meh" or
            "Ok" or
            "Good" or
            "Great" or
            "Perfect";
    }

    private static double GetScoreMultiplier(Score score)
    {
        try
        {
            // Mod.ScoreMultiplier is obsolete and always returns 1; lazer computes mod multipliers
            // through the ruleset's ScoreMultiplierCalculator. Passing the score applies the same
            // version-dependent multipliers (key mods, classic) that lazer uses for stored scores.
            var scoreInfo = score.ScoreInfo;
            var calculator = scoreInfo.Ruleset.CreateInstance()
                .CreateScoreMultiplierCalculator(new ScoreMultiplierContext(scoreInfo.BeatmapInfo.Difficulty, scoreInfo));
            var multiplier = calculator.CalculateFor(scoreInfo.Mods);

            return multiplier > 0 ? multiplier : 1.0;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return 1.0;
        }
    }
}

public sealed record ReplayTimelineResponse(
    string ReplayPath,
    string BeatmapPath,
    string Source,
    bool Estimated,
    int TotalNotes,
    long FinalScore,
    double FinalAccuracy,
    int FinalMaxCombo,
    List<object[]> Timeline,
    List<ReplayTimelineFrame> Frames,
    string? ScaleMetric = null,
    double ScaleFactor = 1.0);

public sealed record ReplayTimelineFrame(
    double Time,
    int Index,
    long Score,
    int Combo,
    int MaxCombo,
    double Accuracy,
    Dictionary<string, int> Hits,
    ReplayFrameDebugInfo? DebugInfo = null);

public sealed record ReplayFrameDebugInfo(
    int Column,
    string Kind,
    double ObjectTime,
    double Offset,
    string Result);
