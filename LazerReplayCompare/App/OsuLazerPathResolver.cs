namespace LazerReplayCompare;

public static class OsuLazerPathResolver
{
    public static string? Resolve(TosuSnapshot? snapshot, string? savedPath = null)
    {
        if (IsOsuLazerRoot(savedPath))
            return savedPath;

        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "osulazer"));

        if (snapshot != null)
        {
            AddPathCandidate(candidates, snapshot.SongsFolder);
            AddPathCandidate(candidates, snapshot.BeatmapFolder);
            if (!string.IsNullOrWhiteSpace(snapshot.SongsFolder) && !string.IsNullOrWhiteSpace(snapshot.BeatmapFile))
                AddPathCandidate(candidates, Path.Combine(snapshot.SongsFolder, snapshot.BeatmapFile));
            AddPathCandidate(candidates, snapshot.BeatmapFile);
        }

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = FindOsuLazerRootFrom(candidate);
            if (root != null)
                return root;
        }

        return null;
    }

    public static bool IsOsuLazerRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(Path.Combine(path, "client.realm")) &&
            Directory.Exists(Path.Combine(path, "files"));
    }

    private static void AddPathCandidate(List<string> candidates, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            candidates.Add(path);
    }

    private static string? FindOsuLazerRootFrom(string path)
    {
        var current = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (IsOsuLazerRoot(current))
                return current;

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
}
