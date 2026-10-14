namespace LazerReplayCompare;

/// <summary>The game the live play runs in (tosu's <c>client</c>), which decides how a replay is scored for comparison.</summary>
public enum LiveClient
{
    Lazer,
    Stable,
}

public static class LiveClients
{
    /// <summary>tosu reports "stable" or "lazer"; anything else (or nothing) is treated as osu!lazer.</summary>
    public static LiveClient Parse(string? value)
        => string.Equals(value?.Trim(), "stable", StringComparison.OrdinalIgnoreCase) ? LiveClient.Stable : LiveClient.Lazer;
}
