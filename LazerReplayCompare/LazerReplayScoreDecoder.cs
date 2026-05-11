using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Game.Scoring.Legacy;

namespace LazerReplayCompare;

public sealed class LazerReplayScoreDecoder : LegacyScoreDecoder
{
    private readonly string? beatmapPath;

    public LazerReplayScoreDecoder(string? beatmapPath)
    {
        this.beatmapPath = beatmapPath;
    }

    protected override Ruleset GetRuleset(int rulesetId)
    {
        return rulesetId switch
        {
            0 => new OsuRuleset(),
            1 => new TaikoRuleset(),
            2 => new CatchRuleset(),
            3 => new ManiaRuleset(),
            _ => throw new NotSupportedException($"Ruleset {rulesetId} is not supported yet."),
        };
    }

    protected override WorkingBeatmap? GetBeatmap(string md5Hash)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            return null;

        return new FlatWorkingBeatmap(beatmapPath);
    }
}
