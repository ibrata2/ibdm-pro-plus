using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using IbdmPro.Core;
using Microsoft.Win32;

namespace IbdmPro.App;

public static class Program
{
    public static string Version { get; } =
        (typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0");

    [STAThread]
    public static int Main(string[] args)
    {
        _ = Settings.Current; // loads the settings, which also sets the language (L.Lang) before any window exists
        if (args.Length > 1 && args[0] == "--snapshot")
        {
            // Render the windows to pictures with sample data, without touching the real settings or list.
            Paths.AppDataOverride = Path.Combine(Path.GetTempPath(), "IBDM Pro snapshot " + Guid.NewGuid().ToString("N")[..6]);
            if (args.Length > 3 && args[2] == "--theme") Settings.Current.Theme = args[3];
            if (args.Length > 5 && args[4] == "--lang") { Settings.Current.Language = args[5]; L.Lang = args[5]; }
            var snap = new IbdmApp(background: true) { SnapshotDir = Path.GetFullPath(args[1]) };
            return snap.Run();
        }
        if (args.Length > 2 && args[0] == "--dump-uia")
        {
            string text = "";
            var t = new Thread(() => text = ExtensionInstaller.Dump(args[1]));
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            File.WriteAllText(args[2], text);
            return 0;
        }
        if (args.Length > 2 && args[0] == "--install-extension")
        {
            // Used by the build: installs the extension into one browser (the app must already be running) and reports.
            var browser = ExtensionInstaller.Installed().FirstOrDefault(b => b.ProcessName.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            var report = new System.Text.StringBuilder();
            if (browser is null) report.Append("browser not found: " + args[1]);
            else
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                DateTime Last()
                {
                    try
                    {
                        var j = hc.GetStringAsync($"http://127.0.0.1:{Settings.Port}/status").GetAwaiter().GetResult();
                        var ago = System.Text.Json.JsonDocument.Parse(j).RootElement.GetProperty("extensionSecondsAgo").GetInt32();
                        return ago < 0 ? DateTime.MinValue : DateTime.Now.AddSeconds(-ago);
                    }
                    catch (Exception) { return DateTime.MinValue; }
                }
                ExtensionInstaller.Result r = null!;
                var t = new Thread(() => r = ExtensionInstaller.Install(browser, Path.GetFullPath(args.Length > 3 ? args[3] : Paths.ExtensionDir),
                    s => report.AppendLine("step: " + s), Last, CancellationToken.None));
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                t.Join();
                report.AppendLine($"ok={r.Ok} confirmed={r.Confirmed} message={r.Message}");
            }
            File.WriteAllText(args[2], report.ToString());
            return 0;
        }
        if (args.Length > 1 && args[0] == "--check-update")
        {
            // Used by the build to prove the update check reads GitHub correctly.
            try
            {
                var r = Updater.LatestAsync().GetAwaiter().GetResult();
                File.WriteAllText(args[1], r is null ? $"current={Program.Version} latest=none newer=false" : $"current={Program.Version} latest={r.Version} newer={r.Version > Updater.Current} size={r.Size} url={r.SetupUrl}");
            }
            catch (Exception e) { File.WriteAllText(args[1], "error: " + e.Message); }
            return 0;
        }
        using var single = new Mutex(true, "Local\\IBDMPro.SingleInstance", out var first);
        if (!first && args.Contains("--restart"))
        {
            // Restarting (new colours): wait for the old instance to finish closing.
            try { first = single.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            // Already running (maybe hidden in the tray): ask it to show its window and leave.
            if (!args.Contains("--background")) LocalServer.TryShowRunning();
            return 0;
        }
        var app = new IbdmApp(args.Contains("--background")) { SetupExtensions = args.Contains("--setup-extensions") };
        return app.Run();
    }
}

/// <summary>The app: owns the download manager, the local server for the browser extension, the tray icon and the main window.</summary>
public sealed class IbdmApp : Application
{
    public static IbdmApp Instance { get; private set; } = null!;
    public DownloadManager Manager { get; private set; } = null!;
    public LocalServer? Server { get; private set; }
    public Tray? Tray { get; private set; }
    public MainWindow Main { get; private set; } = null!;
    private readonly bool _background;
    public bool Quitting { get; private set; }
    public string? SnapshotDir { get; init; }
    public bool SetupExtensions { get; init; }

    public IbdmApp(bool background)
    {
        Instance = this;
        _background = background;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"unhandled: {e.Exception}");
            e.Handled = true;
            try { Ui.Message(null, L.T("حدث خطأ"), e.Exception.Message); } catch (Exception) { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(L.En ? "en-US" : "ar")));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Write($"IBDM Pro {Program.Version} started");
        var settings = Settings.Current;
        if (SnapshotDir is not null)
        {
            Manager = new DownloadManager();
            Main = new MainWindow(Manager);
            Snapshots.Run(this, SnapshotDir);
            Manager.Dispose();
            Shutdown();
            return;
        }
        AutoStart.Apply(settings.StartWithWindows);

        Manager = new DownloadManager();
        Manager.Limiter.BytesPerSecond = settings.SpeedLimitKBps * 1024L;
        Manager.ItemCompleted += item => Dispatcher.BeginInvoke(() =>
        {
            if (Settings.Current.NotifyOnComplete) Tray?.Notify(L.T("اكتمل التحميل"), item.FileName, item.FullPath);
        });
        Manager.UserStarted += item => Dispatcher.BeginInvoke(() =>
        {
            if (Settings.Current.ShowProgressWindow) ProgressWindow.Open(item, Manager);
        });
        Manager.ItemFailed += item => Dispatcher.BeginInvoke(() => Tray?.Notify(L.T("فشل التحميل"), $"{item.FileName}\n{item.Error}", null));

        Main = new MainWindow(Manager);
        Tray = new Tray(this);

        try
        {
            Server = new LocalServer { Version = Program.Version };
            Server.Captured += req => Dispatcher.BeginInvoke(() => Capture.Handle(req));
            Server.ShowRequested += () => Dispatcher.BeginInvoke(ShowMain);
            Server.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"server: {ex.Message}");
            Server = null;
        }

