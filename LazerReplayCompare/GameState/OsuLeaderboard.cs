using System.Net.Http.Headers;
using System.Text.Json;

namespace LazerReplayCompare;

public sealed record LeaderboardScore(int Position, long UserId, string Name, long Score);

/// <summary>A leaderboard score with what the app's Online ranking lists (and its id, to ask for its replay).</summary>
public sealed record OnlineScore(int Position, long ScoreId, long UserId, string Player, long TotalScore, double Accuracy, int MaxCombo, string Rank,
    IReadOnlyList<string> Mods, int Miss, DateTimeOffset? Date, bool HasReplay);

/// <summary>
/// A beatmap's top scores from the osu! API (tosu can't read osu!lazer's leaderboard), with the user's own OAuth client.
/// Each beatmap is asked for once: results are kept, and a failed one waits a minute before trying again, a few times at
/// most. The API asks for no more than 60 requests a minute and no polling of a beatmap, so this stays well below that.
/// </summary>
public sealed class OsuLeaderboard : IDisposable
{
    private const string default_site = "https://osu.ppy.sh";
    private const int kept_beatmaps = 200;
    private const int max_attempts = 3;
    private const int max_requests_per_minute = 20;
    private static readonly TimeSpan retry_delay = TimeSpan.FromMinutes(1);

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Func<(string ClientId, string Secret)> getCredentials;
    private readonly Func<DateTime> utcNow;
    private readonly string site;
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly Dictionary<int, IReadOnlyList<OnlineScore>> scores = new();
    private readonly Queue<int> keptOrder = new();
    private readonly Dictionary<int, (int Attempts, DateTime RetryAfter)> failures = new();
    private readonly Queue<DateTime> requests = new();

    private (string ClientId, string Secret) credentials;
    private string? token;
    private DateTime tokenExpires;

    public OsuLeaderboard(Func<(string ClientId, string Secret)> getCredentials, string site = default_site, Func<DateTime>? utcNow = null)
    {
        this.getCredentials = getCredentials;
        this.site = site;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LazerReplayCompare");
    }

    /// <summary>The beatmap's top scores, best first; nothing without a client, or while they can't be had.</summary>
    public async Task<IReadOnlyList<LeaderboardScore>> GetAsync(int beatmapId)
        => (await GetDetailedAsync(beatmapId)).Select(score => new LeaderboardScore(score.Position, score.UserId, score.Player, score.TotalScore)).ToList();

    /// <summary>Whether an OAuth client is entered (without one nothing is asked for).</summary>
    public bool HasClient
    {
        get
        {
            var current = getCredentials();
            return !string.IsNullOrWhiteSpace(current.ClientId) && !string.IsNullOrWhiteSpace(current.Secret);
        }
    }

    /// <summary>Why the last request failed ("" when it didn't).</summary>
    public string LastError { get; private set; } = string.Empty;

    // replays are downloaded one at a time, on the user's click, and no more than a few a minute
    private const int max_downloads_per_minute = 6;
    private readonly Queue<DateTime> downloads = new();

