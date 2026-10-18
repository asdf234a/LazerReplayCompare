using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LazerReplayCompare;

public sealed class ReplayApiServer : IDisposable
{
    private readonly Func<(string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay)> getReplays;
    private readonly Func<CorrectionMode> getDefaultCorrectionMode;
    private readonly Func<OverlayDisplay> getOverlayDisplay;
    private readonly Func<IEnumerable<string?>> getAllowedRoots;
    private readonly ReplayTimelineBuilder timelineBuilder;

    // /perfect results by beatmap, client and mods (most recent last)
    private readonly List<(string Key, long[] Scores)> perfectScores = new();
    private const int kept_perfect_scores = 8;
    private const int max_header_line = 8192;
    private const int max_headers = 100;
    private readonly CancellationTokenSource cancellation = new();
    private TcpListener? listener;

    public ReplayApiServer(
        Func<(string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay)> getReplays,
        Func<CorrectionMode> getDefaultCorrectionMode,
        Func<OverlayDisplay> getOverlayDisplay,
        Func<IEnumerable<string?>> getAllowedRoots,
        ReplayTimelineBuilder timelineBuilder)
    {
        this.timelineBuilder = timelineBuilder;
        this.getReplays = getReplays;
        this.getDefaultCorrectionMode = getDefaultCorrectionMode;
        this.getOverlayDisplay = getOverlayDisplay;
        this.getAllowedRoots = getAllowedRoots;
    }

    private static readonly TimeSpan request_timeout = TimeSpan.FromSeconds(10);

    public int Port { get; private set; }

