using System.Net;
using System.Net.Sockets;
using System.Text;
using KatanaAirWinHost.Bridge;

namespace KatanaAirWinHost.Http;

/// <summary>
/// Minimal HTTP/1.1 server (TcpListener).
/// Routes: /$$&lt;name&gt;?&lt;args&gt; ($$ protocol), /__status (status page),
/// /__status.json (status JSON), /__status/reconnect (POST, reconnect request), /__debug
/// (debug JSON), /__proxy?url= (cloud proxy), /__host/&lt;file&gt;.js (embedded injection script),
/// anything else (static files; index.html gets head/tail injected).
/// </summary>
internal sealed class HttpServer : IDisposable
{
    /// <summary>Initial request-header buffer (doubled on demand).</summary>
    private const int InitialHeadBytes = 16 * 1024;

    /// <summary>
    /// Request-header upper bound. Request headers carry URL-encoded SysEx parameters and whole
    /// livesets (hundreds of KB); the limit is far above 64 KB.
    /// </summary>
    private const int MaxHeadBytes = 8 * 1024 * 1024;
    private const string HostPrefix = "/__host/";

    private readonly TcpListener _listener;
    private readonly string _webRoot;
    private readonly string _webRootPrefix;
    private readonly Func<string, IReadOnlyList<string>, string> _commandHandler;
    private readonly Func<int, string> _debugProvider;
    private readonly Func<int, string> _statusJsonProvider;
    private readonly Action _reconnectRequested;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public int Port { get; }

    public HttpServer(
        string webRoot,
        int? port,
        Func<string, IReadOnlyList<string>, string> commandHandler,
        Func<int, string> debugProvider,
        Func<int, string> statusJsonProvider,
        Action reconnectRequested)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _webRootPrefix = _webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _commandHandler = commandHandler;
        _debugProvider = debugProvider;
        _statusJsonProvider = statusJsonProvider;
        _reconnectRequested = reconnectRequested;

