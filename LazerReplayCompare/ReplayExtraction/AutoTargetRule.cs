using System.Globalization;
using System.Text.RegularExpressions;

namespace LazerReplayCompare;

/// <summary>Playback rate and Classic mod of a play or replay — what the auto target has to match.</summary>
public readonly record struct PlaySpeed(double Rate, bool Classic)
{
    public string RateText => Rate.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}

/// <summary>
/// In auto mode the comparison target is the best score among replays with the current play's rate and Classic mod.
/// Mirrors <c>isAutoCompatibleReplay</c> / <c>getCurrentModsKey</c> in static/lazerReplayCompareShared/overlayState.js,
/// so the app shows the same target the overlay compares against.
/// </summary>
public static class AutoTargetRule
{
    private const double rate_epsilon = 0.0001;
    private static readonly Regex number = new(@"[\d.]+", RegexOptions.Compiled);

    public static PlaySpeed ForPlay(IReadOnlyList<string> acronyms, double? settingsRate, double modsRate)
    {
        var rate = settingsRate ?? (modsRate > 0 ? modsRate : 1);
        return new PlaySpeed(rate != 1 ? rate : DefaultRate(acronyms), IsClassic(acronyms));
    }

    public static PlaySpeed ForReplay(ReplayEntry replay)
    {
        var acronyms = replay.Mods.Select(mod => mod.Acronym).ToList();
        var settingsRate = replay.Mods.Select(mod => RateFromSettings(mod.Settings)).FirstOrDefault(rate => rate != null);
        return new PlaySpeed(settingsRate ?? DefaultRate(acronyms), IsClassic(acronyms));
    }

    public static bool IsCompatible(ReplayEntry replay, PlaySpeed current)
    {
        var speed = ForReplay(replay);
        return Math.Abs(speed.Rate - current.Rate) <= rate_epsilon && speed.Classic == current.Classic;
    }

    /// <summary>A rate-like mod setting ("speed_change", "rate", …), parsed leniently like the overlay does.</summary>
    public static double? RateFromSettings(IEnumerable<KeyValuePair<string, string>> settings)
    {
        foreach (var (key, value) in settings)
        {
            var name = key.ToLowerInvariant();
            if (!name.Contains("speed") && !name.Contains("rate"))
                continue;

            var match = number.Match(value ?? string.Empty);
            if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0)
                return rate;
        }

        return null;
    }

    private static double DefaultRate(IReadOnlyCollection<string> acronyms)
    {
        if (acronyms.Any(a => is_(a, "DT") || is_(a, "NC")))
            return 1.5;
        if (acronyms.Any(a => is_(a, "HT") || is_(a, "DC")))
            return 0.75;
        return 1;
    }

    private static bool IsClassic(IEnumerable<string> acronyms) => acronyms.Any(a => is_(a, "CL") || is_(a, "CLASSIC"));

    private static bool is_(string acronym, string expected) => string.Equals(acronym?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
