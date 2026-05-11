using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LazerReplayCompare;

public sealed class TosuClient : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();

    public event Action<TosuSnapshot>? SnapshotReceived;
    public event Action<string>? StatusChanged;

    public void Start(string host)
    {
        _ = Task.Run(() => RunAsync(host, cancellation.Token));
    }

    private async Task RunAsync(string host, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();

            try
            {
                StatusChanged?.Invoke("Connecting to tosu...");
                await socket.ConnectAsync(new Uri($"ws://{host}/websocket/v2"), token);
                StatusChanged?.Invoke("Connected to tosu");

                var filter = """
                    applyFilters:["client",{"field":"beatmap","keys":["checksum"]},{"field":"folders","keys":["songs","beatmap"]},{"field":"files","keys":["beatmap"]},{"field":"state","keys":["name"]}]
                    """;
                var filterBytes = Encoding.UTF8.GetBytes(filter);
                await socket.SendAsync(filterBytes, WebSocketMessageType.Text, true, token);

                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var memory = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        memory.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var json = Encoding.UTF8.GetString(memory.ToArray());
                    var snapshot = TosuSnapshot.FromJson(json);
                    if (snapshot != null)
                        SnapshotReceived?.Invoke(snapshot);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"tosu disconnected: {ex.Message}");
                await Task.Delay(1500, token).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}

public sealed record TosuSnapshot(
    string Client,
    string State,
    string BeatmapChecksum,
    string SongsFolder,
    string BeatmapFolder,
    string BeatmapFile)
{
    public static TosuSnapshot? FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new TosuSnapshot(
            Client: GetString(root, "client"),
            State: GetString(root, "state", "name"),
            BeatmapChecksum: GetString(root, "beatmap", "checksum"),
            SongsFolder: GetString(root, "folders", "songs"),
            BeatmapFolder: GetString(root, "folders", "beatmap"),
            BeatmapFile: GetString(root, "files", "beatmap"));
    }

    private static string GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetString(JsonElement root, string parent, string name)
    {
        return root.TryGetProperty(parent, out var parentValue) &&
               parentValue.ValueKind == JsonValueKind.Object &&
               parentValue.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
