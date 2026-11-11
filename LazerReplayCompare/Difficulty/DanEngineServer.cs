using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace LazerReplayCompare;

/// <summary>
/// The dan engine's own local server (127.0.0.1:24053, or the next free port). Over HTTP it serves the
/// analyser's files (GET /analyser/... is a file under the analyser folder next to the exe), and its
/// websocket (ws://127.0.0.1:port/) is what the engine's page, running in the headless browser, takes
/// charts from and answers on. Nothing else is served.
/// <para>
/// Only this PC is answered: the listener is bound to the loopback address, and requests are checked with
/// <see cref="LocalRequestGuard"/> (Host, Origin, Sec-Fetch-Site), so another website open in a browser
/// can't read the files or reach the socket. The websocket is stricter still: a browser must be on a page
/// this server served. No Access-Control-Allow-Origin is sent.
/// </para>
/// </summary>
public sealed class DanEngineServer : IDisposable
{
    public const int DefaultPort = 24053;

    /// <summary>How many ports are tried, from the one asked for upwards</summary>
    public const int PortsToTry = 20;

    /// <summary>The longest message a page may send (results are a few hundred bytes)</summary>
    private const int MaxMessageBytes = 1024 * 1024;

    /// <summary>A page connected over the websocket.</summary>
    public sealed class Connection
    {
        internal Connection(WebSocket socket) => Socket = socket;

        internal WebSocket Socket { get; }
        internal readonly Channel<string> Outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        internal volatile bool Closed;

        public bool IsOpen => !Closed && Socket.State == WebSocketState.Open;

        /// <summary>Queues a text message; it is written by the connection's own writer, so the caller never waits for the page.</summary>
        public void Send(string text)
        {
            if (!Closed) Outbox.Writer.TryWrite(text);
        }

        /// <summary>Drops the connection.</summary>
        public void Close()
        {
            Closed = true;
            Outbox.Writer.TryComplete();
            try
            {
                Socket.Abort();
            }
            catch
            {
                // already gone
            }
        }
    }

