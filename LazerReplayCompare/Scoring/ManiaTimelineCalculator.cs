using osu.Game.Scoring;

namespace LazerReplayCompare;

public sealed class ManiaTimelineCalculator
{
    private readonly ManiaJudgementTimelineBuilder judgementTimelineBuilder = new();
    private readonly ManiaScoreTimelineBuilder scoreTimelineBuilder = new();
    private readonly ManiaDisplayTimelineOrderer displayTimelineOrderer = new();

    public List<ReplayTimelineFrame> Build(
        Score score,
        string beatmapPath,
        double rate = 1.0,
        double scoreMultiplier = 1.0,
        CorrectionMode correctionMode = CorrectionMode.Corrected,
        JudgementScoreOrder scoreOrder = JudgementScoreOrder.JudgementTime)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("A .osu beatmap path is required to simulate mania timelines.");

        var safeRate = rate > 0 ? rate : 1.0;
        var judgements = judgementTimelineBuilder.Build(score, beatmapPath, safeRate);
        if (judgements.Count == 0)
            return new List<ReplayTimelineFrame>();

        var finalJudgements = correctionMode == CorrectionMode.Corrected
            ? JudgementCorrectionService.CorrectJudgementDistribution(judgements, score, scoreMultiplier)
            : judgements;

        var scoredJudgements = scoreTimelineBuilder.Build(finalJudgements, scoreMultiplier, scoreOrder);
        return displayTimelineOrderer.OrderForDisplay(scoredJudgements, scoreOrder);
    }
}