        Manager.Begin();
        if (!_background) ShowMain();
        WatchWindowsTheme();
        _ = Updater.CheckInBackground();

        if (SetupExtensions)
        {
            // The installer's "install the browser extension" option.
            settings.HasSeenWelcome = true;
            settings.Save();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () => await ExtensionInstaller.RunWithUi(Main, ExtensionInstaller.Installed()));
        }
        else if (!settings.HasSeenWelcome)
        {
            settings.HasSeenWelcome = true;
            settings.Save();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ExtensionGuide.Show(Main));
        }
        if (Server is null) Ui.Message(Main, L.T("تنبيه"), L.F("المنفذ {0} مستخدم من برنامج آخر، لذلك لن تصل التحميلات من المتصفح. أغلق البرنامج الآخر ثم أعد تشغيل IBDM Pro.", Settings.Port));
    }

    public void ShowMain() => Ui.ForceFront(Main);

    /// <summary>Closes and starts again (used after changing between light and dark).</summary>
    public void Restart()
    {
        try { if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe, "--restart"); }
        catch (Exception e) { Log.Write($"restart: {e.Message}"); return; }
        Quit();
    }

    /// <summary>Windows switched between light and dark: offer to restart in the new colours.</summary>
    private void WatchWindowsTheme()
    {
        var asked = false;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || asked || Settings.Current.Theme != "auto") return;
            if (Theme.WindowsIsDark() == Theme.IsDark) return;
            asked = true;
            Dispatcher.BeginInvoke(() =>
            {
                var choice = Ui.Ask(Main.IsVisible ? Main : null, L.T("تغيّر مظهر Windows"),
                    L.F("تم تغيير Windows إلى الوضع {0}. إعادة تشغيل IBDM Pro لتطبيق الألوان الجديدة؟ (التحميلات تكمل بعد التشغيل)", (Theme.WindowsIsDark() ? L.T("الليلي") : L.T("النهاري"))),
                    L.T("إعادة التشغيل الآن"), L.T("لاحقاً"));
                if (choice == 0) Restart();
            });
        };
    }

    public void Quit()
    {
        Quitting = true;
        Manager.Dispose();
        Server?.Dispose();
        Tray?.Dispose();
        Shutdown();
    }
}

/// <summary>"Start with Windows": a Run entry for the signed-in user, started hidden in the tray.</summary>
public static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true) ?? Registry.CurrentUser.CreateSubKey(Key);
            if (on && Environment.ProcessPath is { } exe) k.SetValue("IBDM Pro", $"\"{exe}\" --background");
            else k.DeleteValue("IBDM Pro", false);
        }
        catch (Exception e) { Log.Write($"startup: {e.Message}"); }
    }
}


