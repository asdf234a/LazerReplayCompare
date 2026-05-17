using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public static class ModUtility
{
    public static string FormatModsText(IReadOnlyList<ReplayMod> mods)
    {
        return mods.Count == 0 ? "NM" : string.Join(", ", mods.Select(mod => mod.Display));
    }

    public static string BuildModsKey(IReadOnlyList<ReplayMod> mods)
    {
        var modKey = mods.Count == 0 ? "NM" : string.Join("+", mods.Select(mod => mod.MatchKey));
        return $"{modKey}|{GetPlaybackRate(mods).ToString("F4", CultureInfo.InvariantCulture)}";
    }

    public static double GetPlaybackRate(IReadOnlyList<ReplayMod> mods)
    {
        foreach (var mod in mods)
        {
            if (!IsSpeedChangeMod(mod.Acronym))
                continue;

            foreach (var setting in mod.Settings)
            {
                var key = setting.Key.ToLowerInvariant();
                if ((key.Contains("speed") || key.Contains("rate")) && TryParseRate(setting.Value, out var rate))
                    return rate;
            }

            return DefaultRateForMod(mod.Acronym);
        }

        return 1.0;
    }

    public static double GetPlaybackRate(Score score)
    {
        try
        {
            var mods = score.ScoreInfo.GetType().GetProperty("Mods")?.GetValue(score.ScoreInfo);
            if (mods is not IEnumerable modsEnumerable)
                return 1.0;

            foreach (var mod in modsEnumerable.Cast<object>())
            {
                var acronym = GetModAcronym(mod);
                if (acronym is null || !IsSpeedChangeMod(acronym))
                    continue;

                var rate = TryGetSpeedChangeSetting(mod);
                if (rate.HasValue)
                    return rate.Value;

                return DefaultRateForMod(acronym);
            }
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }

        return 1.0;
    }

    public static double GetAdjustedOverallDifficulty(Score score, double baseOverallDifficulty)
    {
        var od = baseOverallDifficulty;

        try
        {
            var mods = score.ScoreInfo.GetType().GetProperty("Mods")?.GetValue(score.ScoreInfo);
            if (mods is not IEnumerable modsEnumerable)
                return ClampDifficulty(od);

            double? directOverallDifficulty = null;
            foreach (var mod in modsEnumerable.Cast<object>())
            {
                var acronym = GetModAcronym(mod);
                if (string.IsNullOrWhiteSpace(acronym))
                    continue;

                var adjusted = TryGetOverallDifficultySetting(mod);
                if (adjusted.HasValue)
                {
                    directOverallDifficulty = adjusted.Value;
                    continue;
                }

                od = ApplyDifficultyMod(acronym, od);
            }

            if (directOverallDifficulty.HasValue)
                od = directOverallDifficulty.Value;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }

        return ClampDifficulty(od);
    }

    public static bool HasMirrorMod(Score score)
    {
        try
        {
            return score.ScoreInfo.Mods.Any(mod => IsMirrorMod(GetModAcronym(mod)));
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return false;
        }
    }

    public static bool IsMirrorMod(string? acronym)
    {
        return string.Equals(acronym, "MR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(acronym, "MIRROR", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSpeedChangeMod(string acronym)
    {
        return acronym.ToUpperInvariant() is "DT" or "NC" or "HT" or "DC";
    }

    public static double DefaultRateForMod(string acronym)
    {
        return acronym.ToUpperInvariant() is "DT" or "NC" ? 1.5 : 0.75;
    }

    public static bool TryParseRate(string? text, out double rate)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && rate > 0)
            return true;

        var match = Regex.Match(text ?? string.Empty, @"\d+(\.\d+)?");
        return match.Success &&
            double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) &&
            rate > 0;
    }

    private static double? TryGetSpeedChangeSetting(object mod)
    {
        try
        {
            var settingsObj = mod.GetType().GetProperty("Settings")?.GetValue(mod);

            if (settingsObj is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    var key = entry.Key?.ToString()?.ToLowerInvariant();
                    if (key?.Contains("speed") == true || key?.Contains("rate") == true)
                    {
                        if (TryParseRate(GetSettingValue(entry.Value), out var rate))
                            return rate;
                    }
                }
            }
            else if (settingsObj is IEnumerable enumerable)
            {
                foreach (var item in enumerable.Cast<object>())
                {
                    var key = item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()?.ToLowerInvariant();
                    if (key?.Contains("speed") == true || key?.Contains("rate") == true)
                    {
                        var raw = item.GetType().GetProperty("Value")?.GetValue(item);
                        if (TryParseRate(GetSettingValue(raw), out var rate))
                            return rate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }

        return null;
    }

    private static double ApplyDifficultyMod(string acronym, double od)
    {
        return acronym.ToUpperInvariant() switch
        {
            "HR" => ClampDifficulty(od * 1.4),
            "EZ" => ClampDifficulty(od * 0.5),
            _ => od,
        };
    }

    private static double? TryGetOverallDifficultySetting(object mod)
    {
        try
        {
            var settingsObj = mod.GetType().GetProperty("Settings")?.GetValue(mod);
            if (settingsObj is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (IsOverallDifficultySetting(entry.Key?.ToString()) &&
                        TryParseDifficulty(GetSettingValue(entry.Value), out var difficulty))
                        return difficulty;
                }
            }
            else if (settingsObj is IEnumerable enumerable)
            {
                foreach (var item in enumerable.Cast<object>())
                {
                    var key = item.GetType().GetProperty("Key")?.GetValue(item)?.ToString();
                    if (!IsOverallDifficultySetting(key))
                        continue;

                    var raw = item.GetType().GetProperty("Value")?.GetValue(item);
                    if (TryParseDifficulty(GetSettingValue(raw), out var difficulty))
                        return difficulty;
                }
            }
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }

        return null;
    }

    private static bool IsOverallDifficultySetting(string? key)
    {
        var normalized = NormalizeSettingKey(key);
        return normalized is "od" or "overalldifficulty";
    }

    private static string NormalizeSettingKey(string? key)
    {
        return Regex.Replace(key ?? string.Empty, @"[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
    }

    private static bool TryParseDifficulty(string? text, out double difficulty)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out difficulty))
            return difficulty >= 0;

        var match = Regex.Match(text ?? string.Empty, @"\d+(\.\d+)?");
        return match.Success &&
            double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out difficulty) &&
            difficulty >= 0;
    }

    private static double ClampDifficulty(double difficulty)
    {
        return Math.Clamp(difficulty, 0, 10);
    }

    private static string? GetModAcronym(object mod)
    {
        return mod.GetType().GetProperty("Acronym")?.GetValue(mod)?.ToString()?.ToUpperInvariant();
    }

    private static string? GetSettingValue(object? value)
    {
        if (value == null)
            return null;

        var nestedValue = value.GetType().GetProperty("Value")?.GetValue(value);
        if (nestedValue != null && !ReferenceEquals(nestedValue, value))
            value = nestedValue;

        return value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}
