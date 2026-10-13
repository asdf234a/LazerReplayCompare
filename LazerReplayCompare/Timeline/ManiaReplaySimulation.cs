using osu.Game.Scoring;

namespace LazerReplayCompare;

/// <param name="RawFinal">Final frame of the lazer replay port, exactly as osu!lazer plays the replay back.</param>
/// <param name="CorrectedFinal">Final frame after boundary correction (equal to <paramref name="RawFinal"/> when not applicable).</param>
/// <param name="Correction">Correction label, e.g. <c>boundary-corrected(flips=7,solutions=42)</c> or <c>raw(legacy-replay)</c>.</param>
public sealed record ManiaSimulationSummary(
    ReplayTimelineFrame RawFinal,
    ReplayTimelineFrame CorrectedFinal,
    string Correction,
    double ScoreMultiplier,
    int JudgementCount);

/// <summary>
/// Entry point for batch analysis: simulates an already decoded osu!mania replay once and returns both the raw and the
/// boundary-corrected final state, without building HTTP timeline responses.
/// </summary>
public static class ManiaReplaySimulation
{
    public static ManiaSimulationSummary Run(Score score, string beatmapPath)
    {
        var result = new ManiaTimelineCalculator().Build(score, beatmapPath, CorrectionMode.Corrected);
        if (result.RawFrames.Count == 0)
            throw new InvalidOperationException("The simulated timeline is empty.");

        return new ManiaSimulationSummary(result.RawFrames[^1], result.Frames[^1], result.Label, result.ScoreMultiplier, result.Frames.Count);
    }
}
