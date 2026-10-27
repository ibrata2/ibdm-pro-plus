using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using IbdmPro.Core;

namespace IbdmPro.App;

/// <summary>"البحث عن تحديث": asks GitHub for the newest IBDM Pro release, downloads its installer and installs it
/// quietly; the installer closes the app, replaces it and starts it again (unfinished downloads continue).</summary>
public static class Updater
{
    private const string Api = "https://api.github.com/repos/ibrata2/ibdm-pro-plus/releases/latest";
    /// <summary>Where people get IBDM Pro: the Telegram channel. (Updates themselves still come from GitHub, above.)</summary>
    public const string Page = "https://t.me/ibraprogram";

    public sealed record Release(Version Version, string Tag, string SetupUrl, long Size, string Notes);

    public static Version Current => Parse(Program.Version);

    private static Version Parse(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    }

    /// <summary>The latest published release, or null when GitHub cannot be reached.</summary>
    public static async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api);
        req.Headers.TryAddWithoutValidation("User-Agent", "IBDM-Pro/" + Program.Version);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.SendAsync(req, timeout.Token);
        // A new repository has no release until its first successful build publishes one.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(L.F("تعذر الوصول لصفحة التحديثات ({0})", (int)resp.StatusCode));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        string? url = null;
        long size = -1;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                url = a.GetProperty("browser_download_url").GetString();
                size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : -1;
            }
        }
        if (url is null) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new Release(Parse(tag), tag, url, size, notes);
    }

    /// <summary>Quiet check when the app starts: only speaks up (tray message) when there is something new.</summary>
    private static Version? _announced;

    /// <summary>Checks 20 seconds after start, then every 6 hours. When a newer version exists: a Windows notice
    /// (click it to update) and a banner at the top of the main window — once per new version.</summary>
    public static async Task CheckInBackground()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                if (Settings.Current.CheckUpdatesOnStart)
                {
                    var r = await LatestAsync();
                    if (r is not null && r.Version > Current && r.Version != _announced)
                    {
                        _announced = r.Version;
                        IbdmApp.Instance.Dispatcher.Invoke(() =>
                        {
                            var app = IbdmApp.Instance;
                            app.Main.ShowUpdateBadge(r.Version.ToString());
                            app.Tray?.NotifyAction(L.T("يتوفر إصدار جديد من IBDM Pro"), L.F("الإصدار {0} — اضغط هنا للتحديث.", r.Version),
                                () => { app.ShowMain(); ShowWindow(app.Main); });
                        });
                    }
                }
            }
            catch (Exception e) { Log.Write($"update check: {e.Message}"); }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    public static void ShowWindow(Window owner) => new UpdateWindow { Owner = owner.IsVisible ? owner : null }.ShowDialog();

    /// <summary>Downloads the installer next to the other temporary files and reports progress.</summary>
    public static async Task<string> DownloadAsync(Release r, Action<long, long, double> progress, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"IBDM-Pro-Setup-{r.Version}.exe");
        using var req = new HttpRequestMessage(HttpMethod.Get, r.SetupUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "IBDM-Pro/" + Program.Version);
        using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? r.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file + ".part"))
        {
            var buf = new byte[128 * 1024];
            long done = 0;
            var sw = Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress(done, total, done / Math.Max(0.2, sw.Elapsed.TotalSeconds));
            }
            if (total > 0 && done != total) throw new IOException(L.T("انقطع تحميل التحديث، حاول مرة ثانية"));
        }
        File.Move(file + ".part", file, true);
        return file;
    }

    /// <summary>Runs the installer quietly; it closes IBDM Pro, installs, and starts it again.</summary>
    public static void Install(string setup)
    {
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
        IbdmApp.Instance.Quit();
    }
}

public sealed class UpdateWindow : Window
{
    private readonly StackPanel _body = new();
    private readonly CancellationTokenSource _cts = new();

