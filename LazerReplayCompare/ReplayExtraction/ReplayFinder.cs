using System.Collections;
using System.Globalization;
using osu.Game.Scoring;
using Realms;

namespace LazerReplayCompare;

public sealed class ReplayFinder
{
    private const ulong CurrentRealmSchemaVersion = 51;

    private readonly object sync = new();

    public List<ReplayEntry> FindReplays(string beatmapMd5, string realmPath, string filesRoot)
    {
        lock (sync)
        {
            var tempRealmPath = CreateTemporaryRealmCopy(realmPath);

            try
            {
                var config = new RealmConfiguration(tempRealmPath)
                {
                    IsReadOnly = true,
                    SchemaVersion = CurrentRealmSchemaVersion,
                    Schema = new[]
                    {
                        typeof(ScoreInfo),
                        typeof(osu.Game.Beatmaps.BeatmapInfo),
                        typeof(osu.Game.Beatmaps.BeatmapSetInfo),
                        typeof(osu.Game.Beatmaps.BeatmapDifficulty),
                        typeof(osu.Game.Beatmaps.BeatmapMetadata),
                        typeof(osu.Game.Beatmaps.BeatmapUserSettings),
                        typeof(osu.Game.Rulesets.RulesetInfo),
                        typeof(osu.Game.Models.RealmUser),
                        typeof(osu.Game.Models.RealmFile),
                        typeof(osu.Game.Models.RealmNamedFileUsage),
                    },
                };

                using var realm = Realm.GetInstance(config);

                var entries = new List<ReplayEntry>();
                foreach (var score in realm.All<ScoreInfo>().AsEnumerable())
                {
                    if (score.BeatmapInfo?.MD5Hash != beatmapMd5 &&
                        score.BeatmapInfo?.OnlineMD5Hash != beatmapMd5 &&
                        score.BeatmapHash != beatmapMd5)
                    {
                        continue;
                    }

                    var mods = GetReplayMods(score);
                    var modsText = ModUtility.FormatModsText(mods);
                    var modsKey = ModUtility.BuildModsKey(mods);
                    var player = score.User?.Username ?? "unknown";
                    var totalScore = score.TotalScore;
                    var accuracy = score.Accuracy;
                    var maxCombo = score.MaxCombo;
                    var rank = score.Rank.ToString();
                    var statistics = score.Statistics.ToDictionary(stat => stat.Key.ToString(), stat => stat.Value);
                    var timestamp = score.Date.UtcDateTime.Ticks;
                    var date = score.Date.UtcDateTime.ToString("O");

                    foreach (var usage in score.Files)
                    {
                        if (usage.Filename?.EndsWith(".osr", StringComparison.OrdinalIgnoreCase) != true)
                            continue;

                        var filePath = GetLazerFilePath(filesRoot, usage.File?.Hash);
                        if (!File.Exists(filePath))
                            continue;

                        entries.Add(new ReplayEntry(
                            FilePath: filePath,
                            Player: player,
                            Score: totalScore,
                            Mods: mods,
                            ModsText: modsText,
                            ModsKey: modsKey,
                            Accuracy: accuracy,
                            MaxCombo: maxCombo,
                            Rank: rank,
                            Statistics: new Dictionary<string, int>(statistics, StringComparer.Ordinal),
                            Timestamp: timestamp,
                            Date: date));
                    }
                }

                return entries
                    .GroupBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(entry => entry.Score)
                    .ToList();
            }
            finally
            {
                TryDeleteTemporaryRealm(tempRealmPath);
            }
        }
    }

    private static string CreateTemporaryRealmCopy(string realmPath)
    {
        var tempRealmPath = Path.Combine(Path.GetTempPath(), $"LazerReplayCompare-{Guid.NewGuid():N}.realm");
        File.Copy(realmPath, tempRealmPath, overwrite: false);
        return tempRealmPath;
    }

    private static void TryDeleteTemporaryRealm(string tempRealmPath)
    {
        try
        {
            if (File.Exists(tempRealmPath))
                File.Delete(tempRealmPath);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    private static string GetLazerFilePath(string filesRoot, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
            return string.Empty;

        return Path.Combine(filesRoot, hash[0].ToString(), hash[..2], hash);
    }

    private static List<ReplayMod> GetReplayMods(ScoreInfo score)
    {
        try
        {
            var scoreType = score.GetType();
            var modsValue = scoreType.GetProperty("APIMods")?.GetValue(score)
                ?? scoreType.GetProperty("Mods")?.GetValue(score);

            if (modsValue is not IEnumerable modsEnumerable)
                return new List<ReplayMod>();

            return modsEnumerable
                .Cast<object>()
                .Select(mod =>
                {
                    var acronym = GetStringProperty(mod, "Acronym");
                    var settings = GetSettings(mod);
                    var display = settings.Count == 0 ? acronym : $"{acronym}({FormatSettings(settings)})";
                    var matchKey = settings.Count == 0 ? acronym : $"{acronym}({BuildSettingsKey(settings)})";

                    return new ReplayMod(acronym, settings, display, matchKey);
                })
                .Where(mod => !string.IsNullOrWhiteSpace(mod.Acronym))
                .OrderBy(mod => mod.Acronym, StringComparer.Ordinal)
                .ThenBy(mod => mod.MatchKey, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return new List<ReplayMod>();
        }
    }

    private static string GetStringProperty(object source, string name)
    {
        return source.GetType().GetProperty(name)?.GetValue(source)?.ToString() ?? string.Empty;
    }

    private static Dictionary<string, string> GetSettings(object mod)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var settingsValue = mod.GetType().GetProperty("Settings")?.GetValue(mod);
        if (settingsValue == null)
            return settings;

        if (settingsValue is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
                AddSetting(settings, entry.Key, entry.Value);

            return settings;
        }

        if (settingsValue is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                var key = item.GetType().GetProperty("Key")?.GetValue(item);
                var value = item.GetType().GetProperty("Value")?.GetValue(item);
                AddSetting(settings, key, value);
            }
        }

        return settings;
    }

    private static void AddSetting(Dictionary<string, string> settings, object? key, object? value)
    {
        var name = key?.ToString();
        if (string.IsNullOrWhiteSpace(name) || value == null)
            return;

        settings[name] = ConvertSettingValue(value);
    }

    private static string ConvertSettingValue(object value)
    {
        var nestedValue = value.GetType().GetProperty("Value")?.GetValue(value);
        if (nestedValue != null && !ReferenceEquals(nestedValue, value))
            value = nestedValue;

        return value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string FormatSettings(Dictionary<string, string> settings)
    {
        return string.Join(", ", settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    private static string BuildSettingsKey(Dictionary<string, string> settings)
    {
        return string.Join(",", settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
    }

}
