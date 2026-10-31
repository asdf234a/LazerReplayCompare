using System.Text.Json;

namespace LazerReplayCompare;

/// <summary>
/// osu!lazer's replays, kept by the app between runs (lazer-replays.json and lazer-beatmaps.json in its settings folder).
/// <para>
/// When an osu!lazer folder is opened the kept list is shown straight away, and osu!lazer's database is read once (from a
/// copy, see <see cref="ReplayFinder"/>). After that the files folder is watched instead: osu!lazer writes every replay
/// it keeps there (played, imported or downloaded; under a temporary name, then renamed) and deletes the ones it
/// removes, so new replays are read from their file as soon as they appear. The rescan button, or the watcher losing
/// events (a large import, once it has gone quiet), reads the database again. When the database can't be read (a newer
/// osu!lazer with another schema) the kept list and the watcher carry on.
/// </para>
/// </summary>
public sealed class LazerReplayStore : IDisposable
{
    private const int format = 1;
    private static readonly TimeSpan save_delay = TimeSpan.FromSeconds(2);
    // after the watcher lost events, how long the files folder must stay quiet before the database is read again
    private static readonly TimeSpan resync_quiet = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions json_options = new() { WriteIndented = false };

    /// <summary>A beatmap's replays changed (its MD5), or possibly every beatmap's (null: the database was read).</summary>
    public event Action<string?>? Changed;

    private readonly string replaysFile;
    private readonly string beatmapsFile;
    private readonly object sync = new();
    private readonly object writing = new();
    private readonly Timer saveTimer;
    private readonly Timer resyncTimer;
    private int resyncPending;
    // what each saved file holds (SHA-256 of its bytes), so an unchanged list isn't written again
    private byte[]? replaysSaved, beatmapsSaved;

    private string root = string.Empty;
    private string filesRoot = string.Empty;
    // bumped when another folder is opened, so work on the old one is dropped
    private int generation;

    private Dictionary<string, StoredReplay> replays = new(StringComparer.OrdinalIgnoreCase);    // by replay file
    private Dictionary<string, StoredBeatmap> beatmaps = new(StringComparer.OrdinalIgnoreCase);  // by beatmap MD5
    // replays seen in the files folder whose beatmap file isn't known yet (MD5 by replay file): read when it is looked at
    private readonly Dictionary<string, string> waiting = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? watcher;

