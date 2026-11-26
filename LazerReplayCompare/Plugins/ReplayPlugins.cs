using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace LazerReplayCompare;

/// <summary>What a plugin makes of one replay: the number the list sorts by, and how it is written.</summary>
public readonly record struct PluginValue(double Value, string Text);

/// <summary>
/// A replay plugin: one .js file in the data folder's "plugins" folder. It is given every press and release of a
/// replay with its error in milliseconds and returns a number of its own making (another game's accuracy, a score, a
/// count), shown as a column of the replay list. See <see cref="ReplayPlugins.Examples"/> for the files' shape.
/// </summary>
public sealed class ReplayPlugin
{
    private readonly object sync = new();
    private readonly Engine? engine;
    private readonly JsValue? score;
    private readonly JsValue? parse;

    /// <summary>The file's name without ".js", in lower case.</summary>
    public string Key { get; }

    /// <summary>The column's title (the script's <c>name</c>, or the file's name).</summary>
    public string Name { get; }

    /// <summary>"percent", "integer" or "decimal".</summary>
    public string Format { get; }

    public int Decimals { get; }

    /// <summary>What the file holds (a changed file is another version: its values are worked out again).</summary>
    public string Version { get; }

    /// <summary>The script itself (the overlays run it too, on the play going on).</summary>
    public string Source { get; } = string.Empty;

    /// <summary>Why the script can't be used, or why its last call failed; null when all is well.</summary>
    public string? Error { get; private set; }

    public ReplayPlugin(string path)
    {
        Key = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        Name = Path.GetFileNameWithoutExtension(path);
        Format = "decimal";
        Decimals = 2;
        Version = string.Empty;

        try
        {
            var text = File.ReadAllText(path);
            Source = text;
            Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

            // No access to files, the network or .NET: a script can only compute. A script that runs away is stopped.
            engine = new Engine(options => options
                .TimeoutInterval(TimeSpan.FromSeconds(5))
                .LimitRecursion(256)
                .LimitMemory(256_000_000));
            engine.Execute(text);

            // (Evaluate, so that names declared with const / let are found too)
            score = engine.Evaluate("typeof score === 'function' ? score : undefined");
            parse = engine.Evaluate("JSON.parse");
            if (score.IsUndefined())
            {
                Error = "The script has no score(replay) function.";
                return;
            }

            if (engine.Evaluate("typeof name === 'string' ? name : undefined") is { } name && name.IsString() && name.AsString().Trim().Length > 0)
                Name = name.AsString().Trim();
            if (engine.Evaluate("typeof format === 'string' ? format : undefined") is { } format && format.IsString())
                Format = format.AsString().Trim().ToLowerInvariant() is "percent" or "integer" ? format.AsString().Trim().ToLowerInvariant() : "decimal";
            Decimals = Format == "integer" ? 0 : 2;
            if (engine.Evaluate("typeof decimals === 'number' ? decimals : undefined") is { } decimals && decimals.IsNumber())
                Decimals = (int)Math.Clamp(decimals.AsNumber(), 0, 6);
        }
        catch (Exception ex)
        {
            Error = describe(ex);
            score = null;
        }
    }

    public bool Usable => engine != null && score is { } function && !function.IsUndefined();

    /// <summary>The script's value for a replay (<paramref name="replayJson"/>: see <see cref="PluginReplayData"/>), or null.</summary>
    public PluginValue? Score(string replayJson)
    {
        if (!Usable)
            return null;

        lock (sync)
        {
            try
            {
                var replay = engine!.Invoke(parse!, replayJson);
                var result = engine.Invoke(score!, replay);
                Error = null;

                if (result.IsNumber())
                    return value(result.AsNumber(), null);

                // { value, text }: a number to sort by and its own wording
                if (result.IsObject())
                {
                    var made = result.AsObject();
                    var number = made.Get("value");
                    var text = made.Get("text");
                    if (number.IsNumber())
                        return value(number.AsNumber(), text.IsString() ? text.AsString() : null);
                }

                return null;
            }
            catch (Exception ex)
            {
                Error = describe(ex);
                InternalLogger.Log($"plugin {Key}: {Error}");
                return null;
            }
        }
    }

    private PluginValue? value(double number, string? text)
    {
        if (!double.IsFinite(number))
            return null;

        return new PluginValue(number, text ?? Format switch
        {
            "percent" => number.ToString("F" + Decimals, CultureInfo.InvariantCulture) + "%",
            "integer" => Math.Round(number).ToString("N0", CultureInfo.InvariantCulture),
            _ => number.ToString("F" + Decimals, CultureInfo.InvariantCulture),
        });
    }

