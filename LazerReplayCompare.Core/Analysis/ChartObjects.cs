using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// Everything osu!lazer judges in a beatmap played with some mods: each note, hold note head and hold note release,
/// with its time and lane. The overlays use it to tell which object each of tosu's live hit errors belongs to (the
/// errors come as bare numbers, in the order they were judged).
/// </summary>
/// <param name="Rate">The play's rate; every time here is real time (song time ÷ rate).</param>
/// <param name="Windows">How far off (real ms) a note press may be for each judgement.</param>
/// <param name="ReleaseLenience">How much wider than those a release's windows are.</param>
/// <param name="Objects">[time, kind, column] in time order; kind 0 is a note, 1 a hold note's head, 2 its release.</param>
public sealed record ChartObjectList(
    double Rate,
    int Columns,
    double OverallDifficulty,
    int Notes,
    int Holds,
    double Length,
    IReadOnlyDictionary<string, double> Windows,
    double ReleaseLenience,
    IReadOnlyList<double[]> Objects);

public static class ChartObjects
{
    private static readonly HitResult[] judgements = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss];

    /// <param name="rate">Speed of a rate-changing mod, when it is not the mod's default.</param>
    public static ChartObjectList Build(string beatmapPath, IReadOnlyList<string> modAcronyms, double rate = 0)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("The beatmap file isn't available.");

        var ruleset = new ManiaRuleset();
        var mods = modAcronyms.Select(acronym => ruleset.CreateModFromAcronym(acronym.Trim().ToUpperInvariant())).OfType<Mod>().ToList();
        if (rate > 0)
        {
            foreach (var rateMod in mods.OfType<ModRateAdjust>())
                rateMod.SpeedChange.Value = rate;
        }

        double speed = mods.OfType<ModRateAdjust>().FirstOrDefault()?.SpeedChange.Value ?? 1;
        var playable = new FlatWorkingBeatmap(beatmapPath).GetPlayableBeatmap(ruleset.RulesetInfo, mods);

        var objects = new List<double[]>();
        foreach (var hitObject in playable.HitObjects.OfType<ManiaHitObject>())
        {
            if (hitObject is HoldNote hold)
            {
                objects.Add([Math.Round(hold.StartTime / speed, 2), 1, hold.Column]);
                objects.Add([Math.Round(hold.EndTime / speed, 2), 2, hold.Column]);
            }
            else if (hitObject is Note)
                objects.Add([Math.Round(hitObject.StartTime / speed, 2), 0, hitObject.Column]);
        }

        objects.Sort((a, b) => a[0].CompareTo(b[0]));

        var reference = playable.HitObjects.OfType<Note>().Cast<osu.Game.Rulesets.Objects.HitObject>()
                                .Concat(playable.HitObjects.OfType<HoldNote>().Select(h => (osu.Game.Rulesets.Objects.HitObject)h.Head))
                                .FirstOrDefault();
        var windows = reference == null
            ? new Dictionary<string, double>()
            : judgements.ToDictionary(result => result.ToString(), result => Math.Round(reference.HitWindows.WindowFor(result) / speed, 3));

        return new ChartObjectList(
            speed,
            playable is ManiaBeatmap mania ? mania.TotalColumns : 0,
            playable.Difficulty.OverallDifficulty,
            playable.HitObjects.Count(o => o is Note),
            playable.HitObjects.Count(o => o is HoldNote),
            Math.Round(objects.Count == 0 ? 0 : objects[^1][0], 1),
            windows,
            TailNote.RELEASE_WINDOW_LENIENCE,
            objects);
    }
}
