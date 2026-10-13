using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// osu!mania hit windows as osu!lazer computed them before 2025.710.0 (ppy/osu 0f078ee550): the plain difficulty range
/// scaled by the rate adjustment, without flooring and the half-millisecond adjustment.
/// </summary>
internal sealed class PreRoundingManiaHitWindows : HitWindows
{
    /// <summary>First osu!lazer release using floored, half-millisecond-adjusted mania hit windows.</summary>
    public static readonly (int Year, int MonthDay) FIRST_ROUNDED_RELEASE = (2025, 710);

    private static readonly (HitResult result, double min, double average, double max)[] ranges =
    {
        (HitResult.Perfect, 22.4, 19.4, 13.9),
        (HitResult.Great, 64, 49, 34),
        (HitResult.Good, 97, 82, 67),
        (HitResult.Ok, 127, 112, 97),
        (HitResult.Meh, 151, 136, 121),
        (HitResult.Miss, 188, 173, 158),
    };

    private readonly double multiplier;
    private readonly Dictionary<HitResult, double> windows = new Dictionary<HitResult, double>();

    public PreRoundingManiaHitWindows(double multiplier, double overallDifficulty)
    {
        this.multiplier = multiplier;
        SetDifficulty(overallDifficulty);
    }

    public override bool IsHitResultAllowed(HitResult result) => windows.ContainsKey(result);

    public override void SetDifficulty(double difficulty)
    {
        foreach (var (result, min, average, max) in ranges)
            windows[result] = IBeatmapDifficultyInfo.DifficultyRange(difficulty, min * multiplier, average * multiplier, max * multiplier);
    }

    public override double WindowFor(HitResult result) => windows.TryGetValue(result, out var value) ? value : 0;

    /// <summary>
    /// Whether <paramref name="score"/> was set on an osu!lazer version that predates the rounded hit windows. Uses the
    /// client version when available and falls back to the play date (as ManiaScoreMultiplierCalculator does).
    /// </summary>
    public static bool AppliesTo(ScoreInfo score)
    {
        if (score.LegacyTotalScore != null)
            return false;

        var clientVersion = score.ClientVersion;
        if (!string.IsNullOrEmpty(clientVersion))
        {
            var pieces = clientVersion.Split('.', '-');
            if (pieces.Length >= 2 && int.TryParse(pieces[0], out var year) && int.TryParse(pieces[1], out var monthDay))
                return (year, monthDay).CompareTo(FIRST_ROUNDED_RELEASE) < 0;
        }

        return score.Date < new DateTimeOffset(FIRST_ROUNDED_RELEASE.Year, 7, 10, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Replaces the hit windows of every note, hold head and hold tail with the pre-2025.710 ones.</summary>
    public static void Apply(IBeatmap playable, IEnumerable<Mod> mods)
    {
        var rate = mods.OfType<ModRateAdjust>().FirstOrDefault()?.SpeedChange.Value ?? 1;
        var windows = new PreRoundingManiaHitWindows(rate, playable.Difficulty.OverallDifficulty);

        foreach (var hitObject in playable.HitObjects)
        {
            switch (hitObject)
            {
                case Note note:
                    note.HitWindows = windows;
                    break;

                case HoldNote hold:
                    hold.Head.HitWindows = windows;
                    hold.Tail.HitWindows = windows;
                    break;
            }
        }
    }
}
