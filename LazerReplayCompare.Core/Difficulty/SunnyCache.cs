using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LazerReplayCompare;

/// <summary>
/// The Sunny star rating at 1.0× of every 4K, 6K and 7K map in the library, kept in data/sunny.json
/// by MD5 with the chart file's size and time. Worked out in the background on every core (at below
/// normal priority, and paused while osu! is playing). On later starts and library reads only maps
/// that are new, or whose chart file changed, are worked out again. Other rates are worked out when
/// a map is looked at.
/// </summary>
public sealed class SunnyCache
{
    /// <summary>Changes when the algorithm's port gives different numbers, so old ones are thrown away</summary>
    private const int Algorithm = 1;

    /// <summary>The key counts with dan tables (every key count from 1 to 10 gets a Sunny rating)</summary>
    public static readonly int[] KeyCounts = [4, 6, 7];

    public static bool Rated(int keys) => keys is >= 1 and <= 10;

    /// <summary>A rating and the chart file it came from (size and last write, to notice edits)</summary>
    private sealed class Entry
    {
        public SunnyResult? Result { get; set; }
        public long Size { get; set; }
        public long Time { get; set; }
    }

    private sealed class CacheFile
    {
        public int Algorithm { get; set; }
        public Dictionary<string, Entry> Maps { get; set; } = new();
    }

    /// <summary>Where 0.3.0 kept it</summary>
    private static string OldFile => Path.Combine(AppPaths.Root, "cache", "sunny.json");

    /// <summary>NaN and infinity can't be JSON numbers; written as strings rather than throwing</summary>
    internal static readonly JsonSerializerOptions FileOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SunnyResult?> atRate = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private CancellationTokenSource? run;
    private bool dirty;

    /// <summary>True while osu! needs the CPU (a play): the workers wait</summary>
    public Func<bool>? Busy { get; set; }

    /// <summary>Workers to use: every core by default</summary>
    public int Threads { get; set; } = Environment.ProcessorCount;

    /// <summary>New ratings are in: every few seconds while working, and at the end</summary>
    public event Action? Changed;

    /// <summary>The progress moved (a few times a second while working)</summary>
    public event Action? Progress;

    /// <summary>Where the current (or last) run stands</summary>
    public WorkProgress Status { get; private set; } = WorkProgress.Idle;

    public SunnyCache()
    {
        Load();
    }

    /// <summary>The 1.0× rating, or null when it isn't worked out yet or the chart can't be read</summary>
    public SunnyResult? Get(string md5) => entries.TryGetValue(md5, out var e) ? e.Result : null;

    /// <summary>Maps still to be rated</summary>
    public int Pending => Status.Running ? Status.Total - Status.Done : 0;