    private bool reading;
    private bool readAgain;
    // the kept list is loaded by the next database read (in the background, before reading)
    private bool keptToLoad;
    // what the watcher added (or deleted: null) while the database was being read; it may be newer than the copy
    private readonly Dictionary<string, StoredReplay?> seenWhileReading = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">Where the kept list is saved.</param>
    public LazerReplayStore(string folder)
    {
        replaysFile = Path.Combine(folder, "lazer-replays.json");
        beatmapsFile = Path.Combine(folder, "lazer-beatmaps.json");
        saveTimer = new Timer(_ => save(beatmapsToo: false));
        resyncTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref resyncPending, 0);
            Refresh();
        });
        ReplayFinder.DeleteLeftoverCopies();
    }

    /// <summary>The database is being read and nothing is kept yet (the list fills in once it is read).</summary>
    public bool IsFillingIn
    {
        get
        {
            lock (sync)
                return reading && replays.Count == 0;
        }
    }

    /// <summary>
    /// Uses this osu!lazer folder: the kept list (if it is this folder's), the database read once, then the watcher. The
    /// files are read in the background; <see cref="Changed"/> follows each step.
    /// </summary>
    public void Open(string lazerRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lazerRoot.Trim()));
        lock (sync)
        {
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
                return;

            watcher?.Dispose();
            watcher = null;
            generation++;
            root = full;
            filesRoot = Path.Combine(full, "files");
            replays = new Dictionary<string, StoredReplay>(StringComparer.OrdinalIgnoreCase);
            beatmaps = new Dictionary<string, StoredBeatmap>(StringComparer.OrdinalIgnoreCase);
            waiting.Clear();
            keptToLoad = true;
            watch();
        }

        lock (writing)
            replaysSaved = beatmapsSaved = null;

        Refresh();
        Changed?.Invoke(null);
    }

    /// <summary>Reads osu!lazer's database again (in the background); <see cref="Changed"/> follows.</summary>
    public void Refresh()
    {
        int scan;
        string lazerRoot, realmPath, files;
        bool loadKept;
        lock (sync)
        {
            if (root.Length == 0)
                return;

            if (reading)
            {
                readAgain = true;
                return;
            }

            reading = true;
            seenWhileReading.Clear();
            scan = generation;
            lazerRoot = root;
            realmPath = Path.Combine(root, "client.realm");
            files = filesRoot;
            loadKept = keptToLoad;
            keptToLoad = false;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (loadKept)
                {
                    try
                    {
                        load(lazerRoot, scan);
                    }
                    catch (Exception ex)
                    {
                        // nothing kept: the database read below fills the list
                        InternalLogger.Log(ex);
                    }

                    Changed?.Invoke(null);
                }

                var (read, readBeatmaps) = ReplayFinder.ReadDatabase(realmPath, files);
                lock (sync)
                {
                    if (scan != generation)
                        return;

                    var next = new Dictionary<string, StoredReplay>(StringComparer.OrdinalIgnoreCase);
                    foreach (var replay in read)
                        next.TryAdd(replay.Entry.FilePath, replay);

                    foreach (var (path, seen) in seenWhileReading)
                    {
                        if (seen == null)
                            next.Remove(path);
                        else
                            next[path] = seen;
                    }

                    replays = next;
                    beatmaps = readBeatmaps;
                    foreach (var path in waiting.Keys.Where(next.ContainsKey).ToList())
                        waiting.Remove(path);
                }

                save(beatmapsToo: true);
            }
            catch (Exception ex)
            {
                // the kept list stays: a newer osu!lazer's database, a file that couldn't be copied...
                InternalLogger.Log(ex);
            }
            finally
            {
                bool again;
                lock (sync)
                {
                    reading = false;
                    again = readAgain;
                    readAgain = false;
                    seenWhileReading.Clear();
                }

                readWaiting(null);
                Changed?.Invoke(null);
                // the database read (every score and beatmap, through Realm) leaves a lot behind
                IdleCollector.AfterHeavyWork();
                if (again)
                    Refresh();
            }
        });
    }

    /// <summary>
    /// The beatmap's replays, best first, and its background file (empty when unknown). <paramref name="beatmapPath"/>
    /// (the .osu file tosu reports) reads replays of a beatmap osu!lazer got after the database was last read.
    /// </summary>
    public (List<ReplayEntry> Replays, string BackgroundPath) Find(string md5, string? beatmapPath)
    {
        readWaiting(md5, beatmapPath);

        lock (sync)
        {
            var list = replays.Values
                              .Where(replay => replay.Beatmaps.Contains(md5, StringComparer.OrdinalIgnoreCase))
                              .Select(replay => replay.Entry)
                              .OrderByDescending(entry => entry.Score)
                              .ToList();

            var background = ReplayFinder.GetLazerFilePath(filesRoot, beatmaps.GetValueOrDefault(md5)?.Background);
            return (list, File.Exists(background) ? background : string.Empty);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            watcher?.Dispose();
            watcher = null;
        }

        resyncTimer.Dispose();
        saveTimer.Dispose();
        save(beatmapsToo: false);
    }

    private static void restart(Timer timer, TimeSpan delay)
    {
        try
        {
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // closing
        }
    }

    // --- watching the files folder ---

    private void watch()
    {
        if (!Directory.Exists(filesRoot))
            return;

        try
        {
            watcher = new FileSystemWatcher(filesRoot)
            {
                IncludeSubdirectories = true,
                // (folders too: a new folder is looked into, see added)
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            int scan = generation;
            watcher.Created += (_, e) => added(e.FullPath, scan);
            watcher.Renamed += (_, e) => added(e.FullPath, scan);
            watcher.Deleted += (_, e) => deleted(e.FullPath, scan);
            // events were lost (thousands of files at once): the database is read instead, once the import is over
            watcher.Error += (_, e) =>
            {
                InternalLogger.Log(e.GetException());
                Interlocked.Exchange(ref resyncPending, 1);
                restart(resyncTimer, resync_quiet);
            };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }

    /// <summary>A file appeared (osu!lazer renames its finished files into place): kept if it is a replay.</summary>
    private void added(string path, int scan)
    {
        stillBusy();

        // A new folder: its first file can be written before the folder itself is watched, so it is looked into once.
        if (Directory.Exists(path))
        {
            _ = Task.Delay(250).ContinueWith(_ =>
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        added(file, scan);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    InternalLogger.Log(ex);
                }
            }, TaskScheduler.Default);
            return;
        }

        // osu!lazer's half-written files ("_{hash}_{guid}")
        if (Path.GetFileName(path).StartsWith('_') || !File.Exists(path))
            return;

        string beatmapPath;
        lock (sync)
        {
            if (scan != generation || replays.ContainsKey(path))
                return;
        }

        var md5 = ReplayFinder.ReplayBeatmapOf(path);
        if (md5 == null)
            return;

        lock (sync)
        {
            if (scan != generation)
                return;

            beatmapPath = ReplayFinder.GetLazerFilePath(filesRoot, beatmaps.GetValueOrDefault(md5)?.File);
            waiting[path] = md5;
        }

        // read off the watcher's thread (decoding takes a while), or when the beatmap is looked at if its file isn't known
        if (beatmapPath.Length == 0)
        {
            Changed?.Invoke(md5);
            return;
        }

        _ = Task.Run(() =>
        {
            readWaiting(md5, beatmapPath);
            Changed?.Invoke(md5);
        });
    }

    /// <summary>While a database read waits for the files folder to go quiet, any event pushes it back.</summary>
    private void stillBusy()
    {
        if (Volatile.Read(ref resyncPending) == 1)
            restart(resyncTimer, resync_quiet);
    }

    private void deleted(string path, int scan)
    {
        stillBusy();

        string[] affected;
        lock (sync)
        {
            if (scan != generation)
                return;

            waiting.Remove(path);
            if (!replays.Remove(path, out var removed))
                return;

            if (reading)
                seenWhileReading[path] = null;
            affected = removed.Beatmaps;
        }

        restart(saveTimer, save_delay);
        foreach (var md5 in affected)
            Changed?.Invoke(md5);
    }

    /// <summary>
    /// Reads waiting replays: of one beatmap (<paramref name="md5"/>, with <paramref name="beatmapPath"/> when its file
    /// isn't known), or of every beatmap whose file is known (null).
    /// </summary>
    private void readWaiting(string? md5, string? beatmapPath = null)
    {
        List<(string Path, string Md5, string BeatmapPath)> toRead;
        int scan;
        lock (sync)
        {
            scan = generation;
            toRead = waiting.Where(item => md5 == null || string.Equals(item.Value, md5, StringComparison.OrdinalIgnoreCase))
                            .Select(item =>
                            {
                                var known = ReplayFinder.GetLazerFilePath(filesRoot, beatmaps.GetValueOrDefault(item.Value)?.File);
                                return (item.Key, item.Value, File.Exists(known) ? known : beatmapPath ?? string.Empty);
                            })
                            .Where(item => File.Exists(item.Item3))
                            .ToList();
        }

        bool any = false;
        foreach (var (path, replayMd5, osu) in toRead)
        {
            StoredReplay replay;
            try
            {
                replay = new StoredReplay(new[] { replayMd5 }, ReplayFinder.ReadReplay(path, osu));
            }
            catch (Exception ex)
            {
                // not a replay this app can read (or one too large to be real): left to the next database read
                InternalLogger.Log(ex);
                lock (sync)
                {
                    if (scan == generation)
                        waiting.Remove(path);
                }

                continue;
            }

            lock (sync)
            {
                if (scan != generation || !waiting.Remove(path))
                    continue;

                replays[path] = replay;
                if (reading)
                    seenWhileReading[path] = replay;
                any = true;
            }

            if (md5 == null)
                Changed?.Invoke(replayMd5);
        }

        if (any)
        {
            restart(saveTimer, save_delay);
            // (each replay was decoded whole, frames included, to list it)
            IdleCollector.AfterHeavyWork();
        }
    }

    // --- keeping the list between runs ---

    private sealed record ReplaysFile(int Format, string Root, List<StoredReplay> Replays);

    private sealed record BeatmapsFile(int Format, string Root, Dictionary<string, StoredBeatmap> Beatmaps);

    /// <summary>
    /// The kept list, if it is <paramref name="lazerRoot"/>'s (replays whose file has gone are left out), added to what the
    /// watcher may have found meanwhile.
    /// </summary>
    private void load(string lazerRoot, int scan)
    {
        var (kept, keptHash) = read<ReplaysFile>(replaysFile);
        var (maps, mapsHash) = read<BeatmapsFile>(beatmapsFile);

        var keptReplays = kept is { Format: format, Replays: { } list } && string.Equals(kept.Root, lazerRoot, StringComparison.OrdinalIgnoreCase)
            ? list.Where(replay => replay is { Entry.FilePath: { } file } && File.Exists(file)).ToList()
            : new List<StoredReplay>();
        var keptBeatmaps = maps is { Format: format, Beatmaps: { } found } && string.Equals(maps.Root, lazerRoot, StringComparison.OrdinalIgnoreCase)
            ? found
            : null;

        lock (sync)
        {
            if (scan != generation)
                return;

            foreach (var replay in keptReplays)
                replays.TryAdd(replay.Entry.FilePath, replay);
            if (keptBeatmaps != null && beatmaps.Count == 0)
                beatmaps = new Dictionary<string, StoredBeatmap>(keptBeatmaps, StringComparer.OrdinalIgnoreCase);
        }

        // (unless a save already wrote them since the folder was opened)
        lock (writing)
        {
            if (keptHash != null)
                replaysSaved ??= keptHash;
            if (mapsHash != null)
                beatmapsSaved ??= mapsHash;
        }
    }

    /// <summary>The file's contents and the hash of its bytes (parsed straight from disk: the files run to tens of megabytes).</summary>
    private static (T? Value, byte[]? Hash) read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return (null, null);

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var hashing = new HashingStream(file);
            var value = JsonSerializer.Deserialize<T>(hashing, json_options);
            // (the serializer stops at the end of the value; whatever follows is hashed too, as the file is compared whole)
            hashing.CopyTo(Stream.Null);
            return (value, hashing.Hash);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            InternalLogger.Log(ex);
            return (null, null);
        }
    }

    private void save(bool beatmapsToo)
    {
        string lazerRoot;
        List<StoredReplay> keptReplays;
        List<KeyValuePair<string, StoredBeatmap>>? keptBeatmaps = null;
        lock (sync)
        {
            if (root.Length == 0)
                return;

            lazerRoot = root;
            keptReplays = replays.Values.ToList();
            if (beatmapsToo)
                keptBeatmaps = beatmaps.ToList();
        }

        // in a fixed order, so the same list is the same file
        var kept = new ReplaysFile(format, lazerRoot, keptReplays.OrderBy(replay => replay.Entry.FilePath, StringComparer.Ordinal).ToList());
        var maps = keptBeatmaps == null
            ? null
            : new BeatmapsFile(format, lazerRoot, keptBeatmaps.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Value));

        lock (writing)
        {
            write(replaysFile, kept, ref replaysSaved);
            if (maps != null)
                write(beatmapsFile, maps, ref beatmapsSaved);
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> unless the file already holds it (<paramref name="saved"/>: its hash). Serialized
    /// straight to a temporary file in small chunks: the replay list runs to tens of megabytes, and building it in
    /// memory first left that much on the large object heap after every save.
    /// </summary>
    private static void write<T>(string path, T value, ref byte[]? saved)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            byte[] hash;
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var hashing = new HashingStream(file))
            {
                JsonSerializer.Serialize(hashing, value, json_options);
                hashing.Flush();
                hash = hashing.Hash;
            }

            if (saved != null && hash.AsSpan().SequenceEqual(saved))
            {
                File.Delete(temporary);
                return;
            }

            File.Move(temporary, path, overwrite: true);
            saved = hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }
}
