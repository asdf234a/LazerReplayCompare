using System.Text.Json;

namespace LazerReplayCompare;

/// <summary>
/// Every mania difficulty installed in osu!lazer and osu!stable, and their collections, so maps that were never played
/// are listed too. Read in the background (osu!lazer's database takes a few seconds) and kept in library.json, which is
/// shown at once on the next start.
/// </summary>
public sealed class MapLibrary
{
    private sealed class CacheFile
    {
        public DateTime Read { get; set; }
        public List<MapInfo> Maps { get; set; } = [];
        public List<OsuCollection> Collections { get; set; } = [];
    }

    private static string cachePath => AppPaths.File("library.json");

    private readonly object gate = new();
    private Dictionary<string, MapInfo> maps = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<OsuCollection> collections = [];
    private int loading;
    private DateTime lastRefresh = DateTime.MinValue;

    public bool Loading => Volatile.Read(ref loading) == 1;

    /// <summary>What the last read found or why it failed.</summary>
    public string Status { get; private set; } = "Library not read yet";

    /// <summary>The library changed (read or read again). Raised on a background thread.</summary>
    public event Action? Changed;

    public IReadOnlyDictionary<string, MapInfo> Maps
    {
        get
        {
            lock (gate) return maps;
        }
    }

    public IReadOnlyList<OsuCollection> Collections
    {
        get
        {
            lock (gate) return collections;
        }
    }

    public MapInfo? Find(string md5)
    {
        lock (gate) return maps.GetValueOrDefault(md5);
    }

    /// <summary>Shows the kept library (read off the UI thread), then reads the games' own.</summary>
    public void Start(string lazerRoot, string stableRoot)
    {
        Task.Run(() =>
        {
            try
            {
                if (File.Exists(cachePath) && JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath)) is { } cache)
                    set(cache.Maps, cache.Collections, $"{cache.Maps.Count:N0} maps (from the last read)");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                InternalLogger.Log(ex);
            }

            Refresh(lazerRoot, stableRoot);
        });
    }

    /// <summary>
    /// Reads the games' libraries again in the background. <paramref name="minInterval"/> keeps automatic refreshes (a
    /// selected map the library doesn't know yet) from running back to back. False when no read started.
    /// </summary>
    public bool Refresh(string lazerRoot, string stableRoot, TimeSpan? minInterval = null)
    {
        if (minInterval is { } wait && DateTime.Now - lastRefresh < wait)
            return false;
        if (Interlocked.Exchange(ref loading, 1) == 1)
            return false;

        lastRefresh = DateTime.Now;
        Task.Run(() =>
        {
            try
            {
                load(lazerRoot, stableRoot);
            }
            catch (Exception ex)
            {
                Status = $"Library: {ex.Message}";
                InternalLogger.Log(ex);
            }
            finally
            {
                Interlocked.Exchange(ref loading, 0);
                Changed?.Invoke();
            }
        });
        return true;
    }

    private void load(string lazerRoot, string stableRoot)
    {
        var found = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        var lists = new List<OsuCollection>();
        var notes = new List<string>();

        var realm = Path.Combine(lazerRoot ?? string.Empty, "client.realm");
        if (!string.IsNullOrWhiteSpace(lazerRoot) && File.Exists(realm))
        {
            try
            {
                var (lazerMaps, lazerCollections) = ReplayFinder.ReadLibrary(realm, Path.Combine(lazerRoot, "files"));
                foreach (var map in lazerMaps)
                    found.TryAdd(map.Md5, map);
                lists.AddRange(lazerCollections);
                notes.Add($"lazer {lazerMaps.Count:N0}");
            }
            catch (Exception ex)
            {
                notes.Add("lazer unreadable");
                InternalLogger.Log(ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(stableRoot) && File.Exists(Path.Combine(stableRoot, "osu!.db")))
        {
            try
            {
                var stableMaps = StableDatabase.ReadMaps(stableRoot);
                foreach (var map in stableMaps)
                {
                    // in both games: osu!lazer's entry stays, and says so
                    if (found.TryGetValue(map.Md5, out var known))
                    {
                        known.Clients = "lazer+stable";
                        if (known.Status is "" or "none" && map.Status.Length > 0)
                            known.Status = map.Status;
                    }
                    else
                        found[map.Md5] = map;
                }

                lists.AddRange(StableDatabase.ReadCollections(stableRoot));
                notes.Add($"stable {stableMaps.Count:N0}");
            }
            catch (Exception ex)
            {
                notes.Add("stable unreadable");
                InternalLogger.Log(ex);
            }
        }

        if (notes.Count == 0)
        {
            int known = Maps.Count;
            Status = known > 0 ? $"{known:N0} maps from the last read (the games' libraries weren't found now)" : "osu!'s beatmap library not found";
            return;
        }

        set(found.Values, lists, $"{found.Count:N0} mania maps ({string.Join(", ", notes)})");
        try
        {
            var temporary = cachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CacheFile { Read = DateTime.Now, Maps = found.Values.ToList(), Collections = lists }));
            File.Move(temporary, cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }

    private void set(IEnumerable<MapInfo> list, IReadOnlyList<OsuCollection> osuCollections, string status)
    {
        var dictionary = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in list)
            dictionary.TryAdd(map.Md5, map);

        lock (gate)
        {
            maps = dictionary;
            collections = osuCollections;
        }

        Status = status;
        Changed?.Invoke();
    }
}
