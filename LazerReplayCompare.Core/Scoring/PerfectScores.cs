using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// The score a play would have after each judgement if every judgement so far were MAX: what the overlays divide the
/// live (or replay) score by to show it as a share of the best possible score at that point ("score_acc").
/// </summary>
public static class PerfectScores
{
    private static readonly HashSet<HitResult> counted = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss];

    /// <summary>
    /// Scores after 0, 1, 2… judgements, as <paramref name="client"/> scores the beatmap with <paramref name="modAcronyms"/>:
    /// osu!lazer's standardised score (hold note heads and tails judged apart), or osu!stable's ScoreV1 (one judgement per
    /// hold note).
    /// </summary>
    /// <param name="rate">Speed of a rate-changing mod, when it is not the mod's default.</param>
    public static long[] Build(string beatmapPath, LiveClient client, IReadOnlyList<string> modAcronyms, double rate = 0)
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

        var working = new FlatWorkingBeatmap(beatmapPath);
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);
        return client == LiveClient.Stable ? stable(working.Beatmap, playable, mods) : lazer(ruleset, playable, mods);
    }

    private static long[] stable(IBeatmap beatmap, IBeatmap playable, IReadOnlyList<Mod> mods)
    {
        int count = playable.HitObjects.Count;
        var scoreV1 = new LegacyManiaScoreV1(count, LegacyManiaScoreV1.ModMultiplier(beatmap, mods), LegacyManiaScoreV1.ModDivider(mods));
        var scores = new long[count + 1];
        for (int i = 1; i <= count; i++)
        {
            scoreV1.Add(HitResult.Perfect);
            scores[i] = (long)Math.Floor(scoreV1.Total);
        }

        return scores;
    }

    private static long[] lazer(ManiaRuleset ruleset, IBeatmap playable, IReadOnlyList<Mod> mods)
    {
        var processor = ruleset.CreateScoreProcessor();
        processor.Mods.Value = mods;
        processor.ApplyBeatmap(playable);

        // every judgement (hold notes: head, body, tail) in the order they happen
        var judged = new List<HitObject>();
        void add(HitObject hitObject)
        {
            judged.Add(hitObject);
            foreach (var nested in hitObject.NestedHitObjects)
                add(nested);
        }

        foreach (var hitObject in playable.HitObjects)
            add(hitObject);

        var scores = new List<long> { 0 };
        foreach (var hitObject in judged.OrderBy(h => h.GetEndTime()))
        {
            var result = new JudgementResult(hitObject, hitObject.Judgement) { Type = hitObject.Judgement.MaxResult };
            processor.ApplyResult(result);
            if (counted.Contains(result.Type))
                scores.Add(processor.TotalScore.Value);
        }

        return scores.ToArray();
    }
}
