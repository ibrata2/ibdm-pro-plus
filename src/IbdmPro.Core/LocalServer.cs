using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IbdmPro.Core;

/// <summary>What the browser extension sends when it hands a download over.</summary>
public sealed class CaptureRequest
{
    /// <summary>"file" (a browser download), "link" (right-click on a link), "media" (a video on the page).</summary>
    public string Kind { get; set; } = "file";
    public string Url { get; set; } = "";
    public string? PageUrl { get; set; }
    public string? Referrer { get; set; }
    public string? FileName { get; set; }
    public string? Mime { get; set; }
    public long Size { get; set; } = -1;
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Title { get; set; }
    public string QueueName { get; set; } = Settings.DefaultQueueName;

    public RequestInfo ToRequestInfo() => new() { Cookies = Cookies, UserAgent = UserAgent, Referrer = Referrer ?? PageUrl };
}

/// <summary>A tiny HTTP server on 127.0.0.1 that only the IBDM Pro browser extension talks to.
/// Requests from ordinary web pages are refused, so a website cannot start downloads on its own.</summary>
public sealed class LocalServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public string Version { get; set; } = "1.0.0";

    public event Action<CaptureRequest>? Captured;
    public event Action? ShowRequested;
    public DateTime LastExtensionContact { get; private set; } = DateTime.MinValue;
    /// <summary>Each browser install of the extension has its own chrome-extension://id origin.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ExtensionOrigins { get; } = new();

    public LocalServer(int port = Settings.Port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { if (_cts.IsCancellationRequested) return; continue; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (method, path, headers, body) = await ReadRequest(stream, timeout.Token);
            var origin = headers.GetValueOrDefault("origin");
            var fromExtension = origin is null || IsExtensionOrigin(origin);

            if (!fromExtension) { await Respond(stream, 403, "{\"ok\":false}", null); return; }
            if (method == "OPTIONS") { await Respond(stream, 204, "", origin); return; }

            // Every real call carries this header; a web page cannot add it without a preflight we refuse.
            var marked = headers.ContainsKey("x-ibdm-pro");
            switch (method, path)
            {
                case ("GET", "/ping"):
                    if (origin is not null || marked)
                    {
                        // only the extension sends these
                        LastExtensionContact = DateTime.Now;
                        ExtensionOrigins[origin ?? "extension"] = DateTime.Now;
                    }
                    await Respond(stream, 200, PingJson(), origin);
                    break;
                case ("POST", "/add") when marked:
                    LastExtensionContact = DateTime.Now;
                    var req = JsonSerializer.Deserialize<CaptureRequest>(body, Json.Web);
                    if (req is null || !Uri.TryCreate(req.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                    {
                        await Respond(stream, 400, "{\"ok\":false,\"error\":\"bad url\"}", origin);
                        break;
                    }
                    Captured?.Invoke(req);
                    await Respond(stream, 200, "{\"ok\":true}", origin);
                    break;
                case ("GET", "/status"):
                    // When the extension last called (used by the automatic extension installer to confirm it worked).
                    var ago = LastExtensionContact == DateTime.MinValue ? -1 : (int)(DateTime.Now - LastExtensionContact).TotalSeconds;
                    await Respond(stream, 200, $"{{\"extensionSecondsAgo\":{ago}}}", origin);
                    break;
                case ("POST", "/show") when marked:
                    ShowRequested?.Invoke();
                    await Respond(stream, 200, "{\"ok\":true}", origin);
                    break;
                default:
                    await Respond(stream, 404, "{\"ok\":false}", origin);
                    break;
            }
        }
        catch (Exception e) { Log.Write($"server: {e.Message}"); }
    }

    // Links the user chose to download with the browser itself: the extension lets them through for a few minutes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> BrowserOnlyUrls = new();

    public static void LetBrowserDownload(params string?[] urls)
    {
        foreach (var u in urls)
            if (!string.IsNullOrWhiteSpace(u)) BrowserOnlyUrls[u] = DateTime.Now.AddMinutes(3);
    }

    private static string[] BrowserOnly()
    {
        foreach (var (u, until) in BrowserOnlyUrls)
            if (until < DateTime.Now) BrowserOnlyUrls.TryRemove(u, out _);
        return BrowserOnlyUrls.Keys.ToArray();
    }

    private string PingJson()
    {
        var s = Settings.Current;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            app = "IBDM Pro",
            version = Version,
            capture = s.CaptureEnabled,
            extensions = s.CaptureExtensions,
            minSizeKB = s.MinCaptureSizeKB,
            excluded = s.ExcludedSites,
            browserOnly = BrowserOnly(),
            theme = s.ResolvedTheme,
            lang = s.Language == "en" ? "en" : "ar", // and its language // the extension's button and window follow the app's style
        }, Json.Web);
    }

    public static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("safari-web-extension://", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) throw new IOException("closed");
            buffer.Write(chunk, 0, n);
            if (buffer.Length > 64 * 1024) throw new IOException("headers too large");
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        var all = buffer.ToArray();
        var head = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) headers[l[..i].Trim().ToLowerInvariant()] = l[(i + 1)..].Trim();
        }
        var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
        if (length > 2 * 1024 * 1024) throw new IOException("body too large");
        var body = new MemoryStream();
        body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
        while (body.Length < length)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) break;
            body.Write(chunk, 0, n);
        }
        var path = first.Length > 1 ? first[1].Split('?')[0] : "/";
        return (first[0].ToUpperInvariant(), path, headers, Encoding.UTF8.GetString(body.ToArray(), 0, (int)Math.Min(body.Length, length)));
    }

    private static int IndexOf(byte[] data, int count, ReadOnlySpan<byte> needle) => data.AsSpan(0, count).IndexOf(needle);

    private static async Task Respond(NetworkStream stream, int code, string json, string? origin)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {(code is 200 or 204 ? "OK" : "Error")}\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (origin is not null && IsExtensionOrigin(origin))
        {
            sb.Append($"Access-Control-Allow-Origin: {origin}\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, X-IBDM-Pro\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            sb.Append("Vary: Origin\r\n");
        }
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head);
        if (body.Length > 0) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    /// <summary>Asks an already-running IBDM Pro to show its window (used by a second launch).</summary>
    public static bool TryShowRunning(int port = Settings.Port)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/show") { Content = new StringContent("{}") };
            req.Headers.Add("X-IBDM-Pro", "1");
            return c.Send(req).IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
    }
}