    /// <summary>
    /// Rates the library's 4/6/7K maps that are new or whose chart changed since it was rated, in the
    /// background. A run still going is replaced (what it finished is kept).
    /// </summary>
    public void Update(IReadOnlyDictionary<string, MapInfo> maps)
    {
        var list = maps.Values.Where(m => Rated(m.Keys) && m.ChartPath.Length > 0).ToList();
        CancellationTokenSource cts;
        lock (gate)
        {
            run?.Cancel();
            run = cts = new CancellationTokenSource();
        }
        var thread = new Thread(() => Run(list, maps.Count, cts.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "sunny" };
        thread.Start();
    }

    /// <summary>A failure here must not take the app down: it's logged, and the next library read tries again.</summary>
    private void Run(List<MapInfo> maps, int libraryCount, CancellationToken stop)
    {
        try
        {
            RunCore(maps, libraryCount, stop);
        }
        catch (Exception e)
        {
            InternalLogger.Log($"sunny: {e}");
            SetStatus(WorkProgress.Idle);
        }
    }

    private void RunCore(List<MapInfo> maps, int libraryCount, CancellationToken stop)
    {
        var threads = Math.Clamp(Threads, 1, Environment.ProcessorCount);
        SetStatus(new WorkProgress { Running = true, Checking = true, Threads = threads });

        // Which charts are new or changed: one look at each file's size and time
        var todo = new ConcurrentBag<(MapInfo Map, long Size, long Time)>();
        int added = 0, changed = 0;
        try
        {
            Parallel.ForEach(maps, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = stop }, map =>
            {
                long size, time;
                try
                {
                    var file = new FileInfo(map.ChartPath);
                    if (!file.Exists)
                    {
                        if (!entries.ContainsKey(map.Md5)) entries[map.Md5] = new Entry();
                        return;
                    }
                    (size, time) = (file.Length, file.LastWriteTimeUtc.Ticks);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return;
                }
                if (!entries.TryGetValue(map.Md5, out var known))
                {
                    Interlocked.Increment(ref added);
                    todo.Add((map, size, time));
                }
                else if (known.Size == 0 && known.Time == 0 && known.Result != null)
                {
                    // Rated by 0.3.0, which kept no file stamp: take the file as it is now
                    entries[map.Md5] = new Entry { Result = known.Result, Size = size, Time = time };
                    MarkDirty();
                }
                else if (known.Size != size || known.Time != time)
                {
                    Interlocked.Increment(ref changed);
                    todo.Add((map, size, time));
                }
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Maps osu! no longer has
        if (libraryCount > 0)
        {
            var keep = maps.Select(m => m.Md5).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var md5 in entries.Keys)
                if (!keep.Contains(md5) && entries.TryRemove(md5, out _))
                    MarkDirty();
        }

        var work = todo.ToArray();
        var started = Stopwatch.GetTimestamp();
        var done = 0;
        var status = new WorkProgress { Running = work.Length > 0, Total = work.Length, New = added, Changed = changed, Threads = threads };
        SetStatus(status);
        if (work.Length == 0)
        {
            SaveIfDirty();
            Changed?.Invoke();
            return;
        }
        InternalLogger.Log($"sunny: rating {work.Length:N0} maps ({added:N0} new, {changed:N0} changed) on {threads} threads");

        // Long charts first, so no core is left with one big chart at the end
        Array.Sort(work, (a, b) => b.Size.CompareTo(a.Size));
        var next = -1;
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                int i;
                while (!stop.IsCancellationRequested && (i = Interlocked.Increment(ref next)) < work.Length)
                {
                    while (Busy?.Invoke() == true && !stop.IsCancellationRequested) Thread.Sleep(500);
                    var (map, size, time) = work[i];
                    SunnyResult? result = null;
                    try
                    {
                        result = Rate(map.ChartPath, 1.0);
                    }
                    catch (Exception e)
                    {
                        // A chart the port can't take is left unrated, not the app closed
                        if (Interlocked.Increment(ref failures) <= 20) InternalLogger.Log($"sunny: {map.ChartPath}: {e.GetType().Name}: {e.Message}");
                    }
                    // Which charts can't be rated (an OD past ~21.3 breaks the formula, ...): the log says
                    if (result == null && Interlocked.Increment(ref unrated) <= 20) InternalLogger.Log($"sunny: no rating for {map.ChartPath}");
                    entries[map.Md5] = new Entry { Result = result, Size = size, Time = time };
                    Interlocked.Increment(ref done);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"sunny {t + 1}" };
            workers[t].Start();
        }

        // Report while the workers run; save now and then so a closed app keeps what's done
        var lastChanged = Stopwatch.GetTimestamp();
        var lastSave = lastChanged;
        while (workers.Any(w => w.IsAlive))
        {
            workers.First(w => w.IsAlive).Join(250);
            var finished = Volatile.Read(ref done);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var paused = Busy?.Invoke() == true;
            SetStatus(status with
            {
                Done = finished,
                Paused = paused,
                Remaining = finished > 0 ? elapsed * ((double)(work.Length - finished) / finished) : null,
            });
            if (finished > 0) MarkDirty();
            if (Stopwatch.GetElapsedTime(lastChanged).TotalSeconds >= 10)
            {
                lastChanged = Stopwatch.GetTimestamp();
                Changed?.Invoke();
            }
            if (Stopwatch.GetElapsedTime(lastSave).TotalSeconds >= 30)
            {
                lastSave = Stopwatch.GetTimestamp();
                SaveIfDirty();
            }
        }
        SaveIfDirty();
        if (stop.IsCancellationRequested) return;
        var took = Stopwatch.GetElapsedTime(started);
        SetStatus(status with { Running = false, Done = work.Length, Took = took });
        Changed?.Invoke();
        InternalLogger.Log($"sunny: rated {work.Length:N0} maps in {took.TotalSeconds:0.0} s ({entries.Count:N0} known)");
    }

    private int failures, unrated;

    private void SetStatus(WorkProgress status)
    {
        Status = status;
        Progress?.Invoke();
    }

    private static SunnyResult? Rate(string chartPath, double rate, string? odFlag = null, string? cvtFlag = null)
    {
        try
        {
            return File.Exists(chartPath) ? Sunny.Calculate(File.ReadAllText(chartPath), rate, odFlag, cvtFlag: cvtFlag) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The rating at another rate (and OD, or with Hold Off / Invert), worked out once per map and kept in memory only (off the UI thread).</summary>
    public Task<SunnyResult?> AtRateAsync(MapInfo map, double rate, string? odFlag = null, string? cvtFlag = null)
    {
        if (Math.Abs(rate - 1.0) < 1e-9 && odFlag == null && cvtFlag == null && entries.ContainsKey(map.Md5)) return Task.FromResult(Get(map.Md5));
        var key = $"{map.Md5}|{Math.Round(rate, 2).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}|{odFlag}|{cvtFlag}";
        if (atRate.TryGetValue(key, out var known)) return Task.FromResult(known);
        return Task.Run(() =>
        {
            SunnyResult? r = null;
            try
            {
                r = Rate(map.ChartPath, rate, odFlag, cvtFlag);
            }
            catch (Exception e)
            {
                InternalLogger.Log($"sunny: {map.ChartPath} at {rate}x: {e.Message}");
            }
            atRate[key] = r;
            return r;
        });
    }

    // ------------------------------------------------------------ the file

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.File("sunny.json")))
            {
                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(AppPaths.File("sunny.json")), FileOptions);
                if (file?.Algorithm == Algorithm)
                    foreach (var (md5, e) in file.Maps)
                        entries[md5] = e;
                return;
            }
            // 0.3.0's cache/sunny.json: {"Version":1,"Maps":{md5: result}}, without file stamps
            if (File.Exists(OldFile) && JsonNode.Parse(File.ReadAllText(OldFile)) is JsonObject old && old["Version"]?.GetValue<int>() == 1
                && old["Maps"] is JsonObject list)
            {
                foreach (var (md5, node) in list)
                    entries[md5] = new Entry { Result = node?.Deserialize<SunnyResult>() };
                dirty = true;
                SaveIfDirty();
                File.Delete(OldFile);
            }
        }
        catch (Exception e)
        {
            // An unreadable file only means rating again
            InternalLogger.Log($"sunny ratings: {e.Message}");
        }
    }

    private void MarkDirty()
    {
        lock (gate) dirty = true;
    }

    private void SaveIfDirty()
    {
        lock (gate)
        {
            if (!dirty) return;
            dirty = false;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var tmp = AppPaths.File("sunny.json") + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile { Algorithm = Algorithm, Maps = new Dictionary<string, Entry>(entries) }, FileOptions));
                File.Move(tmp, AppPaths.File("sunny.json"), true);
            }
            catch (Exception e)
            {
                InternalLogger.Log($"sunny ratings: {e.Message}");
            }
        }
    }
}

/// <summary>Where the background rating stands, for the window</summary>
public sealed record WorkProgress
{
    public static readonly WorkProgress Idle = new();

    public bool Running { get; init; }
    /// <summary>Still looking at which charts are new or changed</summary>
    public bool Checking { get; init; }
    /// <summary>Waiting for osu!'s play to end</summary>
    public bool Paused { get; init; }
    public int Total { get; init; }
    public int Done { get; init; }
    public int New { get; init; }
    public int Changed { get; init; }
    public int Threads { get; init; }
    public TimeSpan? Remaining { get; init; }
    /// <summary>How long the finished run took</summary>
    public TimeSpan? Took { get; init; }
    public double Fraction => Total > 0 ? (double)Done / Total : 0;
}
