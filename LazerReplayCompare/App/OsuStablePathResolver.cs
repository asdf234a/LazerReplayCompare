namespace LazerReplayCompare;

/// <summary>Finds the osu!stable install folder (the one with scores.db / osu!.db and Data/r).</summary>
public static class OsuStablePathResolver
{
    public static string? Resolve(TosuSnapshot? snapshot, string? savedPath = null)
    {
        if (IsOsuStableRoot(savedPath))
            return savedPath;

        var candidates = new List<string>();
        if (snapshot != null)
        {
            // tosu reports the game folder for osu!stable; the Songs folder usually sits inside it
            candidates.Add(snapshot.GameFolder);
            candidates.Add(snapshot.SongsFolder);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "osu!"));

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var current = candidate;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (IsOsuStableRoot(current))
                    return current;

                current = Directory.GetParent(current)?.FullName;
            }
        }

        return null;
    }

    public static bool IsOsuStableRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            (File.Exists(Path.Combine(path, "scores.db")) || File.Exists(Path.Combine(path, "osu!.db")));
    }
}
