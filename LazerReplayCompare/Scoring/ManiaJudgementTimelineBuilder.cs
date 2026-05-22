namespace LazerReplayCompare;

internal sealed class ManiaJudgementTimelineBuilder
{
    private readonly ManiaRawJudgeEngine rawJudgeEngine = new();

    public List<ManiaJudgement> Build(
        osu.Game.Scoring.Score score,
        string beatmapPath,
        double rate)
    {
        var beatmap = ManiaBeatmapParser.Parse(beatmapPath);
        if (beatmap.Mode != 3)
            throw new NotSupportedException("The replay does not contain score headers, and only osu!mania timeline simulation is currently supported.");

            var keyPairs = ManiaReplayInputExtractor.ExtractKeyPairs(score, beatmap.Columns, ModUtility.HasMirrorMod(score));
            var adjustedOverallDifficulty = ModUtility.GetAdjustedOverallDifficulty(score, beatmap.OverallDifficulty);
            return rawJudgeEngine.Judge(beatmap, keyPairs, rate, adjustedOverallDifficulty);
        }
    }
