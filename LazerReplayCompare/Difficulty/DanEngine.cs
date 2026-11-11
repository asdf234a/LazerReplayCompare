using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LazerReplayCompare;

/// <summary>The analyser's dan estimate for a chart, as its card shows it.</summary>
public sealed record DanEstimate
{
    /// <summary>Sunny SR (the analyser's star capsule)</summary>
    public double Sunny { get; init; }
    public double LnRatio { get; init; }
    public int Columns { get; init; }
    /// <summary>The RC part of the label ("Reform 5 mid", "Delta mid/high"), or null</summary>
    public string? Rc { get; init; }
    /// <summary>The LN part ("LN 8 high"), or null when the card shows none</summary>
    public string? Ln { get; init; }
    /// <summary>The estimator's numeric difficulty (6.81), or null when it gives none</summary>
    public double? Numeric { get; init; }
    /// <summary>The estimator that gave it: Roxy, Azusa, Azusa+Companella, Polaris, Sunny ...</summary>
    public string Estimator { get; init; } = "";
    /// <summary>The card would say VIBRO</summary>
    public bool Vibro { get; init; }
    /// <summary>
    /// SunnyWindow's LN star rating (the chart's Sunny SR without its rice parts), which the LN dan is
    /// looked up from; the analyser shows it as "LN5.95*" with its LN Star Rating option. Null when
    /// there's no LN part.
    /// </summary>
    public double? LnStar { get; init; }

    /// <summary>"Reform 5 mid || LN 8 high", or the RC part alone</summary>
    [JsonIgnore]
    public string Label => Ln != null ? $"{Rc} || {Ln}" : Rc ?? "";
}

/// <summary>
/// The dan estimates of osumania_map_analyser (analyser/ next to the exe), for every 4K, 6K and 7K
/// map. The analyser is JavaScript with WebAssembly parts (Etterna's MinaCalc, an ONNX model), so it
/// runs in a headless Edge or Chrome that this class starts and stops: the browser opens
/// /analyser/lr2hp/ on the engine's own <see cref="DanEngineServer"/>, which runs one Web Worker per
/// core and takes charts over the websocket. Results are kept in dans.json in the data folder with
/// each chart's size and time, so later runs only do new and changed charts. The browser runs at below
/// normal priority, gets no new charts while osu! is playing, and is closed when there's nothing left
/// to do.
/// <para>How to use it:</para>
/// <code>
/// var dans = new DanEngine();                  // reads dans.json; starts nothing yet
/// dans.Busy = () => aPlayIsGoingOn;            // optional: no new charts go out while true
/// dans.Changed += () => ...;                   // new estimates are in (any thread): read them again
/// dans.Progress += () => ... dans.Status ...;  // the progress moved (any thread)
/// dans.Update(library);                        // md5 -> MapInfo, after every library read; returns at once
/// var estimate = dans.Get(md5);                // null until worked out (or when it can't be)
/// if (dans.Unavailable is { } why) ...         // no browser, no analyser folder ...: say why
/// dans.Dispose();                              // when the app closes: stops the browser, saves, closes the server
/// </code>
/// <para>
/// There is no Start: the server and the browser are started by the first <see cref="Update"/> (or
/// <see cref="AtRateAsync"/>) that has something to work out. The events come from background threads.
/// </para>
/// </summary>
public sealed class DanEngine : IDisposable
{
    /// <summary>Raise when analyser/ or analyser/lr2hp/ changes, so cached estimates are redone</summary>
    public const int EngineVersion = 1;

    /// <summary>The analyser's estimators; Mixed is its default</summary>
    public static readonly string[] Estimators = ["Mixed", "Azusa", "Roxy", "Polaris", "Daniel", "Companella", "Sunny"];

    /// <summary>An environment variable naming the browser to use, before Edge and Chrome are looked for</summary>
    public const string BrowserVariable = "LRC_DAN_BROWSER";

    private sealed class Entry
    {
        public DanEstimate? Result { get; set; }
        public long Size { get; set; }
        public long Time { get; set; }
        /// <summary>The engine version and settings it was worked out with</summary>
        public string Key { get; set; } = "";
    }

