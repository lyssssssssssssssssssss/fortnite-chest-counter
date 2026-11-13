using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChestCounter.Core.Stats;

namespace ChestCounter.Core.Web;

/// <summary>
/// Local dashboard server: http://localhost:PORT only (loopback; not reachable from other devices).
/// Serves the embedded dashboard, a read-mostly JSON API and a Server-Sent Events stream that pushes an
/// "update" message whenever the database changes, so the page refreshes the moment a replay is processed.
/// </summary>
public sealed class WebServer : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly StatsService _stats;
    private readonly Func<bool, object> _sessionAction; // true = start, false = stop
    private readonly Action _rescan;
    private readonly HttpListener _listener = new();
    private readonly List<HttpListenerResponse> _sseClients = new();
    private readonly object _sseGate = new();
    private readonly Dictionary<string, (byte[] Data, string ContentType)> _assets = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _heartbeat;
    private volatile bool _running;

    public WebServer(StatsService stats, Func<bool, object> sessionAction, Action rescan)
    {
        _stats = stats;
        _sessionAction = sessionAction;
        _rescan = rescan;
        LoadAssets();
        _stats.Invalidated += BroadcastUpdate;
    }

    public int Port { get; private set; }
    public string Url => $"http://localhost:{Port}/";
    public bool IsRunning => _running;

    /// <summary>Starts on the preferred port, or the next free one.</summary>
    public void Start(int preferredPort)
    {
        Exception? last = null;
        for (var port = preferredPort; port < preferredPort + 20; port++)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                Port = port;
                _running = true;
                _ = Task.Run(AcceptLoop);
                _heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                Log.Info($"Dashboard available at {Url}");
                return;
            }
            catch (HttpListenerException ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"Could not start the dashboard server near port {preferredPort}: {last?.Message}", last);
    }

    private void LoadAssets()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("wwwroot/", StringComparison.Ordinal))
            {
                continue;
            }
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var file = name["wwwroot/".Length..];
            _assets[file] = (ms.ToArray(), ContentType(file));
        }
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };

    private async Task AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (!_running)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("Dashboard accept failed: " + ex.Message);
                continue;
            }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            // DNS-rebinding protection: only accept requests addressed to localhost.
            var host = req.Headers["Host"] ?? "";
            if (!host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)
                && !host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase))
            {
                Respond(res, 403, "text/plain", "Forbidden");
                return;
            }

            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; script-src 'self'";

            var path = req.Url?.AbsolutePath ?? "/";

            if (req.HttpMethod == "POST")
            {
                // Custom header => browsers must preflight cross-origin requests, which we never allow.
                if (req.Headers["X-Chest-Counter"] != "1")
                {
                    Respond(res, 403, "text/plain", "Forbidden");
                    return;
                }

                switch (path)
                {
                    case "/api/session/start":
                        RespondJson(res, _sessionAction(true));
                        return;
                    case "/api/session/stop":
                        RespondJson(res, _sessionAction(false));
                        return;
                    case "/api/rescan":
                        _rescan();
                        RespondJson(res, new { ok = true });
                        return;
                    default:
                        Respond(res, 404, "text/plain", "Not found");
                        return;
                }
            }

            if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD")
            {
                Respond(res, 405, "text/plain", "Method not allowed");
                return;
            }

            switch (path)
            {
                case "/api/snapshot":
                    RespondJson(res, _stats.GetSnapshot());
                    return;
                case "/api/health":
                    RespondJson(res, new { ok = true, version = _stats.Version });
                    return;
                case "/api/events":
                    OpenEventStream(res);
                    return;
            }

            if (path.StartsWith("/api/match/", StringComparison.Ordinal) && long.TryParse(path["/api/match/".Length..], out var id))
            {
                var detail = _stats.GetMatchDetail(id);
                if (detail is null)
                {
                    Respond(res, 404, "application/json", "{\"error\":\"not found\"}");
                }
                else
                {
                    RespondJson(res, detail);
                }
                return;
            }

            var file = path == "/" ? "index.html" : path.TrimStart('/');
            if (_assets.TryGetValue(file, out var asset))
            {
                res.Headers["Cache-Control"] = "no-cache";
                Respond(res, 200, asset.ContentType, asset.Data);
                return;
            }

            Respond(res, 404, "text/plain", "Not found");
        }
        catch (Exception ex)
        {
            Log.Warn($"Dashboard request failed ({req.Url}): {ex.Message}");
            try { Respond(res, 500, "text/plain", "Internal error"); } catch { }
        }
    }

    private static void RespondJson(HttpListenerResponse res, object value)
    {
        res.Headers["Cache-Control"] = "no-store";
        Respond(res, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }

    private static void Respond(HttpListenerResponse res, int status, string contentType, string body) =>
        Respond(res, status, contentType, Encoding.UTF8.GetBytes(body));

    private static void Respond(HttpListenerResponse res, int status, string contentType, byte[] body)
    {
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body);
        res.OutputStream.Close();
    }

    // ------------------------------------------------------------------------------------------------
    // Server-Sent Events
    // ------------------------------------------------------------------------------------------------

    private void OpenEventStream(HttpListenerResponse res)
    {
        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-store";
        res.SendChunked = true;
        var hello = Encoding.UTF8.GetBytes($"retry: 3000\nevent: update\ndata: {_stats.Version}\n\n");
        res.OutputStream.Write(hello);
        res.OutputStream.Flush();
        lock (_sseGate)
        {
            _sseClients.Add(res);
        }
    }

    private void BroadcastUpdate(long version) => Send($"event: update\ndata: {version}\n\n");

    private void Heartbeat() => Send(": ping\n\n");

    private void Send(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        List<HttpListenerResponse> clients;
        lock (_sseGate)
        {
            clients = _sseClients.ToList();
        }

        foreach (var c in clients)
        {
            try
            {
                c.OutputStream.Write(bytes);
                c.OutputStream.Flush();
            }
            catch
            {
                lock (_sseGate)
                {
                    _sseClients.Remove(c);
                }
                try { c.Abort(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _stats.Invalidated -= BroadcastUpdate;
        _heartbeat?.Dispose();
        lock (_sseGate)
        {
            foreach (var c in _sseClients)
            {
                try { c.Abort(); } catch { }
            }
            _sseClients.Clear();
        }
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
