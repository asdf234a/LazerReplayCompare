using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

internal sealed record ManiaJudgement(double Time, int Column, HitResult Result, double ObjectTime, JudgementKind Kind);

internal enum JudgementKind
{
    Note,
    HoldHead,
    HoldTail,
}
