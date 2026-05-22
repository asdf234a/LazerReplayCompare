using System.Diagnostics;
using Realms;

namespace RawScoreBatchAnalyzer.Realm;

public sealed class BeatmapRealmIndex
{
    private const ulong CurrentRealmSchemaVersion = 51;

    private readonly Dictionary<string, string> beatmapPaths;

    private BeatmapRealmIndex(Dictionary<string, string> beatmapPaths)
    {
        this.beatmapPaths = beatmapPaths;
    }

    public int Count => beatmapPaths.Count;

    public static BeatmapRealmIndex Build(string realmPath, string filesRoot)
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
                    typeof(osu.Game.Scoring.ScoreInfo),
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

            using var realm = Realms.Realm.GetInstance(config);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var beatmap in realm.All<osu.Game.Beatmaps.BeatmapInfo>().AsEnumerable())
            {
                var filePath = FindOsuFilePath(beatmap, NormalizeFilesRoot(filesRoot));
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    continue;

                Add(map, GetStringProperty(beatmap, "MD5Hash"), filePath);
                Add(map, GetStringProperty(beatmap, "OnlineMD5Hash"), filePath);
            }

            return new BeatmapRealmIndex(map);
        }
        finally
        {
            TryDeleteTemporaryRealm(tempRealmPath);
        }
    }

    public bool TryGetBeatmapPath(string hash, out string path)
    {
        return beatmapPaths.TryGetValue(hash, out path!);
    }

    private static void Add(Dictionary<string, string> map, string hash, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(hash) && !map.ContainsKey(hash))
            map[hash] = filePath;
    }

    private static string FindOsuFilePath(object beatmap, string filesRoot)
    {
        var directFile = beatmap.GetType().GetProperty("File")?.GetValue(beatmap);
        var directHash = GetStringProperty(directFile, "Hash");
        var directPath = GetLazerFilePath(filesRoot, directHash);
        if (File.Exists(directPath))
            return directPath;

        var beatmapPath = GetStringProperty(beatmap, "Path");
        var beatmapSet = beatmap.GetType().GetProperty("BeatmapSet")?.GetValue(beatmap);
        var files = beatmapSet?.GetType().GetProperty("Files")?.GetValue(beatmapSet);
        if (files is System.Collections.IEnumerable enumerable)
        {
            foreach (var usage in enumerable.Cast<object>())
            {
                var filename = GetStringProperty(usage, "Filename");
                if (!filename.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(beatmapPath) &&
                    !string.Equals(filename, beatmapPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var file = usage.GetType().GetProperty("File")?.GetValue(usage);
                var hash = GetStringProperty(file, "Hash");
                var path = GetLazerFilePath(filesRoot, hash);
                if (File.Exists(path))
                    return path;
            }
        }

        return string.Empty;
    }

    private static string NormalizeFilesRoot(string path)
    {
        if (File.Exists(Path.Combine(path, "client.realm")) && Directory.Exists(Path.Combine(path, "files")))
            return Path.Combine(path, "files");

        return path;
    }

    private static string GetStringProperty(object? source, string name)
    {
        return source?.GetType().GetProperty(name)?.GetValue(source)?.ToString() ?? string.Empty;
    }

    private static string GetLazerFilePath(string filesRoot, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
            return string.Empty;

        return Path.Combine(filesRoot, hash[0].ToString(), hash[..2], hash);
    }

    private static string CreateTemporaryRealmCopy(string realmPath)
    {
        var tempRealmPath = Path.Combine(Path.GetTempPath(), $"RawScoreBatchAnalyzer-{Guid.NewGuid():N}.realm");
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
            Debug.WriteLine(ex);
        }
    }
}
