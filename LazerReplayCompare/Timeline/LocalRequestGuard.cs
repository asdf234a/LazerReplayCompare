namespace LazerReplayCompare;

/// <summary>
/// Decides which requests the local API server answers. The server only listens on the loopback address, but a
/// web page open in any browser on this PC can still send requests to 127.0.0.1, so requests are also checked by
/// what the browser says about them:
/// <list type="bullet">
/// <item>Origin and Sec-Fetch-Site: only pages served from this PC (tosu's overlays) are answered. Requests from
/// other websites are refused before any work is done, including ones the page cannot read the answer of (an
/// image tag, a plain form post). Programs that are not browsers send neither header and are answered.</item>
/// <item>Host: must name this PC, which stops DNS rebinding (a website whose domain is pointed at 127.0.0.1 after
/// the page has loaded).</item>
/// <item>File paths in queries must lie inside the osu! folders, so the server never opens anything else.</item>
/// </list>
/// </summary>
public static class LocalRequestGuard
{
    private static readonly string[] loopback_hosts = { "127.0.0.1", "localhost", "[::1]", "::1" };

    /// <summary>Why a request is refused, or null if it may be answered.</summary>
    public static string? Refusal(IReadOnlyDictionary<string, string> headers, int port)
    {
        if (!IsAllowedHost(headers.GetValueOrDefault("Host"), port))
            return "host";
        var origin = headers.GetValueOrDefault("Origin");
        if (!IsAllowedOrigin(origin))
            return "origin";
        // Requests a page cannot read the answer of (an image tag, a link) carry no Origin, but browsers still say
        // they come from another site. An overlay opened as localhost asking 127.0.0.1 is also "cross-site", so this
        // only applies when there is no Origin to go by.
        if (origin == null && string.Equals(headers.GetValueOrDefault("Sec-Fetch-Site"), "cross-site", StringComparison.OrdinalIgnoreCase))
            return "cross-site";
        return null;
    }

    /// <summary>A missing Host is allowed (browsers always send one); otherwise it must be this PC on our port.</summary>
    public static bool IsAllowedHost(string? host, int port)
    {
        if (host == null)
            return true;

        foreach (var name in loopback_hosts)
        {
            if (host.Equals($"{name}:{port}", StringComparison.OrdinalIgnoreCase))
                return true;
            if (port == 80 && host.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// No Origin (not a browser, or a same-origin GET) is allowed. "null" (a file opened from disk, a sandboxed frame)
    /// and every website are refused; pages served from this PC on any port (tosu's overlays) are allowed.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin)
    {
        if (origin == null)
            return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        return uri.IsLoopback && uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
    }

    /// <summary>
    /// Whether a file path from a query lies inside one of the given folders (the osu!lazer and osu!stable folders and
    /// tosu's songs folder). Relative paths, ".." tricks and paths that only share a prefix with a folder are refused.
    /// </summary>
    public static bool IsInside(string? path, IEnumerable<string?> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                continue;

            string fullRoot;
            try
            {
                fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            }
            catch (Exception)
            {
                continue;
            }

            if (full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
