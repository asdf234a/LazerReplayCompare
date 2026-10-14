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
                    applyFilters:["client",{"field":"beatmap","keys":["checksum","artist","title","version","difficulty","name"]},{"field":"folders","keys":["songs","beatmap"]},{"field":"files","keys":["beatmap","background"]},{"field":"directPath","keys":["beatmapBackground"]},{"field":"state","keys":["name"]},{"field":"play","keys":["mods"]}]
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
    string BeatmapArtist,
    string BeatmapTitle,
    string BeatmapVersion,
    string SongsFolder,
    string BeatmapFolder,
    string BeatmapFile,
    string BackgroundFile = "",
    string DirectBackgroundPath = "",
    PlaySpeed? Speed = null)
{
    /// <summary>The current play's rate and Classic mod (1×, no Classic when tosu reports no mods).</summary>
    public PlaySpeed PlaySpeed => Speed ?? new PlaySpeed(1, false);

    /// <summary>Absolute path of the beatmap's background image (empty when tosu reports none).</summary>
    public string BackgroundPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DirectBackgroundPath))
                return DirectBackgroundPath;
            if (string.IsNullOrWhiteSpace(SongsFolder) || string.IsNullOrWhiteSpace(BackgroundFile))
                return string.Empty;

            // Same layout as the beatmap file: lazer stores files flat (by hash) under the songs/files folder.
            return Client == "lazer"
                ? System.IO.Path.Combine(SongsFolder, BackgroundFile)
                : System.IO.Path.Combine(SongsFolder, BeatmapFolder, BackgroundFile);
        }
    }

    public static TosuSnapshot? FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new TosuSnapshot(
            Client: GetString(root, "client"),
            State: GetString(root, "state", "name"),
            BeatmapChecksum: GetString(root, "beatmap", "checksum"),
            BeatmapArtist: GetString(root, "beatmap", "artist"),
            BeatmapTitle: GetString(root, "beatmap", "title"),
            BeatmapVersion: GetBeatmapVersion(root),
            SongsFolder: GetString(root, "folders", "songs"),
            BeatmapFolder: GetString(root, "folders", "beatmap"),
            BeatmapFile: GetString(root, "files", "beatmap"),
            BackgroundFile: GetString(root, "files", "background"),
            DirectBackgroundPath: GetString(root, "directPath", "beatmapBackground"),
            Speed: GetPlaySpeed(root));
    }

    /// <summary>play.mods → rate/Classic, the way the overlays read it (getCurrentModsKey), and whether the client is osu!stable.</summary>
    private static PlaySpeed GetPlaySpeed(JsonElement root)
    {
        bool stable = LiveClients.Parse(GetString(root, "client")) == LiveClient.Stable;
        if (!root.TryGetProperty("play", out var play) || play.ValueKind != JsonValueKind.Object ||
            !play.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object)
            return new PlaySpeed(1, false, stable);

        var acronyms = new List<string>();
        double? settingsRate = null;
        if (mods.TryGetProperty("array", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var mod in array.EnumerateArray())
            {
                if (mod.ValueKind != JsonValueKind.Object)
                    continue;

                acronyms.Add(GetString(mod, "acronym"));
                if (settingsRate == null && mod.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                {
                    settingsRate = AutoTargetRule.RateFromSettings(settings.EnumerateObject()
                        .Select(setting => new KeyValuePair<string, string>(setting.Name, settingText(setting.Value))));
                }
            }
        }

        var rate = mods.TryGetProperty("rate", out var rateValue) && rateValue.ValueKind == JsonValueKind.Number ? rateValue.GetDouble() : 1;
        return AutoTargetRule.ForPlay(acronyms, settingsRate, rate, stable);

        static string settingText(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object when value.TryGetProperty("value", out var inner) => settingText(inner),
            JsonValueKind.Object when value.TryGetProperty("Value", out var inner) => settingText(inner),
            _ => string.Empty,
        };
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

    private static string GetBeatmapVersion(JsonElement root)
    {
        var version = GetString(root, "beatmap", "version");
        if (!string.IsNullOrWhiteSpace(version))
            return version;

        version = GetString(root, "beatmap", "difficulty");
        if (!string.IsNullOrWhiteSpace(version))
            return version;

        return GetString(root, "beatmap", "name");
    }
}
