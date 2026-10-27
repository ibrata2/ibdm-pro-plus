using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IbdmPro.Core;

/// <summary>One choice in the quality list: a video height, or audio only.</summary>
public sealed class MediaOption
{
    public string Label { get; set; } = "";
    public string FormatSpec { get; set; } = "";
    public int Height { get; set; }
    public string Ext { get; set; } = "";
    public long Size { get; set; } = -1;
    public bool SizeIsEstimate { get; set; }
    public bool AudioOnly { get; set; }
    public string? AudioFormat { get; set; }
    /// <summary>For a single plain file, its address — used to ask the server for the exact size when yt-dlp does not know it.</summary>
    public string? DirectUrl { get; set; }
    public string SizeText => Size > 0 ? (SizeIsEstimate ? "\u200E~" : "") + Fmt.Size(Size) : L.T("الحجم غير معروف");
}

public sealed class MediaInfo
{
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    public double Duration { get; set; }
    public string? Site { get; set; }
    public string Url { get; set; } = "";
    public List<MediaOption> Options { get; } = [];
}

/// <summary>Talks to the bundled yt-dlp: lists the qualities a page offers (with sizes) and downloads the chosen one.</summary>
public static class YtDlp
{
    private static ProcessStartInfo Start(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Paths.YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Without these, Arabic titles come back in the console code page and file paths no longer match the disk.
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        psi.ArgumentList.Add("--no-colors");
        // YouTube only lists its real formats to clients that can run its JavaScript; the bundled Deno does that.
        var deno = Paths.Deno;
        if (Path.IsPathRooted(deno) && File.Exists(deno))
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add("deno:" + deno);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static IEnumerable<string> HeaderArgs(RequestInfo info, CookieFile? cookies)
    {
        if (!string.IsNullOrEmpty(info.UserAgent)) { yield return "--user-agent"; yield return info.UserAgent; }
        if (!string.IsNullOrEmpty(info.Referrer)) { yield return "--referer"; yield return info.Referrer; }
        if (cookies is not null) { yield return "--cookies"; yield return cookies.Path; }
    }

    /// <summary>The browser's cookies for the site, as the cookies.txt file yt-dlp expects. Sites like Instagram need
    /// them (signed-in), and yt-dlp no longer accepts them as a plain header. Deleted when the run ends.</summary>
    public sealed class CookieFile : IDisposable
    {
        public string Path { get; }

        private CookieFile(string path) { Path = path; }

        public static CookieFile? For(RequestInfo info, string url)
        {
            if (string.IsNullOrWhiteSpace(info.Cookies)) return null;
            string host;
            try { host = new Uri(url).Host; } catch (Exception) { return null; }
            if (host.StartsWith("www.")) host = host[4..];
            var expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
            var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
            foreach (var part in info.Cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (name.Contains('\t') || value.Contains('\t') || value.Contains('\n')) continue;
                sb.Append($".{host}\tTRUE\t/\tTRUE\t{expires}\t{name}\t{value}\n");
            }
            var file = System.IO.Path.Combine(Paths.AppData, $"cookies-{Guid.NewGuid():N}.txt");
            File.WriteAllText(file, sb.ToString());
            return new CookieFile(file);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (Exception) { }
        }
    }

    /// <summary>Other YouTube clients to try when the default one gives no usable formats.</summary>
    private const string YoutubeFallbackClients = "youtube:player_client=tv,web_safari,mweb,android_vr";

    public static bool IsYoutube(string url)
    {
        try { var h = new Uri(url).Host; return h.EndsWith("youtube.com") || h == "youtu.be" || h.EndsWith("youtube-nocookie.com"); }
        catch (Exception) { return false; }
    }

    /// <summary>"watch?v=ID&amp;list=…&amp;start_radio=1" → "watch?v=ID" (only the video, never the mix/playlist).</summary>
    public static string CleanUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            if (!IsYoutube(url)) return url;
            if (u.Host == "youtu.be") return url.Split('?')[0];
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            var v = q["v"];
            if (u.AbsolutePath == "/watch" && !string.IsNullOrEmpty(v))
            {
                var t = q["t"];
                return $"https://www.youtube.com/watch?v={v}" + (string.IsNullOrEmpty(t) ? "" : $"&t={t}");
            }
        }
        catch (Exception) { }
        return url;
    }

    public static async Task<MediaInfo> GetInfoAsync(string url, RequestInfo info, CancellationToken ct = default)
    {
        url = CleanUrl(url);
        MediaInfo? media = null;
        string? error = null;
        try { media = await GetInfoOnce(url, info, null, ct); }
        catch (InvalidOperationException e) { error = e.Message; }
        // YouTube sometimes refuses one client: ask again as other clients before giving up.
        if (IsYoutube(url) && (media is null || !media.Options.Any(o => !o.AudioOnly)))
        {
            Log.Write($"youtube retry with other clients ({error ?? "no video formats"})");
            try
            {
                var again = await GetInfoOnce(url, info, YoutubeFallbackClients, ct);
                if (again.Options.Count > (media?.Options.Count ?? 0)) { media = again; error = null; }
            }
            catch (InvalidOperationException e) { error ??= e.Message; }
        }
        if (media is null || media.Options.Count == 0)
            throw new InvalidOperationException(error ?? L.T("لم يتم العثور على جودات متوفرة لهذا الفيديو"));
        return media;
    }

    private static async Task<MediaInfo> GetInfoOnce(string url, RequestInfo info, string? extractorArgs, CancellationToken ct)
    {
        // --ignore-no-formats-error: return the video's details even when no format can be picked yet,
        // instead of failing with "Requested format is not available".
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--ignore-config", "--ignore-no-formats-error" };
        if (extractorArgs is not null) { args.Add("--extractor-args"); args.Add(extractorArgs); }
        using var cookies = CookieFile.For(info, url);
        args.AddRange(HeaderArgs(info, cookies));
        args.Add("--");
        args.Add(url);
        using var p = Process.Start(Start(args)) ?? throw new InvalidOperationException(L.T("تعذر تشغيل yt-dlp"));
        using var reg = ct.Register(() => Kill(p));
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;
        if (p.ExitCode != 0 || stdout.Length == 0)
        {
            Log.Write($"yt-dlp -J {url}: {stderr.Trim()}");
            throw new InvalidOperationException(CleanError(stderr) ?? L.T("لم يتم العثور على فيديو في هذه الصفحة"));
        }
        var media = Parse(stdout, url);
        // Ask the server for the real size of plain files whose size yt-dlp did not report.
        await Task.WhenAll(media.Options.Where(o => o.DirectUrl is not null && (o.Size <= 0 || o.SizeIsEstimate)).Select(async o =>
        {
            var probe = await Http.ProbeAsync(o.DirectUrl!, info, ct);
            if (probe.Error is null && probe.Size > 0) { o.Size = probe.Size; o.SizeIsEstimate = false; }
        }));
        return media;
    }

    public static MediaInfo Parse(string json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // A playlist answer (rare with --no-playlist) carries the video in its first entry.
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0) root = entries[0];

        var info = new MediaInfo
        {
            Title = Str(root, "title") ?? "video",
            Thumbnail = Str(root, "thumbnail"),
            Duration = Num(root, "duration"),
            Site = Str(root, "extractor_key"),
            Url = Str(root, "webpage_url") ?? url,
        };

        var formats = new List<JsonElement>();
        if (root.TryGetProperty("formats", out var fs) && fs.ValueKind == JsonValueKind.Array) formats.AddRange(fs.EnumerateArray());
        else formats.Add(root);

        // Generic pages report no codecs at all, only video_ext/audio_ext; such a file is treated as video with sound.
        static bool HasVideo(JsonElement f) => Str(f, "vcodec") is not "none"
            && (Num(f, "height") > 0 || Str(f, "vcodec") is { } v && v != "none" || Str(f, "video_ext") is { } ve && ve != "none");
        static bool HasAudio(JsonElement f) => Str(f, "acodec") is { } a ? a != "none" : Str(f, "audio_ext") is { } ae && ae != "none" || Str(f, "vcodec") is null;
        long SizeOf(JsonElement f, out bool estimate)
        {
            estimate = false;
            if (Num(f, "filesize") is > 0 and var s) return (long)s;
            estimate = true;
            if (Num(f, "filesize_approx") is > 0 and var a) return (long)a;
            if (Num(f, "tbr") is > 0 and var tbr && info.Duration > 0) return (long)(tbr * 1000 / 8 * info.Duration);
            return -1;
        }

        var audios = formats.Where(f => !HasVideo(f) && Str(f, "acodec") is { } a && a != "none").ToList();
        // The audio that pairs with a video: M4A for MP4 (result stays MP4), WebM/Opus for WebM (result stays WebM).
        JsonElement AudioFor(string? videoExt) => audios
            .OrderByDescending(f => videoExt == "webm" ? Str(f, "ext") == "webm" : Str(f, "ext") == "m4a")
            .ThenByDescending(f => Num(f, "abr") > 0 ? Num(f, "abr") : Num(f, "tbr"))
            .FirstOrDefault();
        var bestAudio = AudioFor("mp4");
        long audioSize = -1;
        var audioEst = true;
        if (bestAudio.ValueKind == JsonValueKind.Object) audioSize = SizeOf(bestAudio, out audioEst);

        var videos = formats.Where(f => HasVideo(f) && Num(f, "height") > 0 && Str(f, "protocol") is not "mhtml").ToList();
        foreach (var group in videos.GroupBy(f => (int)Num(f, "height")).OrderByDescending(g => g.Key))
        {
            // Prefer H.264/MP4 (plays everywhere), then the highest bitrate.
            var f = group
                .OrderByDescending(x => Str(x, "vcodec")?.StartsWith("avc") == true && Str(x, "ext") == "mp4")
                .ThenByDescending(x => Str(x, "ext") == "mp4")
                .ThenByDescending(x => Num(x, "fps"))
                .ThenByDescending(x => Num(x, "tbr"))
                .First();
            var id = Str(f, "format_id") ?? "";
            var size = SizeOf(f, out var est);
            var vext = Str(f, "ext");
            var audio = AudioFor(vext);
            var needsAudio = !HasAudio(f) && audio.ValueKind == JsonValueKind.Object;
            if (needsAudio && size > 0)
            {
                var a = SizeOf(audio, out var aEst);
                if (a > 0) size += a; else est = true;
                est |= aEst;
            }
            var aext = needsAudio ? Str(audio, "ext") : null;
            var ext = !needsAudio ? vext ?? "mp4"
                : vext == "mp4" && aext == "m4a" ? "mp4"
                : vext == "webm" && aext == "webm" ? "webm" : "mkv";
            var fps = (int)Num(f, "fps");
            var h = group.Key;
            info.Options.Add(new MediaOption
            {
                Height = h,
                Ext = ext,
                Size = size,
                SizeIsEstimate = est,
                FormatSpec = needsAudio ? $"{id}+{Str(audio, "format_id")}/{id}+bestaudio/{id}/best[height<={h}]/best" : $"{id}/best[height<={h}]/best",
                DirectUrl = !needsAudio && Str(f, "protocol") is "http" or "https" ? Str(f, "url") : null,
                Label = "\u200E" + $"{QualityName(h)}{(fps > 30 ? $" {fps}fps" : "")} — {ext.ToUpperInvariant()}",
            });
        }

        // Pages where yt-dlp only knows one combined file with no height.
        if (info.Options.Count == 0 && formats.Any(HasVideo))
        {
            var f = formats.Last(HasVideo);
            var size = SizeOf(f, out var est);
            info.Options.Add(new MediaOption
            {
                FormatSpec = "best", Ext = Str(f, "ext") ?? "mp4", Size = size, SizeIsEstimate = est,
                Label = L.T("أفضل جودة متوفرة — ") + (Str(f, "ext") ?? "mp4").ToUpperInvariant(),
                DirectUrl = Str(f, "protocol") is "http" or "https" ? Str(f, "url") : null,
            });
        }

        if (bestAudio.ValueKind == JsonValueKind.Object || formats.Any(HasAudio))
        {
            var src = audioSize > 0 ? audioSize : -1;
            var mp3 = info.Duration > 0 ? (long)(info.Duration * 192_000 / 8) : src;
            info.Options.Add(new MediaOption { AudioOnly = true, AudioFormat = "mp3", Ext = "mp3", FormatSpec = "bestaudio/best", Size = mp3, SizeIsEstimate = true, Label = L.T("صوت فقط — MP3") });
            info.Options.Add(new MediaOption { AudioOnly = true, AudioFormat = "m4a", Ext = "m4a", FormatSpec = "bestaudio[ext=m4a]/bestaudio/best", Size = src, SizeIsEstimate = audioEst, Label = L.T("صوت فقط — M4A (الجودة الأصلية)") });
        }
        return info;
    }

    public static string QualityName(int h) => h switch
    {
        >= 4320 => $"{h}p 8K",
        >= 2160 => $"{h}p 4K",
        >= 1440 => $"{h}p 2K",
        >= 1080 => $"{h}p Full HD",
        >= 720 => $"{h}p HD",
        _ => $"{h}p",
    };

    public sealed record Progress(long Downloaded, long Total, double Speed, string? Phase);

    /// <summary>Downloads with yt-dlp. Pausing kills the process; running it again continues from yt-dlp's own .part files.
    /// Returns the final file path as yt-dlp reports it after merging/converting.</summary>
    public static async Task<string> DownloadAsync(DownloadItem item, int connections, long speedLimit, Action<Progress> progress, CancellationToken ct)
    {
        try { return await DownloadOnce(item, connections, speedLimit, progress, false, ct); }
        catch (InvalidOperationException e) when (IsYoutube(item.Url) && !ct.IsCancellationRequested)
        {
            // Continue from the same .part files, asking YouTube as other clients.
            Log.Write($"youtube download retry with other clients: {e.Message}");
            return await DownloadOnce(item, connections, speedLimit, progress, true, ct);
        }
    }

    private static async Task<string> DownloadOnce(DownloadItem item, int connections, long speedLimit, Action<Progress> progress, bool useFallbackClients, CancellationToken ct)
    {
        Directory.CreateDirectory(item.Folder);
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var pathFile = Path.Combine(Paths.AppData, $"ytdlp-{item.Id}.txt");
        File.Delete(pathFile);

        var args = new List<string>
        {
            "--newline", "--no-playlist", "--ignore-config", "--no-mtime", "--progress",
            "--concurrent-fragments", Math.Clamp(connections, 1, 16).ToString(CultureInfo.InvariantCulture),
            "--retries", "10", "--fragment-retries", "10",
            "--progress-template", "download:[IBDM]%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
            "--print-to-file", "after_move:%(filepath)s", pathFile,
            "-f", string.IsNullOrEmpty(item.FormatSpec) ? "bv*+ba/b" : item.FormatSpec,
            "-o", Path.Combine(item.Folder, stem.Replace("%", "%%") + ".%(ext)s"),
        };
        var ffmpeg = Paths.Ffmpeg;
        if (Path.IsPathRooted(ffmpeg)) { args.Add("--ffmpeg-location"); args.Add(ffmpeg); }
        if (speedLimit > 0) { args.Add("--limit-rate"); args.Add(speedLimit.ToString(CultureInfo.InvariantCulture)); }
        if (item.AudioOnly)
        {
            args.AddRange(["-x", "--audio-format", item.AudioFormat ?? "mp3", "--audio-quality", "0"]);
        }
        else if (item.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--merge-output-format", "mp4"]);
        }
        using var cookies = CookieFile.For(item.Request, item.Url);
        args.AddRange(HeaderArgs(item.Request, cookies));
        if (useFallbackClients && IsYoutube(item.Url)) { args.Add("--extractor-args"); args.Add(YoutubeFallbackClients); }
        args.Add("--");
        args.Add(CleanUrl(item.Url));

        var errors = new List<string>();
        long phaseBase = 0, lastDone = 0, lastTotal = 0;
        using var p = new Process { StartInfo = Start(args) };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) lock (errors) { errors.Add(d); if (errors.Count > 40) errors.RemoveAt(0); } };
        if (!p.Start()) throw new InvalidOperationException(L.T("تعذر تشغيل yt-dlp"));
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => Kill(p));

        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
        {
            if (line.StartsWith("[IBDM]", StringComparison.Ordinal))
            {
                var parts = line[6..].Split('|');
                var done = Num64(parts, 0);
                var total = Math.Max(Num64(parts, 1), Num64(parts, 2));
                var speed = parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) ? sp : 0;
                // A new file started (video, then audio): keep counting from where the last one ended.
                if (done < lastDone && lastTotal > 0) phaseBase += lastTotal;
                lastDone = done;
                if (total > 0) lastTotal = total;
                progress(new Progress(phaseBase + done, total > 0 ? phaseBase + total : -1, speed, null));
            }
            else if (line.StartsWith("[Merger]", StringComparison.Ordinal)) progress(new Progress(-1, -1, 0, L.T("جاري دمج الصوت مع الفيديو")));
            else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)) progress(new Progress(-1, -1, 0, L.T("جاري تحويل الصوت")));
            else if (line.StartsWith("[FixupM3u8]", StringComparison.Ordinal) || line.StartsWith("[VideoConvertor]", StringComparison.Ordinal)) progress(new Progress(-1, -1, 0, L.T("جاري المعالجة")));
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string text;
            lock (errors) text = string.Join("\n", errors);
            throw new InvalidOperationException(CleanError(text) ?? L.F("yt-dlp توقف (رمز {0})", p.ExitCode));
        }

        string? final = null;
        try { if (File.Exists(pathFile)) final = File.ReadAllLines(pathFile, Encoding.UTF8).LastOrDefault(l => l.Trim().Length > 0)?.Trim(); }
        catch (Exception) { }
        try { File.Delete(pathFile); } catch (Exception) { }
        if (final is null || !File.Exists(final))
        {
            // Fallback: the newest file in the folder that starts with the chosen name.
            final = new DirectoryInfo(item.Folder).GetFiles(stem + ".*")
                .Where(f => !f.Name.EndsWith(".part") && !f.Name.EndsWith(".ytdl"))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        return final ?? throw new InvalidOperationException(L.T("اكتمل التحميل لكن لم يتم العثور على الملف"));
    }

    public static async Task<string> UpdateAsync()
    {
        var psi = Start(["-U"]);
        using var p = Process.Start(psi)!;
        var o = await p.StandardOutput.ReadToEndAsync();
        var e = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (o + "\n" + e).Trim();
    }

    private static long Num64(string[] parts, int i) =>
        i < parts.Length && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : 0;

    private static string? CleanError(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:")) ?? stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(line)) return null;
        if (line.Contains("Unsupported URL")) return L.T("هذا الرابط لا يحتوي على فيديو يمكن تحميله");
        if (line.Contains("Requested format is not available") || line.Contains("No video formats found"))
            return L.T("يوتيوب لم يعطِ جودات لهذا الفيديو. اضغط «إعادة المحاولة»، وإذا تكرر حدّث محرك الفيديو من الإعدادات ← الإضافة ومحرك الفيديو.");
        if (line.Contains("not a bot") || line.Contains("confirm you"))
            return L.T("يوتيوب طلب التحقق بأنك لست روبوت (بسبب كثرة الطلبات من نفس الإنترنت). انتظر قليلاً ثم أعد المحاولة.");
        if (line.Contains("HTTP Error 403")) return L.T("الموقع رفض التحميل (403) — جرّب مرة ثانية أو حدّث محرك الفيديو من الإعدادات");
        if (line.Contains("Sign in") || line.Contains("login")) return L.T("هذا الفيديو يحتاج تسجيل دخول");
        if (line.Contains("Private video")) return L.T("هذا الفيديو خاص");
        return line.StartsWith("ERROR:") ? line[6..].Trim() : line;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
    }
}
