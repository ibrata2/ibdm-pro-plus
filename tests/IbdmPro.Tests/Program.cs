using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using IbdmPro.Core;

// Real end-to-end tests of the engine against tests/server.js.
var port = int.Parse(args[0]);
var data = args[1];
var baseUrl = $"http://127.0.0.1:{port}";
var work = Path.Combine(Path.GetTempPath(), "ibdm-test-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(work);
Paths.AppDataOverride = Path.Combine(work, "appdata");
var settings = new Settings { DownloadRoot = Path.Combine(work, "IBDM Pro"), Connections = 8, MaxParallel = 3 };
Settings.Reset(settings);
var failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

string Sha(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }

async Task<bool> WaitFor(Func<bool> cond, int seconds)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds) { if (cond()) return true; await Task.Delay(50); }
    return cond();
}

// ---------- names ----------
Check(FileNames.Clean("a|b:c?.mp4") == "a b c .mp4" || FileNames.Clean("a|b:c?.mp4") == "a b c.mp4", $"Clean strips illegal characters → '{FileNames.Clean("a|b:c?.mp4")}'");
Check(FileNames.Clean("CON.txt") == "_CON.txt", "Clean avoids reserved names");
var taken = new HashSet<string> { Path.Combine("d", "x.zip"), Path.Combine("d", "x (2).zip") };
Check(FileNames.Unique(Path.Combine("d", "x.zip"), taken.Contains) == Path.Combine("d", "x (3).zip"), "Unique numbers duplicates (2), (3)…");
Check(settings.CategoryFor("movie.MKV").Name == "فيديو" && settings.CategoryFor("a.rar").Name == "مضغوطات" && settings.CategoryFor("setup.exe").Name == "برامج"
      && settings.CategoryFor("song.mp3").Name == "صوتيات" && settings.CategoryFor("x.unknown").Name == Settings.OtherCategory, "files sorted into the right category");
Check(settings.ShouldCapture("a.zip", "https://site.com/a.zip", 5000) && !settings.ShouldCapture("a.html", "https://site.com/a.html", 5000)
      && !settings.ShouldCapture("a.zip", "https://mail.google.com/x", 5000), "capture rules: extensions and excluded sites");

Check(YtDlp.CleanUrl("https://www.youtube.com/watch?v=u8AjfvHBCSI&list=RDu8AjfvHBCSI&start_radio=1") == "https://www.youtube.com/watch?v=u8AjfvHBCSI"
      && YtDlp.CleanUrl("https://youtu.be/abc?si=x") == "https://youtu.be/abc" && YtDlp.CleanUrl("https://site.com/a?b=1") == "https://site.com/a?b=1",
      "YouTube mix/playlist links reduced to the single video");

// ---------- English ----------
L.Lang = "en";
Check(L.T("حفظ") == "Save" && L.F("{0} تحميلات", 3) == "3 downloads" && L.T("نص بدون ترجمة") == "نص بدون ترجمة" && L.T("فيديو") == "Video",
      $"English texts ({L.Count} translations)");
L.Lang = "ar";
Check(L.T("حفظ") == "حفظ" && L.F("{0} تحميلات", 3) == "3 تحميلات", "Arabic stays Arabic");

// ---------- probing ----------
var info = new RequestInfo();
var p1 = await Http.ProbeAsync($"{baseUrl}/file.bin", info);
Check(p1.Error is null && p1.Size == new FileInfo(Path.Combine(data, "file.bin")).Length && p1.Resumable && p1.FileName == "file.bin", $"probe: size {Fmt.Size(p1.Size)}, resumable={p1.Resumable}, name={p1.FileName}");
var p2 = await Http.ProbeAsync($"{baseUrl}/dl", info);
Check(p2.FileName == "ملف تجربة.zip", $"probe: Arabic name from Content-Disposition → '{p2.FileName}'");
var p3 = await Http.ProbeAsync($"{baseUrl}/redirect", info);
Check(p3.FinalUrl.EndsWith("/file.bin") && p3.FileName == "file.bin" && p3.Resumable, "probe follows redirects");
var p4 = await Http.ProbeAsync($"{baseUrl}/noranges.bin", info);
Check(!p4.Resumable && p4.Size > 0, "probe detects a server without ranges");
var p5 = await Http.ProbeAsync($"{baseUrl}/missing", info);
Check(p5.Error is not null, $"probe reports errors: {p5.Error}");
Check(Http.NameFrom(new ContentDispositionHeaderValue("attachment") { FileName = "\"" + Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("أغنية.mp3")) + "\"" }, "http://x/y", "http://x/y", null) == "أغنية.mp3",
      "UTF-8 name sent as Latin-1 is repaired");

