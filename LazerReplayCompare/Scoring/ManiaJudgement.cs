using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

internal sealed record ManiaJudgement(double Time, int Column, HitResult Result, double ObjectTime, JudgementKind Kind)
{
    public double ScoreTime { get; init; } = Time;
}

internal enum JudgementKind
{
    Note,
    HoldHead,
    HoldTail,
}

public enum JudgementScoreOrder
{
    // Score in the order input/release events produced each judgement.
    JudgementTime,

    // Score passive misses at the moment they become committed, then reorder for display later.
    CommitTime,
}
