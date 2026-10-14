using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;

namespace LazerReplayCompare;

/// <summary>
/// osu!stable osu!mania ScoreV1 (osu! wiki), checked judgement by judgement against osu!stable's live score:
/// half of the score comes from the judgement values, half from a bonus that grows on MAX/300 and drops on the rest.
/// </summary>
internal sealed class LegacyManiaScoreV1
{
    private readonly double unit;
    private readonly double divider;
    private double bonus = 100;

    public double Total { get; private set; }

    /// <param name="objectCount">Judgements in the map (one per hold note).</param>
    /// <param name="multiplier">Stable's mod multiplier (<see cref="ModMultiplier"/>).</param>
    /// <param name="divider">Bonus punishment divider (<see cref="ModDivider"/>).</param>
    public LegacyManiaScoreV1(int objectCount, double multiplier, double divider)
    {
        unit = 1_000_000 * multiplier * 0.5 / Math.Max(objectCount, 1);
        this.divider = divider;
    }

    public void Add(HitResult result)
    {
        var (value, bonusValue, gain, punish) = result switch
        {
            HitResult.Perfect => (320, 32, 2, 0.0),
            HitResult.Great => (300, 32, 1, 0),
            HitResult.Good => (200, 16, 0, 8),
            HitResult.Ok => (100, 8, 0, 24),
            HitResult.Meh => (50, 4, 0, 44),
            _ => (0, 0, 0, double.PositiveInfinity),
        };

        // the bonus is updated before the judgement is scored
        bonus = Math.Clamp(bonus + gain - punish / divider, 0, 100);
        Total += unit * value / 320 + unit * bonusValue * Math.Sqrt(bonus) / 320;
    }

    /// <summary>Stable's score multiplier: Easy, No Fail and Half Time halve it; key mods on converts reduce it.</summary>
    public static double ModMultiplier(IBeatmap beatmap, IReadOnlyList<Mod> mods)
        => ((ILegacyRuleset)new ManiaRuleset()).CreateLegacyScoreSimulator()
                                                .GetLegacyScoreMultiplier(mods, LegacyBeatmapConversionDifficultyInfo.FromBeatmap(beatmap));

    /// <summary>
    /// Divider of the bonus punishment: Hard Rock and each visibility mod make the bonus drop slower. osu!mania has no
    /// Double Time / Nightcore factor here (checked on DT replays).
    /// </summary>
    public static double ModDivider(IReadOnlyList<Mod> mods)
        => (mods.Any(m => m is ModHardRock) ? 1.08 : 1) * Math.Pow(1.06, mods.Count(m => m is ManiaModFadeIn or ModHidden or ModFlashlight));

    /// <summary>osu!stable's osu!mania accuracy: MAX and 300 both count fully.</summary>
    public static double Accuracy(Func<HitResult, int> count)
    {
        int total = count(HitResult.Perfect) + count(HitResult.Great) + count(HitResult.Good) + count(HitResult.Ok) + count(HitResult.Meh) + count(HitResult.Miss);
        if (total == 0) return 1;
        return (300.0 * (count(HitResult.Perfect) + count(HitResult.Great)) + 200.0 * count(HitResult.Good) + 100.0 * count(HitResult.Ok) + 50.0 * count(HitResult.Meh))
               / (300.0 * total);
    }
}