// ---------- segmented download with pause / resume ----------
var manager = new DownloadManager(new Store(Path.Combine(work, "downloads.json")), () => settings);
DownloadItem MakeFile(ProbeResult p, string? cookies = null)
{
    var cat = settings.CategoryFor(p.FileName);
    return new DownloadItem
    {
        Kind = DownloadKind.File, Url = p.FinalUrl, FileName = p.FileName, TotalBytes = p.Size, Resumable = p.Resumable,
        Category = cat.Name, Folder = settings.FolderFor(cat), Request = new RequestInfo { Cookies = cookies },
    };
}

var big = MakeFile(p1);
var sw = Stopwatch.StartNew();
manager.Add(big, start: true);
await WaitFor(() => big.Percent > 30, 30);
var segsWhileRunning = big.Segments.Count;
manager.Pause(big);
await WaitFor(() => big.Status == DownloadStatus.Paused, 10);
var pausedAt = big.Downloaded;
Check(big.Status == DownloadStatus.Paused && pausedAt > 0 && pausedAt < big.TotalBytes && File.Exists(big.PartPath), $"paused at {big.Percent:0}% with {segsWhileRunning} connections, part file kept");
await Task.Delay(500);
Check(big.Downloaded == pausedAt, "nothing downloads while paused");

// resume with fewer connections than it started with
settings.Connections = 3;
manager.Resume(big);
await WaitFor(() => big.Status is DownloadStatus.Completed or DownloadStatus.Error, 120);
Check(big.Status == DownloadStatus.Completed, $"resumed and completed ({big.Error}) in {sw.Elapsed.TotalSeconds:0.0}s");
Check(File.Exists(big.FullPath) && !File.Exists(big.PartPath), $"saved to {big.FullPath}");
Check(File.Exists(big.FullPath) && Sha(big.FullPath) == Sha(Path.Combine(data, "file.bin")), "SHA-256 identical to the original");
Check(Path.GetDirectoryName(big.FullPath)!.EndsWith(Path.Combine("IBDM Pro", Settings.OtherCategory)), "saved inside IBDM Pro\\<category>");

// fast parallel download, check the connections split work
settings.Connections = 8;
var big2 = MakeFile(p1);
sw.Restart();
var maxSegs = 0;
manager.Add(big2, start: true);
while (big2.Status is not (DownloadStatus.Completed or DownloadStatus.Error) && sw.Elapsed.TotalSeconds < 60)
{
    lock (big2.Gate) maxSegs = Math.Max(maxSegs, big2.Segments.Count);
    await Task.Delay(20);
}
var oneConn = p1.Size / 3.0 / 1024 / 1024;
Check(big2.Status == DownloadStatus.Completed && Sha(big2.FullPath) == Sha(Path.Combine(data, "file.bin")),
      $"8 connections: {sw.Elapsed.TotalSeconds:0.0}s (one connection would take ~{oneConn:0}s), up to {maxSegs} ranges after splitting");
Check(Path.GetFileName(big2.FullPath) == "file (2).bin", $"second copy got a new name: {Path.GetFileName(big2.FullPath)}");

// server without ranges
var nr = MakeFile(p4);
manager.Add(nr, start: true);
await WaitFor(() => nr.Status is DownloadStatus.Completed or DownloadStatus.Error, 60);
Check(nr.Status == DownloadStatus.Completed && Sha(nr.FullPath) == Sha(Path.Combine(data, "small.bin")), "server without ranges: one connection, identical file");

