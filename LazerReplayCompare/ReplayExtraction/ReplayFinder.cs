using System.Collections;
using System.Globalization;
using osu.Game.Scoring;
using Realms;

namespace LazerReplayCompare;

public sealed class ReplayFinder
{
    private const ulong CurrentRealmSchemaVersion = 52;

    private readonly object sync = new();

    public List<ReplayEntry> FindReplays(string beatmapMd5, string realmPath, string filesRoot)
        => FindReplaysAndBackground(beatmapMd5, realmPath, filesRoot).Replays;

    /// <summary>
    /// The beatmap's saved replays, plus its background image file (lazer stores files by hash, so the background's
    /// original file name from the beatmap metadata is looked up in the beatmap set's files). Background is empty
    /// when the beatmap or its background is not in the database.
    /// </summary>
    public (List<ReplayEntry> Replays, string BackgroundPath) FindReplaysAndBackground(string beatmapMd5, string realmPath, string filesRoot)
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

                var backgroundPath = FindBackgroundPath(realm, beatmapMd5, filesRoot);
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

                var replays = entries
                    .GroupBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(entry => entry.Score)
                    .ToList();
                return (replays, backgroundPath);
            }
            finally
            {
                TryDeleteTemporaryRealm(tempRealmPath);
            }
        }
    }

    private static string FindBackgroundPath(Realm realm, string beatmapMd5, string filesRoot)
    {
        try
        {
            var beatmap = realm.All<osu.Game.Beatmaps.BeatmapInfo>().Where(info => info.MD5Hash == beatmapMd5).AsEnumerable().FirstOrDefault();
            var fileName = beatmap?.Metadata?.BackgroundFile;
            if (beatmap?.BeatmapSet == null || string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            var usage = beatmap.BeatmapSet.Files.AsEnumerable()
                               .FirstOrDefault(file => string.Equals(file.Filename, fileName, StringComparison.OrdinalIgnoreCase));
            var path = GetLazerFilePath(filesRoot, usage?.File?.Hash);
            return File.Exists(path) ? path : string.Empty;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return string.Empty;
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

            var lazerMods = GetLazerMods(score);

            return modsEnumerable
                .Cast<object>()
                .Select(mod =>
                {
                    var acronym = GetStringProperty(mod, "Acronym");
                    var settings = GetSettings(mod);
                    var display = settings.Count == 0 ? acronym : $"{acronym}({FormatSettings(settings)})";
                    var matchKey = settings.Count == 0 ? acronym : $"{acronym}({BuildSettingsKey(settings)})";

                    var lazerMod = lazerMods.GetValueOrDefault(acronym);

                    return new ReplayMod(acronym, settings, display, matchKey,
                        Name: lazerMod?.Name ?? string.Empty,
                        Type: lazerMod?.Type.ToString() ?? string.Empty,
                        ExtendedInfo: lazerMod?.ExtendedIconInformation ?? string.Empty);
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

    /// <summary>The score's mods as osu!lazer mod instances (with their settings), for icon details.</summary>
    private static Dictionary<string, osu.Game.Rulesets.Mods.Mod> GetLazerMods(ScoreInfo score)
    {
        try
        {
            return score.Mods
                        .GroupBy(mod => mod.Acronym, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return new Dictionary<string, osu.Game.Rulesets.Mods.Mod>(StringComparer.OrdinalIgnoreCase);
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
