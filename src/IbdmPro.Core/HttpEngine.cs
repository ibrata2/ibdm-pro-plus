using System.Net;
using System.Net.Http.Headers;

namespace IbdmPro.Core;

public sealed class ProbeResult
{
    public string FinalUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; } = -1;
    public bool Resumable { get; set; }
    public string? ContentType { get; set; }
    public string? Error { get; set; }
    public bool LooksLikeWebPage => ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
}

public static class Http
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>One client for everything: no automatic decompression (byte ranges must match the file on the server)
    /// and cookies passed by hand from the browser.</summary>
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        MaxConnectionsPerServer = 64,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpRequestMessage Request(string url, RequestInfo info, long? from = null, long? to = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(info.UserAgent) ? DefaultUserAgent : info.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrEmpty(info.Referrer)) req.Headers.TryAddWithoutValidation("Referer", info.Referrer);
        if (!string.IsNullOrEmpty(info.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", info.Cookies);
        if (from is not null) req.Headers.Range = new RangeHeaderValue(from, to);
        return req;
    }

    /// <summary>Asks the server for the first byte only: that tells the size, the real name, the type,
    /// and whether the file can be fetched in pieces (several connections, pause and resume).</summary>
    public static async Task<ProbeResult> ProbeAsync(string url, RequestInfo info, CancellationToken ct = default)
    {
        var result = new ProbeResult { FinalUrl = url };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var req = Request(url, info, 0, 0);
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            result.FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            if (!resp.IsSuccessStatusCode)
            {
                result.Error = L.F("الخادم رفض الطلب ({0} {1})", (int)resp.StatusCode, resp.ReasonPhrase);
                return result;
            }
            var headers = resp.Content.Headers;
            result.ContentType = headers.ContentType?.MediaType;
            if (resp.StatusCode == HttpStatusCode.PartialContent && headers.ContentRange?.Length is long total)
            {
                result.Size = total;
                result.Resumable = true;
            }
            else
            {
                result.Size = headers.ContentLength ?? -1;
                result.Resumable = false;
            }
            result.FileName = NameFrom(headers.ContentDisposition, result.FinalUrl, url, result.ContentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = L.T("انتهت مهلة الاتصال بالخادم");
        }
        catch (HttpRequestException e)
        {
            result.Error = L.T("تعذر الاتصال: ") + (e.InnerException?.Message ?? e.Message);
        }
        if (string.IsNullOrEmpty(result.FileName)) result.FileName = NameFrom(null, result.FinalUrl, url, result.ContentType);
        return result;
    }

    public static string NameFrom(ContentDispositionHeaderValue? cd, string finalUrl, string originalUrl, string? mime)
    {
        string? name = null;
        if (cd is not null)
        {
            name = cd.FileNameStar;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(cd.FileName))
            {
                name = cd.FileName.Trim('"');
                // Some servers send raw UTF-8 bytes as Latin-1; turn them back into Arabic etc.
                try
                {
                    var bytes = System.Text.Encoding.Latin1.GetBytes(name);
                    var utf8 = System.Text.Encoding.UTF8.GetString(bytes);
                    if (!utf8.Contains('�') && bytes.Any(b => b >= 0x80)) name = utf8;
                }
                catch (Exception) { }
                if (name.Contains('%')) { try { name = Uri.UnescapeDataString(name); } catch (Exception) { } }
            }
        }
        name ??= FileNames.FromUrl(finalUrl);
        if (string.IsNullOrEmpty(Path.GetExtension(name ?? "")) && FileNames.FromUrl(originalUrl) is { } o && Path.GetExtension(o).Length > 0) name = o;
        name = FileNames.Clean(name);
        if (Path.GetExtension(name).Length == 0 && FileNames.ExtForMime(mime) is { } ext) name += "." + ext;
        return name;
    }
}

/// <summary>Limits the total speed of all downloads together (0 = no limit).</summary>
public sealed class SpeedLimiter
{
    private readonly object _gate = new();
    private double _tokens;
    private long _last = Environment.TickCount64;
    public long BytesPerSecond { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            var bps = BytesPerSecond;
            if (bps <= 0) return;
            int wait;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                _tokens = Math.Min(bps, _tokens + (now - _last) / 1000.0 * bps);
                _last = now;
                if (_tokens >= 0) { _tokens -= bytes; return; }
                wait = (int)Math.Clamp(-_tokens / bps * 1000, 5, 500);
            }
            await Task.Delay(wait, ct);
        }
    }
}

public sealed class NotResumableException() : Exception(L.T("الخادم لا يدعم التحميل المجزأ"));

/// <summary>Downloads one file over several connections at once, each fetching its own byte range into the same
/// part file. When a connection finishes early it takes half of the biggest range still left (like IDM), so all
/// connections stay busy to the end. Everything needed to resume is kept in <see cref="DownloadItem.Segments"/>.</summary>
public static class HttpDownloader
{
    public const long MinSegment = 512 * 1024;
    private const int Buffer = 64 * 1024;
    // A weak connection may drop before the first byte of a range arrives. Keep retrying
    // long enough for brief Wi-Fi/router outages without requiring a manual Resume click.
    private const int Retries = 30;

    public static async Task RunAsync(DownloadItem item, int connections, SpeedLimiter limiter, CancellationToken ct)
    {
        Directory.CreateDirectory(item.Folder);
        if (item.Resumable && item.TotalBytes > 0)
        {
            try
            {
                await Segmented(item, Math.Max(1, connections), limiter, ct);
                return;
            }
            catch (NotResumableException)
            {
                Log.Write($"{item.FileName}: server stopped accepting ranges, falling back to one connection");
                item.Resumable = false;
            }
        }
        await Single(item, limiter, ct);
    }