    /// <summary>
    /// Downloads one score's replay to <paramref name="targetPath"/> (a replay already there is not asked for again).
    /// False when it couldn't be had; <see cref="LastError"/> says why.
    /// </summary>
    public async Task<bool> DownloadReplayAsync(long scoreId, string targetPath)
    {
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            return true;

        var current = getCredentials();
        if (scoreId <= 0 || string.IsNullOrWhiteSpace(current.ClientId) || string.IsNullOrWhiteSpace(current.Secret))
        {
            LastError = "No osu! API client in Settings";
            return false;
        }

        await gate.WaitAsync();
        try
        {
            var now = utcNow();
            while (downloads.Count > 0 && now - downloads.Peek() >= TimeSpan.FromMinutes(1))
                downloads.Dequeue();
            if (downloads.Count >= max_downloads_per_minute)
            {
                LastError = "Too many downloads in a minute: try again shortly";
                return false;
            }

            if (current != credentials)
            {
                credentials = current;
                token = null;
            }

            if (token == null || now >= tokenExpires)
            {
                if (!takeRequest(now))
                {
                    LastError = "Too many requests in a minute: try again shortly";
                    return false;
                }

                await requestTokenAsync(now);
            }

            if (!takeRequest(now))
            {
                LastError = "Too many requests in a minute: try again shortly";
                return false;
            }

            downloads.Enqueue(now);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{site}/api/v2/scores/{scoreId}/download");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("x-api-version", "20220705");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LastError = (int)response.StatusCode == 404 ? "osu! has no replay of this score" : $"osu! answered {(int)response.StatusCode}";
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                LastError = "osu! sent an empty replay";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporary = targetPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, targetPath, overwrite: true);
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
            token = null;
            LastError = ex.Message;
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The beatmap's top scores with what the Online ranking lists; nothing without a client, or while they can't be had.</summary>
    public async Task<IReadOnlyList<OnlineScore>> GetDetailedAsync(int beatmapId)
    {
        var current = getCredentials();
        if (beatmapId <= 0 || string.IsNullOrWhiteSpace(current.ClientId) || string.IsNullOrWhiteSpace(current.Secret))
            return [];

        await gate.WaitAsync();
        try
        {
            if (current != credentials)
            {
                // a new client: what failed may work now
                credentials = current;
                token = null;
                failures.Clear();
            }

            if (scores.TryGetValue(beatmapId, out var kept))
                return kept;

            var now = utcNow();
            if (failures.TryGetValue(beatmapId, out var failure) && (failure.Attempts >= max_attempts || now < failure.RetryAfter))
                return [];

            try
            {
                var loaded = await fetchAsync(beatmapId, now);
                if (loaded == null)
                    return [];  // over the app's own limit: not the beatmap's failure

                keep(beatmapId, loaded);
                failures.Remove(beatmapId);
                return loaded;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                InternalLogger.Log(ex);
                token = null;
                failures[beatmapId] = (failures.GetValueOrDefault(beatmapId).Attempts + 1, now + retry_delay);
                return [];
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<OnlineScore>?> fetchAsync(int beatmapId, DateTime now)
    {
        if (token == null || now >= tokenExpires)
        {
            if (!takeRequest(now))
                return null;

            await requestTokenAsync(now);
        }

        if (!takeRequest(now))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{site}/api/v2/beatmaps/{beatmapId}/scores?mode=mania&legacy_only=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("x-api-version", "20220705");

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var list = new List<OnlineScore>();
        foreach (var score in document.RootElement.GetProperty("scores").EnumerateArray())
        {
            var name = score.TryGetProperty("user", out var user) && user.TryGetProperty("username", out var username) ? username.GetString() : null;
            var mods = new List<string>();
            if (score.TryGetProperty("mods", out var modList) && modList.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in modList.EnumerateArray())
                {
                    if (mod.ValueKind == JsonValueKind.Object && mod.TryGetProperty("acronym", out var acronym) && acronym.GetString() is { Length: > 0 } text)
                        mods.Add(text);
                }
            }

            int miss = score.TryGetProperty("statistics", out var statistics) && statistics.ValueKind == JsonValueKind.Object
                       && statistics.TryGetProperty("miss", out var missValue) && missValue.ValueKind == JsonValueKind.Number ? missValue.GetInt32() : 0;
            DateTimeOffset? date = score.TryGetProperty("ended_at", out var ended) && ended.ValueKind == JsonValueKind.String
                                   && DateTimeOffset.TryParse(ended.GetString(), out var parsed) ? parsed : null;

            list.Add(new OnlineScore(
                list.Count + 1,
                score.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                score.GetProperty("user_id").GetInt64(),
                name ?? string.Empty,
                score.GetProperty("total_score").GetInt64(),
                score.TryGetProperty("accuracy", out var accuracy) && accuracy.ValueKind == JsonValueKind.Number ? accuracy.GetDouble() : 0,
                score.TryGetProperty("max_combo", out var combo) && combo.ValueKind == JsonValueKind.Number ? combo.GetInt32() : 0,
                score.TryGetProperty("rank", out var rank) ? rank.GetString() ?? string.Empty : string.Empty,
                mods,
                miss,
                date,
                score.TryGetProperty("has_replay", out var hasReplay) && hasReplay.ValueKind == JsonValueKind.True));
        }

        return list;
    }

    private async Task requestTokenAsync(DateTime now)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId.Trim(),
            ["client_secret"] = credentials.Secret.Trim(),
            ["grant_type"] = "client_credentials",
            ["scope"] = "public",
        });

        using var response = await http.PostAsync($"{site}/oauth/token", body);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        token = document.RootElement.GetProperty("access_token").GetString();
        // a minute early, so a token never runs out on the way
        tokenExpires = now + TimeSpan.FromSeconds(Math.Max(document.RootElement.GetProperty("expires_in").GetInt32() - 60, 0));
    }

    private bool takeRequest(DateTime now)
    {
        while (requests.Count > 0 && now - requests.Peek() >= TimeSpan.FromMinutes(1))
            requests.Dequeue();

        if (requests.Count >= max_requests_per_minute)
            return false;

        requests.Enqueue(now);
        return true;
    }

    private void keep(int beatmapId, IReadOnlyList<OnlineScore> list)
    {
        scores[beatmapId] = list;
        keptOrder.Enqueue(beatmapId);
        if (keptOrder.Count > kept_beatmaps)
            scores.Remove(keptOrder.Dequeue());
    }

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }
}