    private static readonly Dictionary<string, string> content_types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8", [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8", [".wasm"] = "application/wasm", [".onnx"] = "application/octet-stream",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp", [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf",
    };

    private readonly object gate = new();
    private readonly List<Connection> connections = new();
    private readonly string root;
    private HttpListener? listener;
    private volatile bool disposed;

    /// <param name="analyserDir">The folder served as /analyser/; by default "analyser" next to the exe.</param>
    public DanEngineServer(string? analyserDir = null)
    {
        AnalyserDir = Path.GetFullPath(analyserDir ?? DefaultAnalyserDir);
        root = Path.TrimEndingDirectorySeparator(AnalyserDir) + Path.DirectorySeparatorChar;
    }

    public static string DefaultAnalyserDir => Path.Combine(AppContext.BaseDirectory, "analyser");

    public string AnalyserDir { get; }

    /// <summary>The port being listened on, or 0 before <see cref="Start"/></summary>
    public int Port { get; private set; }

    public bool IsRunning => listener != null;

    /// <summary>http://127.0.0.1:port (no trailing slash); what a page served from here has as its Origin</summary>
    public string Origin => $"http://127.0.0.1:{Port}";

    /// <summary>The engine's page, with the number of Web Workers it should start</summary>
    public string PageUrl(int workers) => $"{Origin}/analyser/lr2hp/?workers={workers}";

    /// <summary>A page sent a text message (on a thread pool thread).</summary>
    public event Action<Connection, string>? MessageReceived;

    /// <summary>A page's websocket closed.</summary>
    public event Action<Connection>? Disconnected;

    /// <summary>
    /// Starts listening on <paramref name="port"/>, or the first free port of the <see cref="PortsToTry"/>
    /// after it. False when none could be opened. Does nothing when already started.
    /// </summary>
    public bool Start(int port = DefaultPort)
    {
        lock (gate)
        {
            if (disposed) return false;
            if (listener != null) return true;
            for (var p = port; p < port + PortsToTry && p <= 65535; p++)
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{p}/");
                try
                {
                    l.Start();
                }
                catch (Exception e) when (e is HttpListenerException or InvalidOperationException or PlatformNotSupportedException)
                {
                    try
                    {
                        l.Close();
                    }
                    catch
                    {
                        // never started
                    }
                    continue;
                }
                listener = l;
                Port = p;
                _ = Task.Run(() => AcceptLoop(l));
                return true;
            }
            InternalLogger.Log($"dan engine server: no free port from {port} to {port + PortsToTry - 1}");
            return false;
        }
    }

    private async Task AcceptLoop(HttpListener l)
    {
        while (!disposed && l.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await l.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (disposed || !l.IsListening) return;
                continue;
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in request.Headers.AllKeys)
                if (name != null && request.Headers[name] is { } value)
                    headers[name] = value;

            var refusal = !request.IsLocal ? "remote" : LocalRequestGuard.Refusal(headers, Port);
            // The socket is for the page this server served; a program that is not a browser sends no Origin
            if (refusal == null && request.IsWebSocketRequest && headers.TryGetValue("Origin", out var origin)
                && !origin.Equals(Origin, StringComparison.OrdinalIgnoreCase))
                refusal = "origin";
            if (refusal != null)
            {
                InternalLogger.Log($"dan engine server: refused a request ({refusal}) from Origin {headers.GetValueOrDefault("Origin") ?? "-"}, Host {headers.GetValueOrDefault("Host") ?? "-"}");
                Respond(context, 403, "text/plain; charset=utf-8", "forbidden"u8.ToArray());
                return;
            }

            if (request.IsWebSocketRequest)
            {
                if (request.Url?.AbsolutePath != "/")
                {
                    Respond(context, 404, "text/plain; charset=utf-8", "not found"u8.ToArray());
                    return;
                }
                var socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
                await Serve(new Connection(socket)).ConfigureAwait(false);
                return;
            }
            ServeFile(context);
        }
        catch (Exception e)
        {
            // The browser went away mid-request
            if (!disposed && e is not (HttpListenerException or ObjectDisposedException or WebSocketException or IOException))
                InternalLogger.Log($"dan engine server: {e.GetType().Name}: {e.Message}");
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // already closed
            }
        }
    }

    // ------------------------------------------------------------ files

    private void ServeFile(HttpListenerContext context)
    {
        var request = context.Request;
        if (request.HttpMethod is not ("GET" or "HEAD"))
        {
            Respond(context, 405, "text/plain; charset=utf-8", "GET only"u8.ToArray());
            return;
        }
        // The path as it was sent (HttpListener's Url is already normalised), without the query
        var target = request.RawUrl ?? "/";
        var q = target.IndexOfAny(['?', '#']);
        if (q >= 0) target = target[..q];
        if (Resolve(target, out var redirect) is not { } full)
        {
            if (redirect != null)
            {
                context.Response.Headers["Location"] = redirect;
                Respond(context, 301, "text/plain; charset=utf-8", []);
                return;
            }
            Respond(context, 404, "text/plain; charset=utf-8", "not found"u8.ToArray());
            return;
        }
        byte[] body;
        try
        {
            body = File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Respond(context, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(e.Message));
            return;
        }
        Respond(context, 200, content_types.GetValueOrDefault(Path.GetExtension(full), "application/octet-stream"), body);
    }

    /// <summary>
    /// The file a request path (/analyser/..., still percent-encoded) names, or null when there is none
    /// or it would lie outside the analyser folder. <paramref name="redirect"/> is set for a folder
    /// asked for without its slash, so the page's relative links work.
    /// </summary>
    internal string? Resolve(string requestPath, out string? redirect)
    {
        redirect = null;
        string path;
        try
        {
            path = Uri.UnescapeDataString(requestPath);
        }
        catch (UriFormatException)
        {
            return null;
        }
        if (path != "/analyser" && !path.StartsWith("/analyser/", StringComparison.Ordinal)) return null;
        path = path["/analyser".Length..];
        // No drive letters, alternate data streams, backslashes or control characters: only names under the folder
        if (path.Contains(':') || path.Contains('\\') || path.Any(char.IsControl)) return null;
        if (path.EndsWith('/')) path += "index.html";
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(full)) return full;
        if (Directory.Exists(full))
            redirect = "/analyser" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)) + "/";
        return null;
    }

    private static void Respond(HttpListenerContext context, int status, string type, byte[] body)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = type;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.ContentLength64 = body.Length;
        if (context.Request.HttpMethod != "HEAD" && body.Length > 0) response.OutputStream.Write(body, 0, body.Length);
        response.Close();
    }

    // ------------------------------------------------------------ the websocket

    private async Task Serve(Connection connection)
    {
        lock (connections) connections.Add(connection);
        var writer = Task.Run(() => WriteLoop(connection));
        try
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (!disposed && connection.Socket.State == WebSocketState.Open)
            {
                var read = await connection.Socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (read.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, read.Count);
                if (message.Length > MaxMessageBytes) break;
                if (!read.EndOfMessage) continue;
                var text = read.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) : null;
                message.SetLength(0);
                if (text == null) continue;
                try
                {
                    MessageReceived?.Invoke(connection, text);
                }
                catch (Exception e)
                {
                    InternalLogger.Log($"dan engine server: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException or HttpListenerException)
        {
            // connection dropped
        }
        finally
        {
            connection.Close();
            lock (connections) connections.Remove(connection);
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch
            {
                // it ended with the connection
            }
            connection.Socket.Dispose();
            try
            {
                Disconnected?.Invoke(connection);
            }
            catch (Exception e)
            {
                InternalLogger.Log($"dan engine server: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private static async Task WriteLoop(Connection connection)
    {
        try
        {
            await foreach (var text in connection.Outbox.Reader.ReadAllAsync().ConfigureAwait(false))
                await connection.Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The page is gone: the reader notices too
            connection.Close();
        }
    }

    public void Dispose()
    {
        HttpListener? l;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            l = listener;
            listener = null;
        }
        Connection[] open;
        lock (connections) open = connections.ToArray();
        foreach (var c in open) c.Close();
        try
        {
            l?.Close();
        }
        catch
        {
            // already closed
        }
    }
}