    public void Start(int port)
    {
        if (listener != null)
            return;

        var started = new TcpListener(IPAddress.Loopback, port);
        started.Start();
        Port = port;
        listener = started;
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        if (listener == null)
            return;

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                // a connection that stops sending its request (or reading the answer) gives its thread back
                client.ReceiveTimeout = (int)request_timeout.TotalMilliseconds;
                client.SendTimeout = (int)request_timeout.TotalMilliseconds;
                _ = Task.Run(() => HandleClient(client));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        // The Origin to name in Access-Control-Allow-Origin once the request has passed LocalRequestGuard
        string? origin = null;

        void Respond(object value, int statusCode = 200) => WriteJson(stream, value, statusCode, origin);

        try
        {
            var requestLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                if (line.Length > max_header_line || headers.Count >= max_headers)
                {
                    Respond(new { error = "Bad request" }, 400);
                    return;
                }

                var colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            if (LocalRequestGuard.Refusal(headers, Port) is { } refusal)
            {
                InternalLogger.Log($"API: refused a request ({refusal}) from Origin {headers.GetValueOrDefault("Origin") ?? "-"}, Host {headers.GetValueOrDefault("Host") ?? "-"}");
                Respond(new { error = "Forbidden" }, 403);
                return;
            }

            origin = headers.GetValueOrDefault("Origin");

            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || !parts[1].StartsWith('/'))
            {
                Respond(new { error = "Bad request" }, 400);
                return;
            }

            var uri = new Uri("http://127.0.0.1" + parts[1]);
            if (parts[0] == "OPTIONS")
            {
                Respond(new { }, 204);
                return;
            }

            if (parts[0] != "GET")
            {
                Respond(new { error = "Method not allowed" }, 405);
                return;
            }

            if (uri.AbsolutePath == "/health")
            {
                Respond(new { name = "LazerReplayCompare", status = "ok" });
                return;
            }

            if (uri.AbsolutePath == "/replays")
            {
                var (md5, replayList, selectedReplay) = getReplays();
                Respond(new { beatmapMd5 = md5, selectedReplay, replays = replayList });
                return;
            }

            if (uri.AbsolutePath == "/best-replay")
            {
                var (_, bestReplays, selectedReplay) = getReplays();
                Respond(new { replayCount = bestReplays.Count, selectedReplay, replay = selectedReplay ?? bestReplays.FirstOrDefault(replay => replay.HasReplay) });
                return;
            }

            if (uri.AbsolutePath == "/timeline")
            {
                var query = ParseQuery(uri.Query);
                var replayPath = GetQueryValue(query, "osr") ?? GetQueryValue(query, "replay") ?? GetQueryValue(query, "filePath");
                var beatmapPath = GetQueryValue(query, "osu") ?? GetQueryValue(query, "beatmap");
                var roots = getAllowedRoots().ToList();
                if (!LocalRequestGuard.IsInside(replayPath, roots) || (beatmapPath != null && !LocalRequestGuard.IsInside(beatmapPath, roots)))
                {
                    Respond(new { error = "Only files inside the osu! folders can be read" }, 403);
                    return;
                }

                var rateStr = GetQueryValue(query, "rate");
                var correction = GetQueryValue(query, "correctionMode") ?? GetQueryValue(query, "correction") ?? GetQueryValue(query, "mode");
                var rate = double.TryParse(rateStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0;
                var correctionMode = ParseCorrectionMode(correction, getDefaultCorrectionMode());
                var scaleMetric = GetQueryValue(query, "scaleMetric") ?? GetQueryValue(query, "metric") ?? MetricRegistry.Default.Key;
                // the game the live play runs in: osu!stable replays are scored the osu!stable way against osu!stable
                var liveClient = LiveClients.Parse(GetQueryValue(query, "client"));

                Respond(timelineBuilder.GetOrBuild(replayPath!, beatmapPath, rate, correctionMode, scaleMetric, liveClient));
                return;
            }

            if (uri.AbsolutePath == "/perfect")
            {
                var query = ParseQuery(uri.Query);
                var beatmapPath = GetQueryValue(query, "osu") ?? GetQueryValue(query, "beatmap") ?? string.Empty;
                if (!LocalRequestGuard.IsInside(beatmapPath, getAllowedRoots()))
                {
                    Respond(new { error = "Only files inside the osu! folders can be read" }, 403);
                    return;
                }

                var liveClient = LiveClients.Parse(GetQueryValue(query, "client"));
                var mods = (GetQueryValue(query, "mods") ?? string.Empty).Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var rate = double.TryParse(GetQueryValue(query, "rate"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0;
                Respond(new { client = liveClient.ToString().ToLowerInvariant(), scores = GetPerfectScores(beatmapPath, liveClient, mods, rate) });
                return;
            }

            if (uri.AbsolutePath == "/metrics")
            {
                Respond(new { metrics = MetricRegistry.ToJson() });
                return;
            }

            if (uri.AbsolutePath == "/state")
            {
                Respond(BuildState());
                return;
            }

            Respond(new { error = "Not found" }, 404);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            // The exception's message can name files and folders; it stays in the log
            try
            {
                Respond(new { error = "Internal error" }, 500);
            }
            catch (Exception)
            {
                // the connection is gone
            }
        }
    }

    private static CorrectionMode ParseCorrectionMode(string? value, CorrectionMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (string.Equals(value, "raw", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            return CorrectionMode.Raw;

        if (string.Equals(value, "scaled", StringComparison.OrdinalIgnoreCase))
            return CorrectionMode.Scaled;

        return CorrectionMode.Corrected;
    }

    /// <summary>
    /// Score after each judgement if all were MAX (<see cref="PerfectScores"/>), for the overlays' score_acc. Kept for the
    /// few beatmap / mod combinations asked for recently.
    /// </summary>
    private long[] GetPerfectScores(string beatmapPath, LiveClient client, IReadOnlyList<string> mods, double rate)
    {
        var key = $"{beatmapPath}|{client}|{string.Join("+", mods.Select(m => m.ToUpperInvariant()).Order())}|{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        lock (perfectScores)
        {
            var index = perfectScores.FindIndex(entry => entry.Key == key);
            if (index >= 0)
                return perfectScores[index].Scores;
        }

        var scores = PerfectScores.Build(beatmapPath, client, mods, rate);
        lock (perfectScores)
        {
            perfectScores.RemoveAll(entry => entry.Key == key);
            perfectScores.Add((key, scores));
            if (perfectScores.Count > kept_perfect_scores)
                perfectScores.RemoveAt(0);
        }

        return scores;
    }

    private object BuildState()
    {
        var (md5, replayList, selectedReplay) = getReplays();
        var replay = selectedReplay ?? replayList.FirstOrDefault(entry => entry.HasReplay);
        var correctionMode = getDefaultCorrectionMode().ToString().ToLowerInvariant();
        var replayListVersion = replayList.Count == 0
            ? "0"
            : $"{replayList.Count}:{replayList.Max(replay => replay.Timestamp)}";
        var selectedReplayKey = replay == null
            ? string.Empty
            : $"{replay.FilePath}|{replay.Score}|{replay.Timestamp}|{replay.ModsKey}";
        // A scaled timeline is asked for with the overlay's own item, so the item is part of the overlays' cache keys.
        var timelineKey = $"{md5}|{selectedReplayKey}|{correctionMode}|{replayListVersion}";
        var display = getOverlayDisplay();

        return new
        {
            correctionMode,
            // what the overlays show, chosen in the app
            display = new
            {
                main = new { item = display.MainItem, show = display.MainShow },
                sub = new { item = display.SubItem, show = display.SubShow },
            },
            // built-in items, and custom ones (judgement-count formulas) from metrics.json
            items = OverlayItems.ToJson(),
            beatmapMd5 = md5,
            selectedReplay = replay,
            replayCount = replayList.Count,
            replayListVersion,
            selectedReplayKey,
            timelineKey,
            isReady = !string.IsNullOrWhiteSpace(md5) && replay != null,
        };
    }

    private static void WriteJson(Stream stream, object value, int statusCode, string? origin)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var reason = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            _ => "OK",
        };

        using var writer = new StreamWriter(stream, leaveOpen: true) { NewLine = "\r\n" };
        writer.WriteLine($"HTTP/1.1 {statusCode} {reason}");
        writer.WriteLine("Content-Type: application/json; charset=utf-8");
        // Only pages served from this PC (checked by LocalRequestGuard) may read the answers
        if (origin != null)
        {
            writer.WriteLine($"Access-Control-Allow-Origin: {origin}");
            writer.WriteLine("Vary: Origin");
            writer.WriteLine("Access-Control-Allow-Methods: GET, OPTIONS");
            writer.WriteLine("Access-Control-Allow-Headers: Content-Type");
        }
        writer.WriteLine($"Content-Length: {body.Length}");
        writer.WriteLine("Connection: close");
        writer.WriteLine();
        writer.Flush();
        stream.Write(body);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ")) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static string? GetQueryValue(Dictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener?.Stop();
        cancellation.Dispose();
    }
}