        _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break; // interrupted by Dispose
            }

            _ = Task.Run(() => ServeClientAsync(client));
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;

                string[]? head = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head is null) return;

                var parts = head[0].Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0];
                string target = parts[1];
                int qi = target.IndexOf('?');
                string path = qi >= 0 ? target[..qi] : target;
                string query = qi >= 0 ? target[(qi + 1)..] : "";

                if (path.StartsWith("/$$", StringComparison.Ordinal))
                {
                    await ServeCommandAsync(stream, path[1..], query).ConfigureAwait(false);
                    return;
                }

                if (path == "/__status")
                {
                    if (!IsGet(method))
                    {
                        await ServeMethodNotAllowedAsync(stream).ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(stream, 200, "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(StatusPage.Html)).ConfigureAwait(false);
                    return;
                }

                if (path == "/__status.json")
                {
                    if (!IsGet(method))
                    {
                        await ServeMethodNotAllowedAsync(stream).ConfigureAwait(false);
                        return;
                    }

                    string statusJson = _statusJsonProvider(Port);
                    await WriteResponseAsync(stream, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(statusJson)).ConfigureAwait(false);
                    return;
                }

                if (path == "/__status/reconnect")
                {
                    if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await ServeMethodNotAllowedAsync(stream).ConfigureAwait(false);
                        return;
                    }

                    _reconnectRequested();
                    await WriteResponseAsync(stream, 204, null, Array.Empty<byte>()).ConfigureAwait(false);
                    return;
                }

                if (path == "/__debug")
                {
                    string json = _debugProvider(Port);
                    await WriteResponseAsync(stream, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                    return;
                }

                if (path == "/__proxy")
                {
                    await ServeProxyAsync(stream, query).ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith(HostPrefix, StringComparison.Ordinal))
                {
                    await ServeInjectAsync(stream, path[HostPrefix.Length..]).ConfigureAwait(false);
                    return;
                }

                await ServeStaticAsync(stream, path).ConfigureAwait(false);
            }
        }
        catch
        {
            // Client disconnect or incomplete request.
        }
    }

    // ------------------------------------------------------------------ routes

    private static bool IsGet(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);

    private static async Task ServeMethodNotAllowedAsync(NetworkStream stream) =>
        await WriteResponseAsync(stream, 405, "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("method not allowed")).ConfigureAwait(false);

    private async Task ServeCommandAsync(NetworkStream stream, string command, string query)
    {
        var args = ParseCommandArgs(query);

        string payload;
        try
        {
            payload = _commandHandler(command, args);
        }
        catch (Exception ex)
        {
            // A command error returns "v" (undefined) and writes to stderr.
            payload = "v";
            Console.Error.WriteLine($"[cmd] {command}: {ex.Message}");
        }

        await WriteResponseAsync(stream, 200, "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
    }

    private async Task ServeProxyAsync(NetworkStream stream, string query)
    {
        string url = "";
        try
        {
            var parameters = ParseQueryString(query);
            if (parameters.TryGetValue("url", out var value)) url = value;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[proxy] failed to parse query string: {ex.Message}");
        }

        ProxyResponse result = await CloudProxy.FetchAsync(url).ConfigureAwait(false);
        await WriteResponseAsync(stream, result.Status, result.ContentType, result.Body).ConfigureAwait(false);
    }

    private async Task ServeInjectAsync(NetworkStream stream, string fileName)
    {
        string name;
        try { name = Uri.UnescapeDataString(fileName); }
        catch { name = fileName; }

        byte[]? body = InjectResources.Get(name);
        if (body is null)
        {
            await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("inject resource not found")).ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(stream, 200, "text/javascript; charset=utf-8", body).ConfigureAwait(false);
    }

    private async Task ServeStaticAsync(NetworkStream stream, string path)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); }
        catch { decoded = path; }

        if (decoded.Length == 0 || decoded == "/") decoded = "/index.html";

        string relative = decoded.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_webRoot, relative));

        // Directory traversal guard: the resolved path stays inside the web root.
        if (!full.StartsWith(_webRootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, _webRoot, StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(full))
        {
            await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        byte[] body = await File.ReadAllBytesAsync(full).ConfigureAwait(false);

        if (Path.GetFileName(full).Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            string html = HtmlInjector.Inject(Encoding.UTF8.GetString(body));
            body = Encoding.UTF8.GetBytes(html);
        }

        await WriteResponseAsync(stream, 200, MimeFor(full), body).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ HTTP details

    private static async Task<string[]?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[InitialHeadBytes];
        int total = 0;
        int scanned = 0;

        // Read through \r\n\r\n; the buffer doubles on demand up to MaxHeadBytes.
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length >= MaxHeadBytes) return null;   // over the limit
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxHeadBytes));
            }

            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;

            // Scan only the new region (3-byte overlap) instead of the whole buffer.
            int from = Math.Max(0, scanned - 3);
            if (Encoding.Latin1.GetString(buffer, from, total - from)
                .Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
            scanned = total;
        }

        if (total == 0) return null;

        string text = Encoding.Latin1.GetString(buffer, 0, total);
        return text.Split("\r\n");
    }

    /// <summary>Splits a $$ query into ','-separated, URI-unescaped arguments.</summary>
    private static List<string> ParseCommandArgs(string query)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(query)) return list;

        foreach (string part in query.Split(','))
            list.Add(Uri.UnescapeDataString(part));
        return list;
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return map;

        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;

            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair[..eq] : pair;
            string value = eq >= 0 ? pair[(eq + 1)..] : "";
            map[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return map;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string? contentType, byte[] body)
    {
        string reason = status switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            301 => "Moved Permanently",
            302 => "Found",
            304 => "Not Modified",
            307 => "Temporary Redirect",
            308 => "Permanent Redirect",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            408 => "Request Timeout",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => "Status",
        };

        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        if (!string.IsNullOrEmpty(contentType))
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" or ".map" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        // Called explicitly by the $$app_exit path and again by the using scope; idempotent.
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