    private sealed class CacheFile
    {
        public Dictionary<string, Entry> Maps { get; set; } = new();
    }

    private sealed class Job
    {
        public required int Id;
        public required string Text;
        public required JsonObject Settings;
        public MapInfo? Map;
        public long Size, Time;
        public string Key = "";
        public TaskCompletionSource<DanEstimate?>? Waiter;
        public long SentAt;
        /// <summary>Lost with the browser (not answered), so it should be sent again</summary>
        public bool Dropped;
        /// <summary>How many times it was lost; a chart lost twice is sent on its own</summary>
        public int Attempts;
    }

    /// <summary>NaN and infinity can't be JSON numbers; written as strings rather than throwing</summary>
    private static readonly JsonSerializerOptions file_options = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static string DansFile => AppPaths.File("dans.json");

    private readonly DanEngineServer server;
    private readonly int preferredPort;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<DanEstimate?>> atRate = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly ConcurrentDictionary<int, Job> inFlight = new();
    private CancellationTokenSource? run;
    private int nextId;
    private bool dirty;
    private volatile bool disposed;

    private volatile DanEngineServer.Connection? client;
    private int workers;
    private Process? browser;
    private IntPtr job;
    private TaskCompletionSource<bool>? connected;
    private long lastUse;

    /// <param name="port">The port the engine's server asks for first; the next free one is taken when it is in use.</param>
    /// <param name="analyserDir">The analyser's folder; by default "analyser" next to the exe.</param>
    public DanEngine(int port = DanEngineServer.DefaultPort, string? analyserDir = null)
    {
        preferredPort = port;
        server = new DanEngineServer(analyserDir);
        server.MessageReceived += OnServerMessage;
        Load();
        Unavailable = Prerequisites();
        new Thread(IdleWatch) { IsBackground = true, Name = "dan engine idle" }.Start();
    }

    /// <summary>True while osu! needs the CPU (a play): no new charts go out</summary>
    public Func<bool>? Busy { get; set; }

    /// <summary>Web Workers the browser runs (and threads checking chart files): every core by default. Read when the browser starts.</summary>
    public int Workers { get; set; } = Environment.ProcessorCount;

    /// <summary>The engine's server port, or 0 while the server hasn't been started (nothing to work out yet)</summary>
    public int Port => server.Port;

    /// <summary>A browser to use instead of looking for Edge and Chrome</summary>
    public string BrowserPath { get; set; } = "";

    public string Estimator { get; private set; } = "Mixed";
    public bool Extended { get; private set; }

    /// <summary>What the cached results must have been worked out with</summary>
    private string SettingsKey => $"{EngineVersion}|{Estimator}|{(Extended ? "ext" : "default")}";

    /// <summary>Why no estimates can be made (no browser found, the analyser folder is missing ...), or null when fine</summary>
    public string? Unavailable { get; private set; }

    /// <summary>The browser the engine runs in, once found</summary>
    public string BrowserName { get; private set; } = "";

    /// <summary>Where the current (or last) run stands</summary>
    public WorkProgress Status { get; private set; } = WorkProgress.Idle;

    /// <summary>Maps still to be worked out</summary>
    public int Pending => Status.Running ? Status.Total - Status.Done : 0;

    /// <summary>New estimates are in: every few seconds while working, and at the end</summary>
    public event Action? Changed;

    /// <summary>The progress moved</summary>
    public event Action? Progress;

    /// <summary>The estimate at 1.0× with the current settings, or null</summary>
    public DanEstimate? Get(string md5) =>
        entries.TryGetValue(md5, out var e) && e.Key == SettingsKey ? e.Result : null;

    /// <summary>Changes the estimator or the dan table; the library's estimates are redone in the background.</summary>
    public void Configure(string estimator, bool extended, IReadOnlyDictionary<string, MapInfo>? maps)
    {
        estimator = Estimators.Contains(estimator) ? estimator : "Mixed";
        if (estimator == Estimator && extended == Extended) return;
        Estimator = estimator;
        Extended = extended;
        atRate.Clear();
        Changed?.Invoke();
        if (maps != null) Update(maps);
    }

