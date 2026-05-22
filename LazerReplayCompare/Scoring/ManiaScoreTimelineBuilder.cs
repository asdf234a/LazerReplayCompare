namespace LazerReplayCompare;

internal sealed class ManiaScoreTimelineBuilder
{
    public List<ManiaScoredJudgement> Build(
        IReadOnlyList<ManiaJudgement> judgements,
        double scoreMultiplier,
        JudgementScoreOrder scoreOrder)
    {
        var scoreJudgements = scoreOrder == JudgementScoreOrder.CommitTime
            ? judgements
                .OrderBy(j => j.ScoreTime)
                .ThenBy(j => j.Time)
                .ThenBy(j => j.Column)
                .ToList()
            : judgements
                .OrderBy(j => j.Time)
                .ThenBy(j => j.Column)
                .ToList();

        return ManiaScoreCalculator.ScoreJudgementsWithSource(scoreJudgements, scoreMultiplier);
    }
}

internal sealed record ManiaScoredJudgement(ManiaJudgement Judgement, ReplayTimelineFrame Frame);
