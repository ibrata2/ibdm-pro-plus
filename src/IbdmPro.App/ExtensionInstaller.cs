using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using IbdmPro.Core;
using Microsoft.Win32;

namespace IbdmPro.App;

/// <summary>A Chromium browser found on this computer.</summary>
public sealed record BrowserInfo(string Name, string Exe, string ExtensionsPage, string ProcessName);

/// <summary>Installs the IBDM Pro extension into a browser by doing what a person would do — in front of them:
/// opens the browser's extensions page, turns on developer mode, presses "Load unpacked", picks the extension folder,
/// then waits for the extension to call the app to confirm it really runs. Browsers do not allow programs to install
/// extensions silently from outside their stores, so this is the closest thing to automatic.</summary>
public static class ExtensionInstaller
{
    // ---------- which browsers are installed ----------

    private static readonly (string Name, string Exe, string Page)[] Known =
    [
        ("Google Chrome", "chrome.exe", "chrome://extensions"),
        ("Microsoft Edge", "msedge.exe", "edge://extensions"),
        ("Brave", "brave.exe", "brave://extensions"),
        ("Vivaldi", "vivaldi.exe", "vivaldi://extensions"),
        ("Opera", "opera.exe", "opera://extensions"),
        ("Yandex", "browser.exe", "browser://extensions"),
    ];