    private JsonObject SettingsJson(double rate, string? odFlag = null, string? cvtFlag = null) => new()
    {
        ["estimatorAlgorithm"] = Estimator,
        ["extendedEstimationRange"] = Extended,
        ["speedRate"] = rate,
        ["odFlag"] = odFlag,
        ["cvtFlag"] = cvtFlag,
    };

    // ------------------------------------------------------------ the library

    /// <summary>
    /// Works out the library's 4/6/7K maps that are new, changed, or were done with other settings,
    /// in the background. A run still going is replaced (what it finished is kept).
    /// </summary>
    public void Update(IReadOnlyDictionary<string, MapInfo> maps)
    {
        if (disposed) return;
        var list = maps.Values.Where(m => SunnyCache.Rated(m.Keys) && m.ChartPath.Length > 0).ToList();
        var count = maps.Count;
        CancellationTokenSource cts;
        lock (gate)
        {
            run?.Cancel();
            run = cts = new CancellationTokenSource();
        }
        new Thread(() => Run(list, count, cts.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "dan engine" }.Start();
    }

    /// <summary>A failure here must not take the app down: it's logged, and the next library read tries again.</summary>
    private void Run(List<MapInfo> maps, int libraryCount, CancellationToken stop)
    {
        try
        {
            RunCore(maps, libraryCount, stop);
        }
        catch (OperationCanceledException)
        {
            // a newer run took over
        }
        catch (Exception e)
        {
            InternalLogger.Log($"dan engine: {e}");
            SetStatus(WorkProgress.Idle);
        }
    }

    private void RunCore(List<MapInfo> maps, int libraryCount, CancellationToken stop)
    {
        var key = SettingsKey;
        SetStatus(new WorkProgress { Running = true, Checking = true });
        var todo = new ConcurrentBag<(MapInfo Map, long Size, long Time)>();
        int added = 0, changed = 0;
        try
        {
            Parallel.ForEach(maps, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Workers, 1, Environment.ProcessorCount), CancellationToken = stop }, map =>
            {
                long size, time;
                try
                {
                    var file = new FileInfo(map.ChartPath);
                    if (!file.Exists) return;
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
                else if (known.Size != size || known.Time != time || known.Key != key)
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

        // Long charts first, so no worker is left with one big chart at the end
        var work = todo.OrderByDescending(w => w.Size).ToArray();
        var status = new WorkProgress { Running = work.Length > 0, Total = work.Length, New = added, Changed = changed };
        SetStatus(status);
        if (work.Length == 0)
        {
            SaveIfDirty();
            Changed?.Invoke();
            return;
        }
        if (!EnsureBrowser(stop))
        {
            // A run that was replaced leaves the status to the newer one
            if (stop.IsCancellationRequested) return;
            SetStatus(WorkProgress.Idle);
            Changed?.Invoke();
            return;
        }
        status = status with { Threads = workers };
        InternalLogger.Log($"dan engine: {work.Length:N0} maps ({added:N0} new, {changed:N0} changed) in {BrowserName}, {workers} workers, {Estimator}");

        var started = Stopwatch.GetTimestamp();
        var lastChanged = started;
        var lastSave = started;
        var next = 0;
        var done = 0;
        var pending = new List<Task>();
        // Charts lost with the browser go again; one lost twice goes on its own, and if it takes
        // the browser down by itself too it is given up on
        var retry = new ConcurrentQueue<Job>();
        var alone = new ConcurrentQueue<Job>();
        var soloRunning = false;
        void Track(Job j) => pending.Add(j.Waiter!.Task.ContinueWith(_ =>
        {
            if (!j.Dropped)
            {
                Interlocked.Increment(ref done);
                return;
            }
            j.Dropped = false;
            j.Attempts++;
            if (j.Attempts == 1) retry.Enqueue(j);
            else if (j.Attempts == 2) alone.Enqueue(j);
            else
            {
                InternalLogger.Log($"dan engine: {j.Map?.ChartPath} takes the browser down; skipped");
                Keep(j, null);
                Interlocked.Increment(ref done);
            }
        }, TaskScheduler.Default));
        void Resend(Job j)
        {
            j.Waiter = new TaskCompletionSource<DanEstimate?>();
            j.Id = Interlocked.Increment(ref nextId);
            Track(j);
            Send(j);
        }
        while (!stop.IsCancellationRequested && (next < work.Length || pending.Count > 0 || !retry.IsEmpty || !alone.IsEmpty))
        {
            var paused = Busy?.Invoke() == true;
            if (soloRunning && pending.Count == 0) soloRunning = false;
            if (!paused && !soloRunning && pending.Count == 0 && alone.TryDequeue(out var single))
            {
                soloRunning = true;
                Resend(single);
            }
            // Keep every worker busy, with one chart waiting behind each
            while (!paused && !soloRunning && alone.IsEmpty && pending.Count < Math.Max(1, workers) * 2)
            {
                if (retry.TryDequeue(out var again))
                {
                    Resend(again);
                    continue;
                }
                if (next >= work.Length) break;
                var (map, size, time) = work[next++];
                string text;
                try
                {
                    text = File.ReadAllText(map.ChartPath);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref done);
                    continue;
                }
                var j = new Job { Id = Interlocked.Increment(ref nextId), Text = text, Settings = SettingsJson(1.0), Map = map, Size = size, Time = time, Key = key, Waiter = new TaskCompletionSource<DanEstimate?>() };
                Track(j);
                Send(j);
            }
            if (pending.Count > 0) Task.WaitAny(pending.ToArray(), 250);
            else Thread.Sleep(250);
            pending.RemoveAll(t => t.IsCompleted);
            CheckTimeouts();
            if (!EnsureBrowser(stop))
            {
                if (stop.IsCancellationRequested) return;
                SaveIfDirty();
                SetStatus(WorkProgress.Idle);
                Changed?.Invoke();
                InternalLogger.Log($"dan engine: stopped after {Volatile.Read(ref done):N0} of {work.Length:N0} maps ({Unavailable})");
                return;
            }

            var finished = Volatile.Read(ref done);
            var elapsed = Stopwatch.GetElapsedTime(started);
            SetStatus(status with
            {
                Done = Math.Min(finished, work.Length),
                Paused = paused,
                Remaining = finished > 0 ? elapsed * ((double)(work.Length - finished) / finished) : null,
            });
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
        lastUse = Environment.TickCount64;
        if (stop.IsCancellationRequested) return;
        var took = Stopwatch.GetElapsedTime(started);
        SetStatus(status with { Running = false, Done = work.Length, Took = took });
        Changed?.Invoke();
        InternalLogger.Log($"dan engine: {work.Length:N0} maps in {took.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// The estimate at another rate and OD, or with Hold Off / Invert (<paramref name="cvtFlag"/> "HO" / "IN"),
    /// worked out once per map and kept in memory only. Null when it can't be worked out.
    /// </summary>
    public Task<DanEstimate?> AtRateAsync(MapInfo map, double rate, string? odFlag = null, string? cvtFlag = null)
    {
        if (Math.Abs(rate - 1.0) < 1e-9 && odFlag == null && cvtFlag == null && Get(map.Md5) is { } known) return Task.FromResult<DanEstimate?>(known);
        if (disposed) return Task.FromResult<DanEstimate?>(null);
        var key = $"{map.Md5}|{Math.Round(rate, 2).ToString("0.0#", CultureInfo.InvariantCulture)}|{odFlag}|{cvtFlag}|{SettingsKey}";
        return atRate.GetOrAdd(key, _ => Task.Run(async () =>
        {
            if (!EnsureBrowser(CancellationToken.None)) return null;
            string text;
            try
            {
                text = await File.ReadAllTextAsync(map.ChartPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            var j = new Job { Id = Interlocked.Increment(ref nextId), Text = text, Settings = SettingsJson(rate, odFlag, cvtFlag), Waiter = new TaskCompletionSource<DanEstimate?>() };
            Send(j);
            var r = await j.Waiter.Task;
            lastUse = Environment.TickCount64;
            return r;
        }));
    }

    // ------------------------------------------------------------ talking to the page

    private void Send(Job j)
    {
        j.SentAt = Environment.TickCount64;
        inFlight[j.Id] = j;
        var c = client;
        if (c == null)
        {
            Finish(j.Id, null, keep: false);
            return;
        }
        c.Send(new JsonObject { ["type"] = "danJob", ["id"] = j.Id, ["text"] = j.Text, ["settings"] = j.Settings.DeepClone() }.ToJsonString());
    }

    /// <summary>A chart that takes this long has hung its worker (or the browser went away)</summary>
    private void CheckTimeouts()
    {
        var now = Environment.TickCount64;
        foreach (var j in inFlight.Values)
            if (now - j.SentAt > 120_000)
            {
                InternalLogger.Log($"dan engine: no answer for {j.Map?.Md5 ?? "a chart"} in 2 min");
                Finish(j.Id, null);
            }
    }

    /// <summary>A chart is done; <paramref name="keep"/> false when it was only dropped (the browser went away) and should be tried again.</summary>
    private void Finish(int id, DanEstimate? result, bool keep = true)
    {
        if (!inFlight.TryRemove(id, out var j)) return;
        if (keep) Keep(j, result);
        else j.Dropped = true;
        j.Waiter?.TrySetResult(result);
    }

    private void Keep(Job j, DanEstimate? result)
    {
        if (j.Map == null) return;
        entries[j.Map.Md5] = new Entry { Result = result, Size = j.Size, Time = j.Time, Key = j.Key };
        MarkDirty();
    }

    /// <summary>A message from a page on the engine's server: its hello, or an answer to a chart.</summary>
    private void OnServerMessage(DanEngineServer.Connection page, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return;
            var type = t.GetString() ?? "";
            if (type == "hello") OnHello(page, root);
            else if (ReferenceEquals(page, client)) OnMessage(root, type);
        }
    }

    /// <summary>The page said hello: it is the engine, with this many workers.</summary>
    private void OnHello(DanEngineServer.Connection page, JsonElement hello)
    {
        lock (gate)
        {
            // Only the page of the browser that was just started is taken on; any other page that finds
            // the socket (the address opened in a browser by hand) is sent away, so it can't take the charts
            if (browser == null || connected is not { Task.IsCompleted: false } waiting)
            {
                page.Close();
                return;
            }
            workers = hello.TryGetProperty("workers", out var w) && w.TryGetInt32(out var n) ? Math.Max(1, n) : 1;
            client = page;
            waiting.TrySetResult(true);
        }
    }

    private void OnMessage(JsonElement root, string type)
    {
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
        {
            if (type == "danEngineError") InternalLogger.Log($"dan engine: {root}");
            return;
        }
        DanEstimate? result = null;
        if (type == "danResult" && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double? Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            result = Num("star") is not { } star || !double.IsFinite(star) ? null : new DanEstimate
            {
                Sunny = star,
                LnRatio = Num("lnRatio") ?? 0,
                Columns = (int)(Num("columns") ?? 0),
                Rc = Str("rc"),
                Ln = Str("ln"),
                Numeric = Num("numeric"),
                Estimator = Str("algorithm") ?? "",
                Vibro = r.TryGetProperty("vibro", out var v) && v.ValueKind == JsonValueKind.True,
                LnStar = Num("lnStar") is > 0 and var lnStar ? lnStar : null,
            };
        }
        else if (type == "danError" && Volatile.Read(ref errorsLogged) < 20)
        {
            Interlocked.Increment(ref errorsLogged);
            InternalLogger.Log($"dan engine: {(root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "error")}");
        }
        Finish(id, result);
    }

    private int errorsLogged;

    // ------------------------------------------------------------ the browser

    private readonly SemaphoreSlim starting = new(1, 1);
    private long startedAt, lastRestart;
    private int restarts;

    /// <summary>
    /// Starts the headless browser if it isn't running, and waits for its page. One caller starts it
    /// at a time; the others wait for the same browser. Cancelling only stops the waiting.
    /// </summary>
    private bool EnsureBrowser(CancellationToken stop)
    {
        if (disposed) return false;
        try
        {
            starting.Wait(stop);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            TaskCompletionSource<bool>? waitFor = null;
            var delay = TimeSpan.Zero;
            lock (gate)
            {
                if (disposed) return false;
                // The page's connection is what counts: msedge.exe can hand over to another process
                // of its own and exit while the page keeps working
                if (browser != null && client is { IsOpen: true }) return true;
                // Started by a run that was cancelled while waiting: wait for the same page
                if (browser != null && connected is { Task.IsCompleted: false } pending && Environment.TickCount64 - startedAt < 60_000) waitFor = pending;
                else
                {
                    if (browser != null)
                        InternalLogger.Log($"dan engine: {BrowserName} lost ({(client == null ? "its page never connected" : "its page disconnected")}); restarting it");
                    StopBrowser();
                    // Restarting over and over: wait longer each time, so a broken browser doesn't spin
                    var now = Environment.TickCount64;
                    if (now - lastRestart > 120_000) restarts = 0;
                    lastRestart = now;
                    if (++restarts > 3)
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(60, 5 * (restarts - 3)));
                        InternalLogger.Log($"dan engine: restarted {restarts - 1} times in a row; waiting {delay.TotalSeconds:0} s");
                    }
                }
            }
            if (waitFor == null)
            {
                if (delay > TimeSpan.Zero && stop.WaitHandle.WaitOne(delay)) return false;
                lock (gate) waitFor = disposed ? null : StartBrowser();
                if (waitFor == null) return false;
            }
            bool ok;
            try
            {
                // Edge's first start with a new profile can take a while
                ok = waitFor.Task.Wait(TimeSpan.FromSeconds(60), stop) && waitFor.Task.Result;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (!ok)
            {
                if (disposed) return false;
                Unavailable = $"{BrowserName} started but the analyser page didn't connect";
                InternalLogger.Log($"dan engine: {Unavailable}");
                lock (gate) StopBrowser();
                return false;
            }
            Unavailable = null;
            lastUse = Environment.TickCount64;
            InternalLogger.Log($"dan engine: {BrowserName} ready with {workers} workers");
            return true;
        }
        finally
        {
            starting.Release();
        }
    }

    /// <summary>What is missing for the engine to run at all (the analyser's files, a browser), or null.</summary>
    private string? Prerequisites()
    {
        if (!File.Exists(Path.Combine(server.AnalyserDir, "lr2hp", "index.html")))
            return "The analyser folder is missing next to the exe";
        if (FindBrowser() == null)
            return "Microsoft Edge or Google Chrome is needed for the analyser's dans";
        return null;
    }

    /// <summary>Starts the browser (under <see cref="gate"/>); the task completes when its page says hello.</summary>
    private TaskCompletionSource<bool>? StartBrowser()
    {
        StopBrowser();
        if (Prerequisites() is { } missing)
        {
            Unavailable = missing;
            InternalLogger.Log($"dan engine: {missing}");
            return null;
        }
        if (!server.Start(preferredPort))
        {
            Unavailable = $"No free local port for the analyser ({preferredPort} to {preferredPort + DanEngineServer.PortsToTry - 1})";
            InternalLogger.Log($"dan engine: {Unavailable}");
            return null;
        }
        var exe = FindBrowser()!;
        BrowserName = Path.GetFileNameWithoutExtension(exe);
        startedAt = Environment.TickCount64;
        var started = connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = Path.Combine(AppPaths.Cache, "dan-engine");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
            "--disable-sync", "--mute-audio", "--disable-background-timer-throttling", "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows", $"--user-data-dir={profile}",
        }) info.ArgumentList.Add(arg);
        // Linux runs as root in a container
        if (!OperatingSystem.IsWindows()) info.ArgumentList.Add("--no-sandbox");
        info.ArgumentList.Add(server.PageUrl(Math.Clamp(Workers, 1, Environment.ProcessorCount)));
        try
        {
            browser = Process.Start(info);
            if (browser != null && OperatingSystem.IsWindows()) Confine(browser);
        }
        catch (Exception e)
        {
            Unavailable = $"Could not start {BrowserName}: {e.Message}";
            InternalLogger.Log($"dan engine: {Unavailable}");
            return null;
        }
        if (browser == null)
        {
            Unavailable = $"Could not start {BrowserName}";
            return null;
        }
        return started;
    }

    /// <summary>Closes the browser a while after the last chart, unless the library run is still going.</summary>
    private void IdleWatch()
    {
        while (!disposed)
        {
            Thread.Sleep(5000);
            try
            {
                if (disposed) return;
                if (browser == null || Status.Running || !inFlight.IsEmpty) continue;
                // A browser that is still starting (for one chart at another rate) counts as in use
                if (Environment.TickCount64 - Math.Max(lastUse, startedAt) < 90_000) continue;
                lock (gate) StopBrowser();
            }
            catch (Exception e)
            {
                InternalLogger.Log($"dan engine: {e.Message}");
            }
        }
    }

    private void StopBrowser()
    {
        var page = client;
        client = null;
        connected?.TrySetResult(false);
        foreach (var j in inFlight.Keys) Finish(j, null, keep: false);
        page?.Close();
        try
        {
            if (browser is { HasExited: false }) browser.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
        browser?.Dispose();
        browser = null;
        if (job != IntPtr.Zero)
        {
            // Every process of the browser, also ones msedge.exe handed over to; a new browser on the
            // same profile while the old one is still closing would only pass its page to the old one
            TerminateJobObject(job, 0);
            var until = Environment.TickCount64 + 5000;
            while (ActiveProcesses(job) > 0 && Environment.TickCount64 < until) Thread.Sleep(50);
            CloseHandle(job);
            job = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll")]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out Accounting info, int length, IntPtr returnLength);

    private static uint ActiveProcesses(IntPtr job) =>
        QueryInformationJobObject(job, 1, out var info, Marshal.SizeOf<Accounting>(), IntPtr.Zero) ? info.ActiveProcesses : 0;

    /// <summary>Edge, then Chrome (or <see cref="BrowserPath"/>, or the <see cref="BrowserVariable"/> environment variable)</summary>
    private string? FindBrowser()
    {
        var candidates = new List<string?> { BrowserPath, Environment.GetEnvironmentVariable(BrowserVariable) };
        if (OperatingSystem.IsWindows())
        {
            foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
            {
                candidates.Add(AppPath(Microsoft.Win32.Registry.LocalMachine, exe));
                candidates.Add(AppPath(Microsoft.Win32.Registry.CurrentUser, exe));
            }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (root.Length == 0) continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }
        }
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? AppPath(Microsoft.Win32.RegistryKey hive, string exe)
    {
        try
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    // A job object: the browser and every process it starts run below normal priority, and are
    // closed with the app even if it crashes
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private void Confine(Process process)
    {
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;
            const uint killOnClose = 0x2000, priorityClass = 0x20, belowNormal = 0x4000;
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = killOnClose | priorityClass, PriorityClass = belowNormal } };
            SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>());
            AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception e)
        {
            InternalLogger.Log($"dan engine: job object: {e.Message}");
        }
    }

    // ------------------------------------------------------------ the file

    private void Load()
    {
        try
        {
            if (File.Exists(DansFile) && JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(DansFile), file_options) is { } file)
                foreach (var (md5, e) in file.Maps)
                    entries[md5] = e;
        }
        catch (Exception e)
        {
            // An unreadable file only means working them out again
            InternalLogger.Log($"dans: {e.Message}");
        }
    }

    private void MarkDirty()
    {
        lock (entries) dirty = true;
    }

    private void SaveIfDirty()
    {
        lock (entries)
        {
            if (!dirty) return;
            dirty = false;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var tmp = DansFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile { Maps = new Dictionary<string, Entry>(entries) }, file_options));
                File.Move(tmp, DansFile, true);
            }
            catch (Exception e)
            {
                InternalLogger.Log($"dans: {e.Message}");
            }
        }
    }

    private void SetStatus(WorkProgress status)
    {
        Status = status;
        Progress?.Invoke();
    }

    /// <summary>Stops the run and the browser, saves what was worked out, and closes the server.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (gate)
        {
            run?.Cancel();
            StopBrowser();
        }
        SaveIfDirty();
        server.Dispose();
    }
}
