namespace LazerReplayCompare;

internal sealed class ManiaDisplayTimelineOrderer
{
    public List<ReplayTimelineFrame> OrderForDisplay(
        IReadOnlyList<ManiaScoredJudgement> scoredJudgements,
        JudgementScoreOrder scoreOrder)
    {
        if (scoreOrder != JudgementScoreOrder.CommitTime)
            return scoredJudgements.Select(scored => scored.Frame).ToList();

        return scoredJudgements
            .OrderBy(scored => scored.Judgement.Time)
            .ThenBy(scored => scored.Judgement.Column)
            .Select((scored, index) =>
            {
                var frame = scored.Frame;
                var judgement = scored.Judgement;
                return frame with
                {
                    Time = judgement.Time,
                    Index = index + 1,
                };
            })
            .ToList();
    }
}