// cookies from the browser
var withCookie = MakeFile(await Http.ProbeAsync($"{baseUrl}/needcookie.bin", new RequestInfo { Cookies = "session=abc" }), "session=abc");
manager.Add(withCookie, start: true);
await WaitFor(() => withCookie.Status is DownloadStatus.Completed or DownloadStatus.Error, 30);
Check(withCookie.Status == DownloadStatus.Completed, "browser cookies are sent with the download");
var noCookie = await Http.ProbeAsync($"{baseUrl}/needcookie.bin", new RequestInfo());
Check(noCookie.Error is not null, "without the cookie the server refuses (as expected)");

// cancelling a running download removes it from the list (and it does not come back)
var toCancel = MakeFile(p1);
manager.Add(toCancel, start: true);
await WaitFor(() => toCancel.Downloaded > 0, 20);
await manager.RemoveAsync(toCancel, deleteFile: false);
await Task.Delay(1500);
Check(!manager.Items.Contains(toCancel) && !File.Exists(toCancel.PartPath), "cancel: running download removed from the list, part file deleted");
Check(!new Store(Path.Combine(work, "downloads.json")).Items.Any(i => i.Id == toCancel.Id), "cancel: not in the saved list either");

// cancel is immediate: gone from the list at once, and "resume" can't bring it back
var quick = MakeFile(p1);
manager.Add(quick, start: true);
await WaitFor(() => quick.Downloaded > 0, 20);
var removing = manager.RemoveAsync(quick, deleteFile: false);
var goneAtOnce = !manager.Items.Contains(quick);
manager.Resume(quick);
await removing;
await Task.Delay(500);
Check(goneAtOnce && !manager.IsRunning(quick) && !manager.Items.Contains(quick) && !File.Exists(quick.PartPath), "cancel: removed at once, cannot be resumed");

// cookies go to yt-dlp as a cookies.txt file for the site
using (var cf = YtDlp.CookieFile.For(new RequestInfo { Cookies = "sessionid=abc; csrftoken=x=y" }, "https://www.instagram.com/reel/1/"))
{
    var text = File.ReadAllText(cf!.Path);
    Check(text.StartsWith("# Netscape HTTP Cookie File") && text.Contains(".instagram.com\tTRUE\t/\tTRUE\t") && text.Contains("\tsessionid\tabc\n") && text.Contains("\tcsrftoken\tx=y\n"), "cookies.txt for yt-dlp");
}

// list survives a restart, with progress
manager.Save();
var reloaded = new Store(Path.Combine(work, "downloads.json"));
Check(reloaded.Items.Count == manager.Items.Count && reloaded.Items.Any(i => i.FileName == "file.bin" && i.Status == DownloadStatus.Completed), "download list saved and reloaded");

// ---------- media through yt-dlp ----------
try
{
    var media = await YtDlp.GetInfoAsync($"{baseUrl}/page.html", new RequestInfo());
    Console.WriteLine($"      media: '{media.Title}' {Fmt.Duration(media.Duration)} options: " + string.Join(" | ", media.Options.Select(o => $"{o.Label} {o.SizeText}")));
    Check(media.Options.Any(o => !o.AudioOnly) && media.Options.Any(o => o.AudioOnly), "yt-dlp lists video qualities and audio-only");
    Check(media.Title.Contains("تجربة") || media.Title.Length > 0, $"title read: {media.Title}");

    var v = media.Options.First(o => !o.AudioOnly);
    var cat = settings.CategoryNamed("فيديو")!;
    var vid = new DownloadItem { Kind = DownloadKind.Media, Url = media.Url, FileName = FileNames.Clean(media.Title) + "." + v.Ext, FormatSpec = v.FormatSpec, TotalBytes = v.Size, Folder = settings.FolderFor(cat), Category = cat.Name };
    manager.Add(vid, start: true);
    await WaitFor(() => vid.Status is DownloadStatus.Completed or DownloadStatus.Error, 60);
    Check(vid.Status == DownloadStatus.Completed && File.Exists(vid.FullPath), $"video downloaded: {vid.FullPath} ({vid.Error})");

    var a = media.Options.First(o => o.AudioOnly && o.AudioFormat == "mp3");
    var acat = settings.CategoryNamed("صوتيات")!;
    var aud = new DownloadItem { Kind = DownloadKind.Media, Url = media.Url, FileName = FileNames.Clean(media.Title) + ".mp3", FormatSpec = a.FormatSpec, AudioOnly = true, AudioFormat = "mp3", Folder = settings.FolderFor(acat), Category = acat.Name };
    manager.Add(aud, start: true);
    await WaitFor(() => aud.Status is DownloadStatus.Completed or DownloadStatus.Error, 60);
    Check(aud.Status == DownloadStatus.Completed && aud.FullPath.EndsWith(".mp3") && File.Exists(aud.FullPath), $"audio only (mp3): {aud.FullPath} ({aud.Error})");
}
catch (Exception e) { Check(false, "yt-dlp: " + e.Message); }