    public UpdateWindow()
    {
        Ui.Setup(this, L.T("التحديثات — IBDM Pro"), 520, 360);
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(24) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Ui.Logo(44));
        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text(L.T("البحث عن أحدث إصدار"), 17, Theme.Ink, bold: true));
        var cur = new StackPanel { Orientation = Orientation.Horizontal };
        cur.Children.Add(Ui.Text(L.T("الإصدار الحالي:"), 12, Theme.Muted));
        var v = Ui.Ltr(Program.Version, 12, Theme.Muted);
        v.Margin = new Thickness(6, 0, 0, 0);
        cur.Children.Add(v);
        titles.Children.Add(cur);
        head.Children.Add(titles);
        root.Children.Add(head);
        root.Children.Add(_body);
        Content = root;
        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) => await Check();
    }

    private void Busy(string text)
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Text(text, 13, Theme.Ink));
        _body.Children.Add(new ProgressBar { Style = Theme.Progress, IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 6) });
    }

    private async Task Check()
    {
        Busy(L.T("جاري البحث عن تحديث…"));
        Updater.Release? r;
        try { r = await Updater.LatestAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            Result(L.T("تعذر البحث عن تحديث"), L.T("تأكد من اتصال الإنترنت وحاول مرة ثانية.\n") + e.Message, Theme.Red, retry: true);
            return;
        }
        if (r is null || r.Version <= Updater.Current)
        {
            Result(L.T("✓ أنت تستخدم أحدث إصدار"), L.F("IBDM Pro {0} هو آخر إصدار متوفر.", Program.Version), Theme.Green, retry: false);
            return;
        }
        Offer(r);
    }

    private void Result(string title, string text, System.Windows.Media.Brush color, bool retry)
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Text(title, 15, color, bold: true));
        var t = Ui.Text(text, 12.5, Theme.Muted, wrap: TextWrapping.Wrap);
        t.Margin = new Thickness(0, 6, 0, 16);
        _body.Children.Add(t);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (retry)
        {
            var again = Ui.Button(L.T("إعادة المحاولة"), "", Theme.Button, async () => await Check());
            again.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(again);
        }
        var close = Ui.Button(L.T("إغلاق"), style: Theme.Button, click: Close);
        close.IsCancel = true;
        row.Children.Add(close);
        _body.Children.Add(row);
    }

    private void Offer(Updater.Release r)
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Text(L.T("يتوفر إصدار جديد!"), 15, Theme.Gold, bold: true));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        line.Children.Add(Ui.Text(L.T("الإصدار الجديد:"), 13, Theme.Ink));
        var nv = Ui.Ltr($"{r.Version}   ({Fmt.Size(r.Size).TrimStart('‎')})", 13, Theme.Ink, bold: true);
        nv.Margin = new Thickness(6, 0, 0, 0);
        line.Children.Add(nv);
        _body.Children.Add(line);

        var active = IbdmApp.Instance.Manager.Items.Count(i => i.IsActive);
        var note = Ui.Text(
            L.T("سيتم تحميل التحديث وتثبيته تلقائياً، ثم يعاد تشغيل البرنامج.") +
            (active > 0 ? L.F("\nالتحميلات الجارية ({0}) تتوقف مؤقتاً وتكمل بعد التحديث من نفس المكان.", active) : ""),
            12.5, Theme.Muted, wrap: TextWrapping.Wrap);
        note.Margin = new Thickness(0, 10, 0, 16);
        _body.Children.Add(note);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var go = Ui.Button(L.T("تحميل وتثبيت التحديث"), "", Theme.PrimaryButton, async () => await Install(r));
        go.IsDefault = true;
        go.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(go);
        var page = Ui.Button(L.T("قناة التليكرام"), "", Theme.Button, () => Ui.Open(Updater.Page));
        page.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(page);
        var later = Ui.Button(L.T("لاحقاً"), style: Theme.Button, click: Close);
        later.IsCancel = true;
        row.Children.Add(later);
        _body.Children.Add(row);
    }

    private async Task Install(Updater.Release r)
    {
        _body.Children.Clear();
        var label = Ui.Text(L.F("جاري تحميل الإصدار {0}…", r.Version), 13, Theme.Ink);
        var bar = new ProgressBar { Style = Theme.Progress, Maximum = 100, Margin = new Thickness(0, 12, 0, 6) };
        var stats = Ui.Ltr("", 12, Theme.Muted);
        _body.Children.Add(label);
        _body.Children.Add(bar);
        _body.Children.Add(stats);
        var cancel = Ui.Button(L.T("إلغاء"), style: Theme.Button, click: Close);
        cancel.Margin = new Thickness(0, 14, 0, 0);
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        _body.Children.Add(cancel);
        try
        {
            var setup = await Updater.DownloadAsync(r, (done, total, speed) => Dispatcher.BeginInvoke(() =>
            {
                if (total > 0) bar.Value = done * 100.0 / total;
                stats.Text = $"{Fmt.Size(done).TrimStart('‎')} / {Fmt.Size(total).TrimStart('‎')}   •   {Fmt.Speed(speed).TrimStart('‎')}";
            }), _cts.Token);
            label.Text = L.T("جاري التثبيت… سيعاد تشغيل البرنامج تلقائياً");
            bar.IsIndeterminate = true;
            await Task.Delay(600);
            Updater.Install(setup);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Result(L.T("تعذر تحميل التحديث"), e.Message, Theme.Red, retry: true);
        }
    }
}


