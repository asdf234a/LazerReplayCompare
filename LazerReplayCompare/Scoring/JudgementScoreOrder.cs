namespace LazerReplayCompare;

/// <summary>
/// Kept for API compatibility with the analyzers. The lazer replay port always produces judgements in the order
/// osu!lazer itself applies them, so this no longer changes the simulated timeline.
/// </summary>
public enum JudgementScoreOrder
{
    JudgementTime,
    CommitTime,
}