    private static async Task Single(DownloadItem item, SpeedLimiter limiter, CancellationToken ct)
    {
        lock (item.Gate) item.Segments.Clear();
        item.Downloaded = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = Http.Request(item.Url, item.Request);
                using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is long len) item.TotalBytes = len;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(item.PartPath, FileMode.Create, FileAccess.Write, FileShare.Read, Buffer, true);
                item.Downloaded = 0;
                var buf = new byte[Buffer];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await limiter.WaitAsync(n, ct);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    item.AddDownloaded(n);
                }
                if (item.TotalBytes > 0 && item.Downloaded < item.TotalBytes) throw new IOException(L.T("انقطع الاتصال قبل اكتمال الملف"));
                if (item.TotalBytes <= 0) item.TotalBytes = item.Downloaded;
                return;
            }
            catch (Exception e) when (IsTransient(e) && attempt < Retries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 1.5)), ct);
            }
        }
    }

    private static async Task Segmented(DownloadItem item, int connections, SpeedLimiter limiter, CancellationToken ct)
    {
        var total = item.TotalBytes;
        lock (item.Gate)
        {
            var valid = item.Segments.Count > 0 && File.Exists(item.PartPath) && new FileInfo(item.PartPath).Length == total
                && item.Segments.All(s => s.Start >= 0 && s.End < total && s.Done >= 0);
            if (!valid)
            {
                item.Segments.Clear();
                var count = (int)Math.Clamp(total / MinSegment, 1, connections);
                var size = total / count;
                for (var i = 0; i < count; i++)
                {
                    var start = i * size;
                    var end = i == count - 1 ? total - 1 : start + size - 1;
                    item.Segments.Add(new Segment { Start = start, End = end });
                }
                using var f = new FileStream(item.PartPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                f.SetLength(total);
            }
            item.Downloaded = item.Segments.Sum(s => Math.Min(s.Done, s.End - s.Start + 1));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new Dictionary<Task, Segment>();
        void Start(Segment s) => running[Worker(item, s, limiter, cts.Token)] = s;

        lock (item.Gate)
            foreach (var s in item.Segments.Where(s => !s.Finished).Take(connections)) Start(s);

        try
        {
            while (running.Count > 0)
            {
                var done = await Task.WhenAny(running.Keys);
                running.Remove(done);
                await done; // rethrows a failure, which cancels the rest below

                lock (item.Gate)
                {
                    // Idle connections first pick up whole ranges nobody is working on (after a resume with fewer connections)…
                    var busy = running.Values.ToHashSet();
                    while (running.Count < connections && item.Segments.FirstOrDefault(s => !s.Finished && !busy.Contains(s)) is { } waiting)
                    {
                        Start(waiting);
                        busy.Add(waiting);
                    }
                    // …then split the biggest range still being fetched and give its second half to a new connection.
                    while (running.Count < connections)
                    {
                        var biggest = running.Values.Where(s => !s.Finished).OrderByDescending(s => s.Remaining).FirstOrDefault();
                        if (biggest is null || biggest.Remaining < MinSegment * 2) break;
                        var mid = biggest.Position + biggest.Remaining / 2;
                        var tail = new Segment { Start = mid, End = biggest.End };
                        biggest.End = mid - 1;
                        item.Segments.Add(tail);
                        Start(tail);
                    }
                }
            }
        }
        catch
        {
            cts.Cancel();
            try { await Task.WhenAll(running.Keys); } catch (Exception) { }
            throw;
        }

        lock (item.Gate)
        {
            if (item.Segments.Any(s => !s.Finished)) throw new IOException(L.T("لم تكتمل كل أجزاء الملف"));
            item.Downloaded = total;
        }
    }

    private static async Task Worker(DownloadItem item, Segment seg, SpeedLimiter limiter, CancellationToken ct)
    {
        await Task.Yield();
        var buf = new byte[Buffer];
        for (var attempt = 1; ; attempt++)
        {
            long from, to;
            lock (item.Gate)
            {
                if (seg.Finished) return;
                from = seg.Position;
                to = seg.End;
            }
            try
            {
                using var req = Http.Request(item.Url, item.Request, from, to);
                using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.OK) throw new NotResumableException();
                if ((int)resp.StatusCode is 429 or >= 500) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new HttpRequestException(L.F("الخادم رفض الطلب ({0} {1})", (int)resp.StatusCode, resp.ReasonPhrase), null, resp.StatusCode);
                if (resp.Content.Headers.ContentRange?.From is long realFrom && realFrom != from) throw new NotResumableException();

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(item.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, Buffer, true);
                dst.Position = from;
                while (true)
                {
                    var n = await src.ReadAsync(buf, ct);
                    if (n == 0) break;
                    int take;
                    lock (item.Gate)
                    {
                        var allowed = seg.End - seg.Position + 1; // the range may have been shortened by a split
                        take = (int)Math.Min(n, Math.Max(0, allowed));
                    }
                    if (take > 0)
                    {
                        await limiter.WaitAsync(take, ct);
                        await dst.WriteAsync(buf.AsMemory(0, take), ct);
                        lock (item.Gate) seg.Done += take;
                        item.AddDownloaded(take);
                        attempt = 0; // progress made: reset the retry budget
                    }
                    if (take < n) break;
                }
                lock (item.Gate) if (seg.Finished) return;
                throw new IOException(L.T("انقطع الاتصال"));
            }
            catch (Exception e) when (IsTransient(e) && attempt < Retries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 0.5 + attempt * 1.5)), ct);
            }
        }
    }

    private static bool IsTransient(Exception e) => e switch
    {
        NotResumableException => false,
        HttpRequestException h => h.StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError,
        IOException => true,
        TimeoutException => true,
        _ => false,
    };
}