    /// <summary>Firefox, when installed (its extension is loaded differently, see the guide).</summary>
    public static string? Firefox()
    {
        var p = FindExe("firefox.exe", "Firefox");
        if (p is not null) return p;
        foreach (var g in new[] { @"%ProgramFiles%\Mozilla Firefox\firefox.exe", @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe" })
        {
            var e = Environment.ExpandEnvironmentVariables(g);
            if (File.Exists(e)) return e;
        }
        return null;
    }

    public static List<BrowserInfo> Installed()
    {
        var list = new List<BrowserInfo>();
        foreach (var (name, exe, page) in Known)
        {
            var path = FindExe(exe, name);
            if (path is not null) list.Add(new BrowserInfo(name, path, page, Path.GetFileNameWithoutExtension(exe)));
        }
        return list;
    }

    private static string? FindExe(string exe, string name)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (k?.GetValue("") is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch (Exception) { }
        }
        string L(string s) => Environment.ExpandEnvironmentVariables(s);
        var guesses = name switch
        {
            "Google Chrome" => new[] { L(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"), L(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"), L(@"%LocalAppData%\Google\Chrome\Application\chrome.exe") },
            "Microsoft Edge" => [L(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"), L(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")],
            "Brave" => [L(@"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe"), L(@"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe")],
            "Vivaldi" => [L(@"%LocalAppData%\Vivaldi\Application\vivaldi.exe"), L(@"%ProgramFiles%\Vivaldi\Application\vivaldi.exe")],
            "Opera" => [L(@"%LocalAppData%\Programs\Opera\opera.exe"), L(@"%LocalAppData%\Programs\Opera\launcher.exe")],
            "Yandex" => [L(@"%LocalAppData%\Yandex\YandexBrowser\Application\browser.exe")],
            _ => [],
        };
        return guesses.FirstOrDefault(File.Exists);
    }

    // ---------- the install itself ----------

    public sealed class Result
    {
        public bool Ok { get; set; }
        public bool Confirmed { get; set; }
        public string Message { get; set; } = "";
        public List<string> Steps { get; } = [];
    }

    /// <summary>Runs on a background thread (UI Automation must not run on the app's UI thread).</summary>
    public static Result Install(BrowserInfo browser, string extensionDir, Action<string> step, Func<DateTime> lastContact, CancellationToken ct)
    {
        var r = new Result();
        void Say(string s) { r.Steps.Add(s); step(s); Log.Write($"extension install ({browser.Name}): {s}"); }
        try
        {
            if (!File.Exists(Path.Combine(extensionDir, "manifest.json"))) throw new InvalidOperationException(L.T("مجلد الإضافة غير موجود: ") + extensionDir);
            var started = DateTime.Now;

            Say(L.F("فتح {0} على صفحة الإضافات…", browser.Name));
            var before = BrowserWindows(browser.ProcessName).Select(w => w.Handle).ToHashSet();
            var launched = DateTime.Now;
            Process.Start(new ProcessStartInfo(browser.Exe) { UseShellExecute = false, ArgumentList = { "--no-first-run", "--no-default-browser-check", "--new-window", browser.ExtensionsPage } });

            // The extensions page: a browser window in which the developer-mode switch can be found.
            // Browsers often ignore chrome://… addresses given on the command line and open a new tab instead,
            // so after a few seconds the address is typed into the address bar.
            AutomationElement? window = null, devToggle = null;
            var until = DateTime.Now.AddSeconds(45);
            var navigatedAt = DateTime.MinValue;
            var maximized = false;
            var sideMenu = false;
            var windowHandle = IntPtr.Zero;
            var tries = 0;
            while (DateTime.Now < until && devToggle is null)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(700);
                var wins = BrowserWindows(browser.ProcessName).OrderByDescending(w => !before.Contains(w.Handle)).ToList();
                if (wins.Count > 0 && !maximized && (DateTime.Now - launched).TotalSeconds > 2)
                {
                    ShowWindow(wins[0].Handle, 3); // maximized: the extensions page shows its whole toolbar
                    maximized = true;
                    Thread.Sleep(600);
                }
                foreach (var w in wins)
                {
                    var el = Element(w.Handle);
                    if (el is null) continue;
                    var t = FindToggle(el);
                    // Edge hides developer mode inside the ☰ side menu when the window is narrow.
                    if (t is null && OpenSideMenu(el)) { Thread.Sleep(900); t = FindToggle(el); sideMenu = t is not null; }
                    if (t is not null) { window = el; devToggle = t; windowHandle = w.Handle; break; }
                }
                if (devToggle is null && wins.Count > 0 && (DateTime.Now - navigatedAt).TotalSeconds > 8 && tries < 3
                    && DateTime.Now > (navigatedAt == DateTime.MinValue ? DateTime.Now.AddSeconds(-1) : navigatedAt))
                {
                    if (tries == 0) Thread.Sleep(2500); // let the window finish opening
                    if (TypeAddress(wins[0].Handle, browser.ExtensionsPage)) Say(L.T("كتابة عنوان صفحة الإضافات…"));
                    navigatedAt = DateTime.Now;
                    tries++;
                }
            }
            if (window is null || devToggle is null) throw new InvalidOperationException(L.T("لم أجد صفحة الإضافات أو زر «وضع المطوّر» في المتصفح."));
            try
            {
                if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var wp))
                    ((WindowPattern)wp).SetWindowVisualState(WindowVisualState.Maximized);
            }
            catch (Exception) { }

            Say(L.T("تفعيل «وضع المطوّر»…"));
            if (devToggle.TryGetCurrentPattern(TogglePattern.Pattern, out var tpo) && tpo is TogglePattern tp)
            {
                if (tp.Current.ToggleState != ToggleState.On) { tp.Toggle(); Thread.Sleep(900); }
            }
            else if (FindLoadUnpacked(window) is null && devToggle.TryGetCurrentPattern(InvokePattern.Pattern, out var ipo))
            {
                // Brave shows the switch as a clickable item without an on/off state: it is off while
                // "Load unpacked" is hidden, so click it once.
                ((InvokePattern)ipo).Invoke();
                Thread.Sleep(900);
                // Invoking the group does not always flip the switch: focus it and press Space, then click it for real.
                if (FindLoadUnpacked(window) is null) PressSpace(windowHandle, devToggle);
                if (FindLoadUnpacked(window) is null) ClickOn(windowHandle, devToggle);
            }
            if (sideMenu)
            {
                // Close Edge's side menu again: while it is open the buttons behind it cannot be pressed.
                SetForegroundWindow(windowHandle);
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("{ESC}");
                Thread.Sleep(800);
            }

