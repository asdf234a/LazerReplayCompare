using System.Net.Http.Headers;
using System.Text.Json;

namespace LazerReplayCompare;

public sealed record LeaderboardScore(int Position, long UserId, string Name, long Score);

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

    private readonly Dictionary<int, IReadOnlyList<LeaderboardScore>> scores = new();
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

    private async Task<IReadOnlyList<LeaderboardScore>?> fetchAsync(int beatmapId, DateTime now)
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

        var list = new List<LeaderboardScore>();
        foreach (var score in document.RootElement.GetProperty("scores").EnumerateArray())
        {
            var name = score.TryGetProperty("user", out var user) && user.TryGetProperty("username", out var username) ? username.GetString() : null;
            list.Add(new LeaderboardScore(list.Count + 1, score.GetProperty("user_id").GetInt64(), name ?? string.Empty, score.GetProperty("total_score").GetInt64()));
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

    private void keep(int beatmapId, IReadOnlyList<LeaderboardScore> list)
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
