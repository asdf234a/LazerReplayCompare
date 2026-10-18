using System.Collections;
using System.Globalization;
using osu.Game.Scoring;
using Realms;

namespace LazerReplayCompare;

/// <summary>
/// The current beatmap's replays from osu!lazer's database. The database (client.realm, possibly hundreds of MB) is read
/// from a copy so the game's own file is never opened; the copy is reused while client.realm keeps its size and write
/// time, for a minute at most (a change may not show in either right away), unless a scan asks for a fresh one.
/// </summary>
public sealed class ReplayFinder : IDisposable
{
    private const ulong CurrentRealmSchemaVersion = 52;
    private const string copy_prefix = "LazerReplayCompare-";
    private static readonly TimeSpan copy_reuse = TimeSpan.FromMinutes(1);

    private readonly object sync = new();

    // the copy the last scan read, what it is a copy of, and when it was made
    private string? copyPath;
    private (string Source, long Length, DateTime Written) copyOf;
    private DateTime copiedAt;

    public ReplayFinder()
    {
        deleteLeftoverCopies();
    }

    public List<ReplayEntry> FindReplays(string beatmapMd5, string realmPath, string filesRoot)
        => FindReplaysAndBackground(beatmapMd5, realmPath, filesRoot).Replays;

    /// <summary>
    /// The beatmap's saved replays, plus its background image file (lazer stores files by hash, so the background's
    /// original file name from the beatmap metadata is looked up in the beatmap set's files). Background is empty
    /// when the beatmap or its background is not in the database.
    /// </summary>
    /// <param name="fresh">Copy the database again even if it looks unchanged (a score was just saved).</param>
    public (List<ReplayEntry> Replays, string BackgroundPath) FindReplaysAndBackground(string beatmapMd5, string realmPath, string filesRoot, bool fresh = false)
    {
        lock (sync)
        {
            var tempRealmPath = realmCopy(realmPath, fresh);
            try
            {
                return read(tempRealmPath, beatmapMd5, filesRoot);
            }
            catch
            {
                // a copy that can't be read is not reused
                deleteCopy(tempRealmPath);
                copyPath = null;
                throw;
            }
        }
    }

    private static (List<ReplayEntry> Replays, string BackgroundPath) read(string tempRealmPath, string beatmapMd5, string filesRoot)
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
                    Date: date,
                    IsLegacy: score.IsLegacyScore || score.LegacyTotalScore != null));
            }
        }

        var replays = entries
            .GroupBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(entry => entry.Score)
            .ToList();
        return (replays, backgroundPath);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (copyPath != null)
                deleteCopy(copyPath);
            copyPath = null;
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

    /// <summary>The copy to read: the last one while client.realm looks unchanged, otherwise a new one (under a new name).</summary>
    private string realmCopy(string realmPath, bool fresh)
    {
        var source = new FileInfo(realmPath);
        var key = (Path.GetFullPath(realmPath), source.Length, source.LastWriteTimeUtc);
        if (!fresh && copyPath != null && copyOf == key && DateTime.UtcNow - copiedAt < copy_reuse && File.Exists(copyPath))
            return copyPath;

        var next = Path.Combine(Path.GetTempPath(), $"{copy_prefix}{Guid.NewGuid():N}.realm");
        File.Copy(realmPath, next, overwrite: false);

        if (copyPath != null)
            deleteCopy(copyPath);
        copyPath = next;
        copyOf = key;
        copiedAt = DateTime.UtcNow;
        return next;
    }

    /// <summary>Deletes a copy and the files Realm keeps next to it (.lock, .note, .management).</summary>
    private static void deleteCopy(string path)
    {
        foreach (var file in new[] { path, path + ".lock", path + ".note" })
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                InternalLogger.Log(ex);
            }
        }

        try
        {
            if (Directory.Exists(path + ".management"))
                Directory.Delete(path + ".management", recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }

    /// <summary>
    /// Copies (and Realm's files next to them) left in the temp folder by an app that did not close normally, or by older
    /// versions, which deleted only the copy itself.
    /// </summary>
    private static void deleteLeftoverCopies()
    {
        try
        {
            var temp = Path.GetTempPath();
            var leftovers = Directory.EnumerateFileSystemEntries(temp, copy_prefix + "*.realm*")
                                     .Select(entry => entry.EndsWith(".realm", StringComparison.OrdinalIgnoreCase) ? entry : entry[..(entry.LastIndexOf(".realm", StringComparison.OrdinalIgnoreCase) + ".realm".Length)])
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToList();

            foreach (var path in leftovers)
            {
                // one another running copy of the app made lately is left alone
                if (File.Exists(path) && DateTime.UtcNow - File.GetCreationTimeUtc(path) < TimeSpan.FromHours(1))
                    continue;

                deleteCopy(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    /// <summary>
    /// <see cref="ReplayMod"/>s for osu!lazer mod instances (e.g. converted from an osu!stable mod bitmask), the same way
    /// realm scores' mods are listed: settings only where they differ from the defaults.
    /// </summary>
    internal static List<ReplayMod> FromLazerMods(IEnumerable<osu.Game.Rulesets.Mods.Mod> mods)
    {
        return mods
            .Select(mod =>
            {
                var settings = GetSettings(new osu.Game.Online.API.APIMod(mod));
                var display = settings.Count == 0 ? mod.Acronym : $"{mod.Acronym}({FormatSettings(settings)})";
                var matchKey = settings.Count == 0 ? mod.Acronym : $"{mod.Acronym}({BuildSettingsKey(settings)})";
                return new ReplayMod(mod.Acronym, settings, display, matchKey,
                    Name: mod.Name, Type: mod.Type.ToString(), ExtendedInfo: mod.ExtendedIconInformation);
            })
            .OrderBy(mod => mod.Acronym, StringComparer.Ordinal)
            .ThenBy(mod => mod.MatchKey, StringComparer.Ordinal)
            .ToList();
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