    private static string describe(Exception ex) => ex switch
    {
        JavaScriptException js => $"{js.Message} (line {js.Location.Start.Line})",
        TimeoutException => "The script took too long.",
        _ => ex.Message,
    };
}

/// <summary>The plugins in the data folder's "plugins" folder, read again whenever a file there changes.</summary>
public sealed class ReplayPlugins : IDisposable
{
    public static string Folder => Path.Combine(AppPaths.Root, "plugins");

    /// <summary>The plugins were read again (a file was saved, added or removed). Raised off the UI thread.</summary>
    public event Action? Changed;

    private readonly object sync = new();
    private List<ReplayPlugin> plugins = new();
    private FileSystemWatcher? watcher;
    private readonly Timer reload;

    public ReplayPlugins()
    {
        reload = new Timer(_ =>
        {
            Load();
            Changed?.Invoke();
        });
    }

    public IReadOnlyList<ReplayPlugin> All
    {
        get
        {
            lock (sync)
                return plugins;
        }
    }

    public ReplayPlugin? Find(string? key) => string.IsNullOrEmpty(key) ? null : All.FirstOrDefault(plugin => plugin.Key == key);

    /// <summary>Reads the folder (made with the examples when there is none) and starts watching it.</summary>
    public void Start()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                Directory.CreateDirectory(Folder);
                foreach (var (name, text) in Examples)
                    File.WriteAllText(Path.Combine(Folder, name), text);
            }

            Load();
            watcher = new FileSystemWatcher(Folder, "*.js")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            // (editors save in several steps: read once the folder has been quiet for a moment)
            void changed(object? sender, FileSystemEventArgs e) => reload.Change(400, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (sender, e) => changed(sender, e);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    public void Load()
    {
        var read = new List<ReplayPlugin>();
        try
        {
            var known = All.ToDictionary(plugin => plugin.Key);
            foreach (var path in Directory.EnumerateFiles(Folder, "*.js").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var plugin = new ReplayPlugin(path);
                // an unchanged file keeps its plugin (and its script's state)
                read.Add(known.TryGetValue(plugin.Key, out var old) && old.Version == plugin.Version && old.Usable ? old : plugin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }

        lock (sync)
            plugins = read;
    }

    public void Dispose()
    {
        watcher?.Dispose();
        reload.Dispose();
    }

    /// <summary>The files a new plugins folder starts with: each explains the shape of a plugin and of a replay.</summary>
    public static readonly (string Name, string Text)[] Examples =
    [
        ("wife3.js", """
            // A replay plugin: this file is one column of the replay list (pick it with "Column" above the list),
            // and an item the Main and Sub overlays can show during a play (under Plugins in their pickers).
            // Saved changes apply right away. Copy the file under another name to make another one.
            //
            // During a play the game only tells how far off each judged press and release was, not which note it
            // belongs to. There the plugin gets every one of them as a "note" (column -1, no "head" or "release"), and
            // each miss in unpressed; the compared replay is told the same way, so the two can be compared. The
            // numbers then match this column's only on maps without hold notes.
            //
            //   name      the column's title
            //   format    "percent" (97.53%), "decimal" (97.53) or "integer" (9,753)
            //   decimals  how many decimals (optional)
            //   score(replay)  returns the number shown and sorted by (or { value, text } to word it yourself;
            //                  null shows a dash)
            //
            // replay:
            //   hits         every press and release that was judged, in time order:
            //                  ms      how far off it was, in real milliseconds (late is positive)
            //                  kind    "note", "head" (a hold note's press) or "release"
            //                  result  the judgement it got: "Perfect" (MAX), "Great" (300), "Good" (200), "Ok" (100),
            //                          "Meh" (50) or "Miss"
            //                  time    when the note is, in real milliseconds
            //                  column  0 is the leftmost lane
            //   unpressed    notes and hold note heads that were never pressed: { time, column, result }
            //   heldPastEnd  hold notes held past their end, so with no release to time: { time, column, result }
            //   misses       how many judgements were a miss; missTimes: when each was counted (real ms)
            //   notes, holds how many notes and hold notes the map has
            //   rate         1.5 with Double Time (the times above are real time already)
            //   od, columns, length (ms), stable (true: an osu!stable replay, one judgement per hold note)
            //   player, score, accuracy (0-1), maxCombo, rank, mods (["DT", "MR"])
            //   counts       the judgement counts the replay recorded: { Perfect, Great, Good, Ok, Meh, Miss }
            //
            // This one: Etterna's Wife3 at judge 4. Every note and hold note head scores by how far off it was, a
            // note never pressed is a miss, and releases are not timed (Etterna doesn't time them).

            const name = "Wife3";
            const format = "percent";

            const timingScale = 1;        // judge 4; judge 5 is 0.84, judge 6 is 0.66, judge 7 is 0.5
            const maxPoints = 2;
            const missWeight = -5.5;

            function erf(x) {
                // Abramowitz and Stegun 7.1.26
                const sign = x < 0 ? -1 : 1;
                x = Math.abs(x);
                const t = 1 / (1 + 0.3275911 * x);
                const y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.exp(-x * x);
                return sign * y;
            }

            function wife3(ms) {
                const ridic = 5 * timingScale;
                const zero = 65 * Math.pow(timingScale, 0.75);
                const dev = 22.7 * Math.pow(timingScale, 0.75);
                const maxBoo = 180 * timingScale;
                const off = Math.abs(ms);
                if (off <= ridic) return maxPoints;
                if (off <= zero) return maxPoints * erf((zero - off) / dev);
                if (off <= maxBoo) return (off - zero) * missWeight / (maxBoo - zero);
                return missWeight;
            }

            function score(replay) {
                let points = 0, judged = 0;
                for (const hit of replay.hits) {
                    if (hit.kind === "release") continue;
                    points += wife3(hit.ms);
                    judged++;
                }
                points += replay.unpressed.length * missWeight;
                judged += replay.unpressed.length;
                return judged === 0 ? null : points / (judged * maxPoints) * 100;
            }
            """),
        ("windows.js", """
            // A replay plugin that judges every press again with timing windows of its own (see wife3.js for what a
            // plugin is given). The numbers below are close to Quaver's standard windows: change them to the game you
            // want, or copy the file under another name for a second column.

            const name = "Windows";
            const format = "percent";

            // up to this many ms off -> worth this much
            const windows = [
                [18, 100],      // Marvelous
                [43, 98.25],    // Perfect
                [76, 65],       // Great
                [106, 25],      // Good
                [127, -100],    // Okay
            ];
            const missValue = -50;
            const maxValue = 100;
            const releaseScale = 1.5;   // releases get windows this much wider

            function worth(ms, scale) {
                const off = Math.abs(ms);
                for (const [limit, value] of windows)
                    if (off <= limit * scale) return value;
                return missValue;
            }

            function score(replay) {
                let total = 0, judged = 0;
                for (const hit of replay.hits) {
                    total += worth(hit.ms, hit.kind === "release" ? releaseScale : 1);
                    judged++;
                }
                // never pressed: a miss. Held past the end: counted as the last window.
                total += replay.unpressed.length * missValue;
                total += replay.heldPastEnd.length * windows[windows.length - 1][1];
                judged += replay.unpressed.length + replay.heldPastEnd.length;
                return judged === 0 ? null : Math.max(0, total / (judged * maxValue)) * 100;
            }
            """),
    ];
}

/// <summary>A replay as plugins see it: the JSON described at the top of the example plugin.</summary>
public static class PluginReplayData
{
    private static readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Build(ReplayEntry entry, ReplayAnalysis analysis)
    {
        static double round(double value, int digits) => Math.Round(value, digits);

        return JsonSerializer.Serialize(new
        {
            player = entry.Player,
            score = entry.Score,
            accuracy = entry.Accuracy,
            maxCombo = entry.MaxCombo,
            rank = entry.Rank,
            mods = entry.Mods.Select(mod => mod.Acronym).ToArray(),
            counts = entry.Statistics,
            rate = analysis.Rate,
            od = analysis.OverallDifficulty,
            columns = analysis.Columns,
            length = round(analysis.Length, 1),
            stable = analysis.StableJudging,
            notes = analysis.Notes,
            holds = analysis.Holds,
            misses = analysis.MissTimes.Count,
            missTimes = analysis.MissTimes.Select(time => round(time, 1)),
            hits = analysis.Samples.Select(sample => new
            {
                ms = round(sample.Error, 3),
                kind = sample.Kind switch { HitKind.Head => "head", HitKind.Release => "release", _ => "note" },
                result = sample.Result.ToString(),
                time = round(sample.Time, 1),
                column = sample.Column,
            }),
            unpressed = analysis.Unpressed.Select(item => new { time = round(item.Time, 1), column = item.Column, result = item.Result.ToString() }),
            heldPastEnd = analysis.HeldPastEnd.Select(item => new { time = round(item.Time, 1), column = item.Column, result = item.Result.ToString() }),
        }, options);
    }
}

/// <summary>
/// Plugin values by replay: kept in memory, and between runs in the cache folder (working one out means analysing the
/// whole replay). The replays' data of the maps looked at lately is kept too, so a plugin that is being edited shows
/// its new values at once.
/// </summary>
public sealed class PluginScores : IDisposable
{
    private sealed record Stored(double? V, string? T);

    private const int kept_replays = 48;

    private readonly string file = Path.Combine(AppPaths.Cache, "plugin-values.json");
    private readonly object sync = new();
    // "{plugin}|{version}" -> replay key -> value (null: worked out, and there is none)
    private Dictionary<string, Dictionary<string, Stored>> values = new();
    private readonly List<(string Key, string Json)> replays = new();
    private readonly Timer save;
    private bool loaded;

    public PluginScores()
    {
        save = new Timer(_ => write());
    }

    /// <summary>What identifies a replay's data: the replay file as it is now, and the beatmap it is judged on.</summary>
    public static string ReplayKey(string replayPath, string beatmapPath)
    {
        var info = new FileInfo(replayPath);
        return $"{replayPath}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{beatmapPath}";
    }

    /// <summary>The kept value; <paramref name="known"/> false when it has yet to be worked out.</summary>
    public PluginValue? Get(ReplayPlugin plugin, string replayKey, out bool known)
    {
        lock (sync)
        {
            load();
            known = values.TryGetValue($"{plugin.Key}|{plugin.Version}", out var of) && of.TryGetValue(replayKey, out var stored);
            return known && values[$"{plugin.Key}|{plugin.Version}"][replayKey] is { V: { } number, T: { } text } ? new PluginValue(number, text) : null;
        }
    }

    /// <summary>
    /// Works the value out (analysing the replay unless its data is kept) and keeps it. Call off the UI thread.
    /// </summary>
    public PluginValue? Compute(ReplayPlugin plugin, ReplayEntry entry, string beatmapPath)
    {
        var key = ReplayKey(entry.FilePath, beatmapPath);
        var value = plugin.Score(ReplayData(entry, beatmapPath));
        // (a script that failed is asked again once it is fixed: its version changes with the file)
        lock (sync)
        {
            load();
            var name = $"{plugin.Key}|{plugin.Version}";
            if (!values.TryGetValue(name, out var of))
            {
                // other versions of the same plugin are of no use any more
                foreach (var old in values.Keys.Where(k => k.StartsWith(plugin.Key + "|", StringComparison.Ordinal)).ToList())
                    values.Remove(old);
                values[name] = of = new Dictionary<string, Stored>();
            }

            of[key] = value is { } made ? new Stored(made.Value, made.Text) : new Stored(null, null);
        }

        try
        {
            save.Change(3000, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // closing
        }

        return value;
    }

    /// <summary>
    /// A replay as plugins see it (<see cref="PluginReplayData"/>): analysed once, then kept for the maps looked at
    /// lately. Call off the UI thread.
    /// </summary>
    public string ReplayData(ReplayEntry entry, string beatmapPath)
    {
        var key = ReplayKey(entry.FilePath, beatmapPath);
        string? json;
        lock (sync)
            json = replays.FirstOrDefault(item => item.Key == key).Json;

        if (json != null)
            return json;

        json = PluginReplayData.Build(entry, ReplayAnalyzer.Analyze(entry.FilePath, beatmapPath));
        IdleCollector.AfterHeavyWork();
        lock (sync)
        {
            replays.RemoveAll(item => item.Key == key);
            replays.Add((key, json));
            if (replays.Count > kept_replays)
                replays.RemoveAt(0);
        }

        return json;
    }

    private void load()
    {
        if (loaded)
            return;

        loaded = true;
        try
        {
            if (File.Exists(file))
                values = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Stored>>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }

    private void write()
    {
        try
        {
            string text;
            lock (sync)
                text = JsonSerializer.Serialize(values);

            File.WriteAllText(file + ".tmp", text);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }

    public void Dispose()
    {
        save.Dispose();
        write();
    }
}
