using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using osu.Game.Scoring;
using Realms;

namespace LazerReplayCompare;

/// <summary>A replay in osu!lazer's files, with the beatmaps (MD5) its score belongs to.</summary>
public sealed record StoredReplay(string[] Beatmaps, ReplayEntry Entry);

/// <summary>Where a beatmap's .osu file and background are in osu!lazer's files (by hash; empty when unknown).</summary>
public sealed record StoredBeatmap(string File, string Background);

/// <summary>
/// Reads osu!lazer's data: its database (client.realm), always from a temporary copy so the game's own file is never
/// opened, and single replay files from its files folder.
/// </summary>
public static class ReplayFinder
{
    private const string copy_prefix = "LazerReplayCompare-";

    /// <summary>The database's schema version as the installed osu!lazer writes it (52 when it can't be read).</summary>
    public static ulong SchemaVersion { get; } = readSchemaVersion();

    /// <summary>Every score with a replay file (scores being deleted left out), and every beatmap's files.</summary>
    public static (List<StoredReplay> Replays, Dictionary<string, StoredBeatmap> Beatmaps) ReadDatabase(string realmPath, string filesRoot)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"{copy_prefix}{Guid.NewGuid():N}.realm");
        File.Copy(realmPath, copy, overwrite: false);

        try
        {
            return read(copy, filesRoot);
        }
        finally
        {
            deleteCopy(copy);
        }
    }

    private static (List<StoredReplay>, Dictionary<string, StoredBeatmap>) read(string realmPath, string filesRoot)
    {
        var config = new RealmConfiguration(realmPath)
        {
            IsReadOnly = true,
            SchemaVersion = SchemaVersion,
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

        var replays = new List<StoredReplay>();
        foreach (var score in realm.All<ScoreInfo>().AsEnumerable())
        {
            if (score.DeletePending)
                continue;

            var beatmaps = new[] { score.BeatmapInfo?.MD5Hash, score.BeatmapInfo?.OnlineMD5Hash, score.BeatmapHash }
                           .Where(hash => !string.IsNullOrWhiteSpace(hash))
                           .Select(hash => hash!.ToLowerInvariant())
                           .Distinct()
                           .ToArray();
            if (beatmaps.Length == 0)
                continue;

            foreach (var usage in score.Files)
            {
                if (usage.Filename?.EndsWith(".osr", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                var filePath = GetLazerFilePath(filesRoot, usage.File?.Hash);
                if (File.Exists(filePath))
                    replays.Add(new StoredReplay(beatmaps, ToEntry(score, filePath)));
            }
        }

        // each beatmap's .osu file (stored under its own hash) and background (looked up by name in its set's files)
        var stored = new Dictionary<string, StoredBeatmap>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in realm.All<osu.Game.Beatmaps.BeatmapSetInfo>().AsEnumerable())
        {
            Dictionary<string, string>? files = null;
            foreach (var beatmap in set.Beatmaps)
            {
                if (string.IsNullOrWhiteSpace(beatmap.MD5Hash))
                    continue;

                var background = beatmap.Metadata?.BackgroundFile;
                var backgroundHash = string.Empty;
                if (!string.IsNullOrWhiteSpace(background))
                {
                    files ??= set.Files.AsEnumerable()
                                 .Where(file => file.Filename != null && file.File?.Hash != null)
                                 .GroupBy(file => file.Filename!, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(group => group.Key, group => group.First().File!.Hash!, StringComparer.OrdinalIgnoreCase);
                    backgroundHash = files.GetValueOrDefault(background) ?? string.Empty;
                }

                stored[beatmap.MD5Hash] = new StoredBeatmap(beatmap.Hash ?? string.Empty, backgroundHash);
            }
        }

        return (replays, stored);
    }

    /// <summary>
    /// The beatmap (MD5) a file in osu!lazer's files is a replay of, or null when it is not a replay. Only the start of the
    /// file is read: ruleset, replay version and beatmap MD5.
    /// </summary>
    public static string? ReplayBeatmapOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = new byte[40];
            if (stream.Read(start, 0, start.Length) < start.Length)
                return null;

            int version = BitConverter.ToInt32(start, 1);
            if (start[0] > 3 || version is < 20000000 or > 39999999 || start[5] != 0x0b || start[6] != 32)
                return null;

            var md5 = Encoding.ASCII.GetString(start, 7, 32);
            return md5.All(Uri.IsHexDigit) ? md5.ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Every file of an imported beatmap set lands in osu!lazer's files folder too, so a file that only looks like a replay
    // is checked before decoding: no real replay comes near these sizes, and the decoder would allocate whatever a
    // length field says.
    private const long max_replay_file = 32 * 1024 * 1024;
    private const long max_replay_data = 64 * 1024 * 1024;

    /// <summary>A replay file's score, read with its beatmap (the .osu file).</summary>
    public static ReplayEntry ReadReplay(string path, string beatmapPath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        checkReplaySizes(stream);
        stream.Position = 0;

        var score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream).ScoreInfo;
        return ToEntry(score, path);
    }

    /// <summary>Throws when a replay file, or the data it says it holds, is larger than any real replay's.</summary>
    private static void checkReplaySizes(Stream stream)
    {
        if (stream.Length > max_replay_file)
            throw new InvalidDataException("The file is too large to be a replay.");

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        reader.ReadByte(); // ruleset
        int version = reader.ReadInt32();
        for (int i = 0; i < 3; i++)
            skipString(reader); // beatmap MD5, player, replay MD5
        stream.Seek(6 * 2 + 4 + 2 + 1 + 4, SeekOrigin.Current); // judgement counts, score, combo, perfect, mods
        skipString(reader); // life bar graph
        reader.ReadInt64(); // date

        checkCompressed(reader); // the frames
        if (version >= 20140721)
            reader.ReadInt64(); // online score id
        else if (version >= 20121008)
            reader.ReadInt32();
        if (version >= 30000001)
            checkCompressed(reader); // osu!lazer's score details

        static void skipString(BinaryReader reader)
        {
            byte flag = reader.ReadByte();
            if (flag == 0x00)
                return;
            if (flag != 0x0b)
                throw new InvalidDataException($"Unexpected string flag 0x{flag:x2}.");

            long length = 0;
            int shift = 0;
            byte part;
            do
            {
                part = reader.ReadByte();
                length |= (long)(part & 0x7f) << shift;
                shift += 7;
            } while ((part & 0x80) != 0 && shift < 35);

            if (length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("The replay is cut off.");
            reader.BaseStream.Seek(length, SeekOrigin.Current);
        }

        // LZMA data: 5 bytes of properties, then the uncompressed size (-1 when unknown, as some tools write it)
        static void checkCompressed(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length <= 0)
                return;

            var data = reader.BaseStream;
            if (length > data.Length - data.Position)
                throw new InvalidDataException("The replay is cut off.");

            long end = data.Position + length;
            if (length >= 13)
            {
                data.Seek(5, SeekOrigin.Current);
                long size = reader.ReadInt64();
                if (size < -1 || size > max_replay_data)
                    throw new InvalidDataException("The replay's data is too large to be real.");
            }

            data.Position = end;
        }
    }

    /// <summary>A score as the replay list shows it (from the database, or read from its replay file).</summary>
    public static ReplayEntry ToEntry(ScoreInfo score, string filePath)
    {
        var mods = GetReplayMods(score);
        return new ReplayEntry(
            FilePath: filePath,
            Player: score.User?.Username ?? "unknown",
            Score: score.TotalScore,
            Mods: mods,
            ModsText: ModUtility.FormatModsText(mods),
            ModsKey: ModUtility.BuildModsKey(mods),
            Accuracy: score.Accuracy,
            MaxCombo: score.MaxCombo,
            Rank: score.Rank.ToString(),
            Statistics: score.Statistics.ToDictionary(stat => stat.Key.ToString(), stat => stat.Value, StringComparer.Ordinal),
            Timestamp: score.Date.UtcDateTime.Ticks,
            Date: score.Date.UtcDateTime.ToString("O"),
            IsLegacy: score.IsLegacyScore || score.LegacyTotalScore != null);
    }

    public static string GetLazerFilePath(string filesRoot, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
            return string.Empty;

        return Path.Combine(filesRoot, hash[0].ToString(), hash[..2], hash);
    }

    /// <summary>
    /// Copies (and Realm's files next to them) left in the temp folder by an app that did not close normally, or by older
    /// versions, which deleted only the copy itself.
    /// </summary>
    public static void DeleteLeftoverCopies()
    {
        try
        {
            var leftovers = Directory.EnumerateFileSystemEntries(Path.GetTempPath(), copy_prefix + "*.realm*")
                                     .Select(entry => entry.EndsWith(".realm", StringComparison.OrdinalIgnoreCase)
                                         ? entry
                                         : entry[..(entry.LastIndexOf(".realm", StringComparison.OrdinalIgnoreCase) + ".realm".Length)])
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

    /// <summary>osu!lazer's RealmAccess.schema_version, read from the installed osu.Game (a constant, so a newer osu!lazer brings its own).</summary>
    private static ulong readSchemaVersion()
    {
        try
        {
            var field = typeof(osu.Game.Database.RealmAccess).GetField("schema_version", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetRawConstantValue() is int version && version > 0)
                return (ulong)version;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }

        return 52;
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
