namespace LazerReplayCompare;

/// <summary>
/// Where the app keeps everything it writes: one folder, "data", next to the exe, so the app folder can be moved or
/// copied as a whole. Before 3.0 the files were in %LocalAppData%\LazerReplayCompare; they are copied over once.
/// When the app's folder can't be written to (Program Files), the old place is used instead.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> root = new(resolve);

    /// <summary>The data folder (created).</summary>
    public static string Root => root.Value;

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Metrics => Path.Combine(Root, "metrics.json");

    /// <summary>Things that can be made again: copies of osu!lazer's database while it is read, the dan engine's browser profile.</summary>
    public static string Cache => folder("cache");

    /// <summary>Replays downloaded from osu!'s leaderboards.</summary>
    public static string Downloads => folder("replays");

    public static string Logs => folder("logs");

    /// <summary>A file in the data folder.</summary>
    public static string File(string name) => Path.Combine(Root, name);

    private static string folder(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string legacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LazerReplayCompare");

    private static string resolve()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            System.IO.File.WriteAllText(probe, string.Empty);
            System.IO.File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
            Directory.CreateDirectory(legacyRoot);
            return legacyRoot;
        }

        migrate(portable);
        return portable;
    }

    /// <summary>Copies the files of an earlier version, once (the old ones are left where they are).</summary>
    private static void migrate(string portable)
    {
        try
        {
            if (!Directory.Exists(legacyRoot) || System.IO.File.Exists(Path.Combine(portable, "settings.json")))
                return;

            foreach (var file in Directory.EnumerateFiles(legacyRoot))
            {
                var target = Path.Combine(portable, Path.GetFileName(file));
                if (!System.IO.File.Exists(target))
                    System.IO.File.Copy(file, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }
}
