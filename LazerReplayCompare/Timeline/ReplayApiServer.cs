using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LazerReplayCompare;

public sealed class ReplayApiServer : IDisposable
{
    private readonly Func<(string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay)> getReplays;
    private readonly ReplayTimelineBuilder timelineBuilder = new();
    private readonly CancellationTokenSource cancellation = new();
    private TcpListener? listener;

    public ReplayApiServer(Func<(string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay)> getReplays)
    {
        this.getReplays = getReplays;
    }

    public int Port { get; private set; }

    public void Start(int port)
    {
        Port = port;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
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
                WriteJson(stream, new { replayCount = bestReplays.Count, selectedReplay, replay = selectedReplay ?? bestReplays.FirstOrDefault() });
                return;
            }

            if (uri.AbsolutePath == "/timeline")
            {
                var query = ParseQuery(uri.Query);
                var replayPath = GetQueryValue(query, "osr") ?? GetQueryValue(query, "replay") ?? GetQueryValue(query, "filePath");
                var beatmapPath = GetQueryValue(query, "osu") ?? GetQueryValue(query, "beatmap");
                var rateStr = GetQueryValue(query, "rate");
                var correction = GetQueryValue(query, "correction") ?? GetQueryValue(query, "mode");
                var rate = double.TryParse(rateStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0;
                var applyCorrections = !string.Equals(correction, "raw", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(correction, "none", StringComparison.OrdinalIgnoreCase);

                WriteJson(stream, timelineBuilder.Build(replayPath ?? string.Empty, beatmapPath, rate, applyCorrections));
                return;
            }

            WriteJson(stream, new { error = "Not found" }, 404);
        }
        catch (Exception ex)
        {
            WriteJson(stream, new { error = ex.Message }, 500);
        }
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
