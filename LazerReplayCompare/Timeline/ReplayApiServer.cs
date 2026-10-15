using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LazerReplayCompare;

public sealed class ReplayApiServer : IDisposable
{
    private readonly Func<(string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay)> getReplays;
    private readonly Func<CorrectionMode> getDefaultCorrectionMode;
    private readonly Func<OverlayDisplay> getOverlayDisplay;
    private readonly ReplayTimelineBuilder timelineBuilder;

    // /perfect results by beatmap, client and mods (most recent last)
    private readonly List<(string Key, long[] Scores)> perfectScores = new();
    private const int kept_perfect_scores = 8;
    private readonly CancellationTokenSource cancellation = new();
    private TcpListener? listener;

    public ReplayApiServer(
        Func<(string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay)> getReplays,
        Func<CorrectionMode> getDefaultCorrectionMode,
        Func<OverlayDisplay> getOverlayDisplay,
        ReplayTimelineBuilder timelineBuilder)
    {
        this.timelineBuilder = timelineBuilder;
        this.getReplays = getReplays;
        this.getDefaultCorrectionMode = getDefaultCorrectionMode;
        this.getOverlayDisplay = getOverlayDisplay;
    }

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

        try
        {
            var requestLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            string? line;
            do
            {
                line = reader.ReadLine();
            } while (!string.IsNullOrEmpty(line));

            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                WriteJson(stream, new { error = "Bad request" }, 400);
                return;
            }

            var uri = new Uri("http://127.0.0.1" + parts[1]);
            if (parts[0] == "OPTIONS")
            {
                WriteJson(stream, new { }, 204);
                return;
            }

            if (uri.AbsolutePath == "/health")
            {
                WriteJson(stream, new { name = "LazerReplayCompare", status = "ok" });
                return;
            }

            if (uri.AbsolutePath == "/replays")
            {
                var (md5, replayList, selectedReplay) = getReplays();
                WriteJson(stream, new { beatmapMd5 = md5, selectedReplay, replays = replayList });
                return;
            }

            if (uri.AbsolutePath == "/best-replay")
            {
                var (_, bestReplays, selectedReplay) = getReplays();
                WriteJson(stream, new { replayCount = bestReplays.Count, selectedReplay, replay = selectedReplay ?? bestReplays.FirstOrDefault(replay => replay.HasReplay) });
                return;
            }

            if (uri.AbsolutePath == "/timeline")
            {
                var query = ParseQuery(uri.Query);
                var replayPath = GetQueryValue(query, "osr") ?? GetQueryValue(query, "replay") ?? GetQueryValue(query, "filePath");
                var beatmapPath = GetQueryValue(query, "osu") ?? GetQueryValue(query, "beatmap");
                var rateStr = GetQueryValue(query, "rate");
                var correction = GetQueryValue(query, "correctionMode") ?? GetQueryValue(query, "correction") ?? GetQueryValue(query, "mode");
                var rate = double.TryParse(rateStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0;
                var correctionMode = ParseCorrectionMode(correction, getDefaultCorrectionMode());
                var scaleMetric = GetQueryValue(query, "scaleMetric") ?? GetQueryValue(query, "metric") ?? MetricRegistry.Default.Key;
                // the game the live play runs in: osu!stable replays are scored the osu!stable way against osu!stable
                var liveClient = LiveClients.Parse(GetQueryValue(query, "client"));

                WriteJson(stream, timelineBuilder.GetOrBuild(replayPath ?? string.Empty, beatmapPath, rate, correctionMode, scaleMetric, liveClient));
                return;
            }

            if (uri.AbsolutePath == "/perfect")
            {
                var query = ParseQuery(uri.Query);
                var beatmapPath = GetQueryValue(query, "osu") ?? GetQueryValue(query, "beatmap") ?? string.Empty;
                var liveClient = LiveClients.Parse(GetQueryValue(query, "client"));
                var mods = (GetQueryValue(query, "mods") ?? string.Empty).Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var rate = double.TryParse(GetQueryValue(query, "rate"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0;
                WriteJson(stream, new { client = liveClient.ToString().ToLowerInvariant(), scores = GetPerfectScores(beatmapPath, liveClient, mods, rate) });
                return;
            }

            if (uri.AbsolutePath == "/metrics")
            {
                WriteJson(stream, new { metrics = MetricRegistry.ToJson() });
                return;
            }

            if (uri.AbsolutePath == "/state")
            {
                WriteJson(stream, BuildState());
                return;
            }

            WriteJson(stream, new { error = "Not found" }, 404);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            WriteJson(stream, new { error = ex.Message }, 500);
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

    private static void WriteJson(Stream stream, object value, int statusCode = 200)
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
            404 => "Not Found",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            _ => "OK",
        };

        using var writer = new StreamWriter(stream, leaveOpen: true) { NewLine = "\r\n" };
        writer.WriteLine($"HTTP/1.1 {statusCode} {reason}");
        writer.WriteLine("Content-Type: application/json; charset=utf-8");
        writer.WriteLine("Access-Control-Allow-Origin: *");
        writer.WriteLine("Access-Control-Allow-Methods: GET, OPTIONS");
        writer.WriteLine("Access-Control-Allow-Headers: Content-Type");
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