// cancelling a running video download (yt-dlp) removes it too
{
    var vcat = settings.CategoryNamed("فيديو")!;
    var slow = new DownloadItem { Kind = DownloadKind.Media, Url = $"{baseUrl}/big.mp4", FileName = "big.mp4", FormatSpec = "best", Folder = settings.FolderFor(vcat), Category = vcat.Name };
    manager.Add(slow, start: true);
    await WaitFor(() => slow.Downloaded > 0, 30);
    var gotData = slow.Downloaded > 0;
    await manager.RemoveAsync(slow, deleteFile: false);
    await Task.Delay(2000);
    var leftovers = Directory.GetFiles(slow.Folder, "big.*");
    Check(gotData && !manager.Items.Contains(slow) && leftovers.Length == 0, $"cancel video: removed from the list, leftovers: {string.Join(",", leftovers.Select(Path.GetFileName))}");
}

// parse a real-looking YouTube answer
var yt = YtDlp.Parse(File.ReadAllText(Path.Combine(data, "yt.json")), "https://youtube.com/watch?v=x");
Console.WriteLine("      youtube-like: " + string.Join(" | ", yt.Options.Select(o => $"{o.Label} {o.SizeText} [{o.FormatSpec}]")));
var o1080 = yt.Options.FirstOrDefault(o => o.Height == 1080);
Check(o1080 is not null && o1080.FormatSpec.StartsWith("137+140") && o1080.Size == 100_000_000 + 5_000_000 && o1080.Ext == "mp4", "1080p picks H.264/MP4 + M4A audio, exact size = video + audio");
Check(yt.Options.First().Height == 2160 && yt.Options.Count(o => o.AudioOnly) == 2, "highest quality first, audio-only choices included");

// ---------- the local server the extension talks to ----------
using var server = new LocalServer(port + 1) { Version = "9.9.9" };
CaptureRequest? got = null;
server.Captured += r => got = r;
server.Start();
using var hc = new HttpClient();
var ping = await hc.GetStringAsync($"http://127.0.0.1:{port + 1}/ping");
Check(ping.Contains("\"app\":\"IBDM Pro\"") && ping.Contains("9.9.9") && ping.Contains("\"zip\""), "extension ping returns version and capture list");
async Task<int> Post(string path, string? origin, bool marked)
{
    var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port + 1}{path}")
    { Content = new StringContent("{\"kind\":\"file\",\"url\":\"https://x.com/a.zip\",\"fileName\":\"a.zip\",\"size\":123}", Encoding.UTF8, "application/json") };
    if (origin is not null) req.Headers.Add("Origin", origin);
    if (marked) req.Headers.Add("X-IBDM-Pro", "1");
    return (int)(await hc.SendAsync(req)).StatusCode;
}
Check(await Post("/add", "https://evil.example", true) == 403 && got is null, "a normal website is refused");
Check(await Post("/add", "chrome-extension://abc", false) == 404 && got is null, "a request without the extension header is refused");
Check(await Post("/add", "chrome-extension://abc", true) == 200 && await WaitFor(() => got?.FileName == "a.zip" && got.Size == 123, 3), "the extension's download reaches the app");

manager.Dispose();
Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
try { Directory.Delete(work, true); } catch (Exception) { }
return failures == 0 ? 0 : 1;