            Say(L.T("الضغط على «تحميل إضافة غير مُجمَّعة»…"));
            AutomationElement? load = null;
            until = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < until && load is null)
            {
                ct.ThrowIfCancellationRequested();
                load = FindLoadUnpacked(window);
                if (load is null) Thread.Sleep(500);
            }
            if (load is null) throw new InvalidOperationException(L.T("لم أجد زر «تحميل إضافة غير مُجمَّعة / Load unpacked»."));
            var dialogsBefore = Dialogs(browser.ProcessName).ToHashSet();
            // Invoke blocks while the modal folder dialog is open on some versions, so press it from another thread.
            var press = new Thread(() =>
            {
                try { ((InvokePattern)load.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }
                catch (Exception e) { Log.Write($"invoke load unpacked: {e.Message}"); }
            }) { IsBackground = true };
            press.SetApartmentState(ApartmentState.MTA);
            press.Start();

            Say(L.T("اختيار مجلد الإضافة…"));
            IntPtr dlg = IntPtr.Zero;
            until = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < until && dlg == IntPtr.Zero)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(400);
                dlg = Dialogs(browser.ProcessName).FirstOrDefault(h => !dialogsBefore.Contains(h));
            }
            if (dlg == IntPtr.Zero) throw new InvalidOperationException(L.T("لم تظهر نافذة اختيار المجلد."));
            ChooseFolder(dlg, extensionDir);

            Say(L.T("انتظار تشغيل الإضافة والتأكد من اتصالها بالبرنامج…"));
            until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until)
            {
                ct.ThrowIfCancellationRequested();
                if (lastContact() >= started) { r.Confirmed = true; break; }
                // The browser may show an error in the folder dialog or on the page; stop waiting if the dialog is still open.
                if (IsWindow(dlg) && IsWindowVisible(dlg) && DateTime.Now > started.AddSeconds(25)) break;
                Thread.Sleep(500);
            }
            r.Ok = true;
            r.Message = r.Confirmed
                ? L.F("تم تنصيب إضافة IBDM Pro على {0} وهي متصلة بالبرنامج ✓", browser.Name)
                : L.F("تم تحميل الإضافة في {0}. إذا ما ظهرت أيقونة IBDM Pro، أعد تشغيل المتصفح.", browser.Name);
            Say(r.Confirmed ? L.T("✓ تم") : L.T("تم (بدون تأكيد اتصال)"));
        }
        catch (OperationCanceledException) { r.Message = L.T("تم الإلغاء"); }
        catch (Exception e)
        {
            r.Message = e.Message;
            Log.Write($"extension install ({browser.Name}) failed: {e}");
        }
        return r;
    }

    /// <summary>On Edge's extensions page with a narrow window, opens the ☰ menu that holds "Developer mode".</summary>
    private static bool OpenSideMenu(AutomationElement window)
    {
        try
        {
            var url = "";
            foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
            {
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) { url = ((ValuePattern)vp).Current.Value ?? ""; break; }
            }
            if (!url.Contains("extensions")) return false;
            foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true)))
            {
                var n = Norm(e.Current.Name);
                var id = Norm(e.Current.AutomationId);
                if (n.Contains("menu") || n.Contains("قائمه") || n.Contains("navigation") || n.Contains("تنقل") || id.Contains("menu") || id.Contains("hamburger"))
                {
                    var r = e.Current.BoundingRectangle;
                    var wr = window.Current.BoundingRectangle;
                    // Only the small button next to the page's title — not the tab strip or toolbar menus above it.
                    if (r.Top < wr.Top + 90 || r.Top > wr.Top + 280 || r.Width > 80 || r.Left > wr.Left + 200) continue;
                    if (n.Contains("settings and more") || n.Contains("الاعدادات والمزيد")) continue;
                    ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    /// <summary>Puts an address in the browser's address bar and opens it.</summary>
    private static bool TypeAddress(IntPtr window, string address)
    {
        try
        {
            var el = Element(window);
            if (el is null) return false;
            var edits = el.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
            AutomationElement? bar = null;
            foreach (AutomationElement e in edits)
            {
                var n = Norm(e.Current.Name);
                if (n.Contains("address") || n.Contains("عنوان") || n.Contains("search") || n.Contains("بحث") || n.Contains("url")) { bar = e; break; }
            }
            bar ??= edits.Count > 0 ? edits[0] : null;
            SetForegroundWindow(window);
            Thread.Sleep(200);
            if (bar is not null)
            {
                try { bar.SetFocus(); } catch (Exception) { }
                ((ValuePattern)bar.GetCurrentPattern(ValuePattern.Pattern)).SetValue(address);
            }
            else
            {
                System.Windows.Forms.SendKeys.SendWait("^l");
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait(address);
            }
            Thread.Sleep(250);
            SetForegroundWindow(window);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"type address: {e.Message}");
            return false;
        }
    }

    // ---------- finding things on the extensions page ----------

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (c is >= 'ً' and <= 'ٟ' or 'ـ' or 'ٰ') continue; // Arabic diacritics and tatweel
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'أ' or 'إ' or 'آ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', _ => char.ToLowerInvariant(c) });
        }
        return sb.ToString();
    }

    private static readonly string[] DevNames =
    [
        "developer mode", "وضع المطور", "وضع مطور", "مطور البرامج", "mode developpeur", "entwicklermodus", "modo de desarrollador",
        "modo desenvolvedor", "gelistirici modu", "режим разработчика", "حالت توسعه دهنده",
    ];

    private static readonly string[] LoadNames =
    [
        "load unpacked", "unpacked", "غير مجمعه", "غير مجمع", "غير مضغوط", "غير محزم", "غير المضغوط", "charger l'extension non empaquetee",
        "entpackte erweiterung laden", "cargar descomprimida", "carregar sem compactacao", "paketlenmemis", "загрузить распакованное",
    ];

    private static AutomationElement? FindToggle(AutomationElement window)
    {
        try
        {
            var cond = new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true);
            var all = window.FindAll(TreeScope.Descendants, cond);
            foreach (AutomationElement e in all)
            {
                var id = Norm(e.Current.AutomationId);
                var name = Norm(e.Current.Name);
                if (id.Contains("devmode") || id.Contains("developer")) return e;
                if (DevNames.Any(n => name.Contains(Norm(n)))) return e;
            }
            // Brave: the switch (id "devMode") is exposed as a clickable group, not as an on/off switch.
            var inv = new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true);
            foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, inv))
            {
                var id = Norm(e.Current.AutomationId);
                if (id.Contains("devmode")) return e;
            }
        }
        catch (Exception) { }
        return null;
    }

    private static AutomationElement? FindLoadUnpacked(AutomationElement window)
    {
        try
        {
            var cond = new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true);
            foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, cond))
            {
                var id = Norm(e.Current.AutomationId);
                var name = Norm(e.Current.Name);
                if (id.Contains("loadunpacked") || id.Contains("load-unpacked")) return e;
                if (LoadNames.Any(n => name.Contains(Norm(n)))) return e;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Types the folder into the Windows "Select folder" dialog and confirms it.</summary>
    private static void ChooseFolder(IntPtr dialog, string folder)
    {
        var dlg = Element(dialog) ?? throw new InvalidOperationException(L.T("تعذر قراءة نافذة اختيار المجلد."));
        AutomationElement? edit = null;
        var until = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < until && edit is null)
        {
            edit = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
                       new PropertyCondition(AutomationElement.AutomationIdProperty, "1152"),
                       new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                   ?? dlg.FindFirst(TreeScope.Descendants, new AndCondition(
                       new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                       new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
            if (edit is null) Thread.Sleep(300);
        }
        if (edit is null) throw new InvalidOperationException(L.T("لم أجد خانة اسم المجلد في نافذة الاختيار."));
        ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(folder);
        Thread.Sleep(300);
        var ok = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        if (ok is not null && ok.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) ((InvokePattern)ip).Invoke();
        else
        {
            SetForegroundWindow(dialog);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        }
        // Some versions need a second confirmation after the folder is typed (first press opens it).
        Thread.Sleep(800);
        if (IsWindow(dialog) && IsWindowVisible(dialog) && ok is not null && ok.TryGetCurrentPattern(InvokePattern.Pattern, out var ip2))
        {
            try { ((InvokePattern)ip2).Invoke(); } catch (Exception) { }
        }
    }

    /// <summary>Build diagnostics: every control UI Automation sees in the browser's windows.</summary>
    public static string Dump(string processName)
    {
        var sb = new StringBuilder();
        foreach (var w in BrowserWindows(processName))
        {
            sb.AppendLine($"window {w.Handle} {w.Class}");
            var el = Element(w.Handle);
            if (el is null) continue;
            try
            {
                foreach (AutomationElement e in el.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                {
                    try
                    {
                        var c = e.Current;
                        if (string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.AutomationId)) continue;
                        var tog = (bool)e.GetCurrentPropertyValue(AutomationElement.IsTogglePatternAvailableProperty);
                        var inv = (bool)e.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty);
                        sb.AppendLine($"  {c.ControlType.ProgrammaticName} id='{c.AutomationId}' name='{c.Name}' toggle={tog} invoke={inv} rect={c.BoundingRectangle}");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { sb.AppendLine("  error " + ex.Message); }
        }
        return sb.ToString();
    }

    private static AutomationElement? Element(IntPtr h)
    {
        try { return AutomationElement.FromHandle(h); } catch (Exception) { return null; }
    }

    // ---------- windows of the browser ----------

    private sealed record Win(IntPtr Handle, string Class);

    private static List<Win> BrowserWindows(string processName)
    {
        var ids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
        var list = new List<Win>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (ids.Contains(pid) && IsWindowVisible(h))
            {
                var cls = ClassOf(h);
                if (cls.StartsWith("Chrome_WidgetWin")) list.Add(new Win(h, cls));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static List<IntPtr> Dialogs(string processName)
    {
        var ids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (ids.Contains(pid) && IsWindowVisible(h) && ClassOf(h) == "#32770") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    private static void PressSpace(IntPtr window, AutomationElement e)
    {
        try
        {
            SetForegroundWindow(window);
            Thread.Sleep(250);
            e.SetFocus();
            Thread.Sleep(250);
            System.Windows.Forms.SendKeys.SendWait(" ");
            Thread.Sleep(900);
        }
        catch (Exception ex) { Log.Write("press space: " + ex.Message); }
    }

    private static void ClickOn(IntPtr window, AutomationElement e)
    {
        try
        {
            SetForegroundWindow(window);
            Thread.Sleep(250);
            var r = e.Current.BoundingRectangle; // physical screen pixels
            SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: same pixels as UI Automation
            SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
            Thread.Sleep(150);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // left down
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // left up
            Thread.Sleep(900);
        }
        catch (Exception ex) { Log.Write("click: " + ex.Message); }
    }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);

    // ---------- the window the person uses ----------

    /// <summary>Asks first, shows "don't touch anything" while it works, then the result.</summary>
    public static async Task RunWithUi(Window? owner, IReadOnlyList<BrowserInfo> browsers)
    {
        if (browsers.Count == 0) { Ui.Message(owner, L.T("لا يوجد متصفح"), L.T("لم أجد Chrome أو Edge أو متصفح مشابه على الجهاز.")); return; }
        var names = string.Join(L.T(" و "), browsers.Select(b => b.Name));
        if (Ui.Ask(owner, L.T("تنصيب الإضافة تلقائياً"),
                L.F("سيتم فتح {0} وتنصيب إضافة IBDM Pro بشكل تلقائي.\n\n⚠ لا تضغط على أي شيء ولا تحرّك الماوس على المتصفح لحين انتهاء التنصيب (حوالي ١٥ ثانية لكل متصفح).", names),
                L.T("ابدأ التنصيب"), L.T("إلغاء")) != 0) return;

        var overlay = new InstallOverlay();
        overlay.Show();
        var results = new List<(BrowserInfo B, Result R)>();
        using var cts = new CancellationTokenSource();
        overlay.Cancelled += cts.Cancel;
        var server = IbdmApp.Instance.Server;
        foreach (var b in browsers)
        {
            overlay.SetTitle(L.F("جاري تنصيب الإضافة على {0}", b.Name));
            var r = await Task.Run(() =>
            {
                Result res = null!;
                var t = new Thread(() => res = Install(b, Paths.ExtensionDir, s => overlay.Dispatcher.BeginInvoke(() => overlay.AddStep(s)),
                    () => server?.LastExtensionContact ?? DateTime.MinValue, cts.Token));
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                t.Join();
                return res;
            });
            results.Add((b, r));
            if (cts.IsCancellationRequested) break;
        }
        overlay.Close();

        var ok = results.All(x => x.R.Ok);
        var text = string.Join("\n\n", results.Select(x => (x.R.Ok ? "✓ " : "✗ ") + x.B.Name + ": " + x.R.Message));
        if (!ok) text += L.T("\n\nإذا فشل التنصيب التلقائي، استخدم الخطوات اليدوية بنافذة «الإضافة».");
        Ui.Message(owner, ok ? L.T("تم تنصيب الإضافة ✓") : L.T("لم يكتمل التنصيب"), text);
    }
}

/// <summary>The message in the middle of the screen while the installer works: it never takes focus or clicks,
/// so the automation keeps control of the browser.</summary>
public sealed class InstallOverlay : Window
{
    private readonly TextBlock _title = Ui.Text("", 18, Theme.Ink, bold: true);
    private readonly StackPanel _steps = new() { Margin = new Thickness(0, 12, 0, 0) };
    public event Action? Cancelled;

    public InstallOverlay()
    {
        Ui.Setup(this, L.T("IBDM Pro — تنصيب الإضافة"), 560, 300);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Logo(40));
        _title.Margin = new Thickness(12, 0, 0, 0);
        head.Children.Add(_title);
        var warn = Ui.Card(Ui.Text(L.T("⚠ لا تضغط على أي شيء ولا تحرّك الماوس لحين انتهاء تنصيب الإضافة"), 15, Theme.Gold, bold: true, wrap: TextWrapping.Wrap));
        warn.Margin = new Thickness(0, 14, 0, 0);
        var bar = new ProgressBar { Style = Theme.Progress, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 0) };
        var hint = Ui.Text(L.T("للإيقاف اضغط Esc"), 11.5, Theme.Muted);
        hint.Margin = new Thickness(0, 10, 0, 0);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(head);
        root.Children.Add(warn);
        root.Children.Add(bar);
        root.Children.Add(_steps);
        root.Children.Add(hint);
        Content = new Border
        {
            Child = root, Background = Theme.Panel, BorderBrush = Theme.Gold, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.5 },
            Margin = new Thickness(20),
        };
        SourceInitialized += (_, _) =>
        {
            // Never activated and clicks pass through, so the browser keeps focus for the automation.
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x00000080 | 0x00000020);
        };
        _hotkey = new Thread(WatchEscape) { IsBackground = true };
        _hotkey.Start();
        Closed += (_, _) => _closed = true;
    }

    private readonly Thread _hotkey;
    private volatile bool _closed;

    private void WatchEscape()
    {
        while (!_closed)
        {
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { Dispatcher.BeginInvoke(() => Cancelled?.Invoke()); return; }
            Thread.Sleep(80);
        }
    }

    public void SetTitle(string t)
    {
        _title.Text = t;
        _steps.Children.Clear();
    }

    public void AddStep(string s)
    {
        var line = Ui.Text("• " + s, 13, Theme.Ink);
        line.Margin = new Thickness(0, 3, 0, 0);
        _steps.Children.Add(line);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
