using IbdmPro.Core;
using WF = System.Windows.Forms;

namespace IbdmPro.App;

/// <summary>The icon next to the clock: IBDM Pro keeps running there so the browser can always hand downloads to it.</summary>
public sealed class Tray : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private string? _lastFile;
    private Action? _onClick;

    public Tray(IbdmApp app)
    {
        var menu = new WF.ContextMenuStrip { RightToLeft = L.Rtl ? WF.RightToLeft.Yes : WF.RightToLeft.No };
        menu.Items.Add(L.T("فتح IBDM Pro"), null, (_, _) => app.ShowMain());
        menu.Items.Add(L.T("إضافة رابط…"), null, (_, _) => { app.ShowMain(); app.Main.AddUrl(); });
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(L.T("استئناف الكل"), null, (_, _) => app.Manager.ResumeAll());
        menu.Items.Add(L.T("إيقاف الكل"), null, (_, _) => app.Manager.PauseAll());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(L.T("فتح مجلد التحميلات"), null, (_, _) => { Directory.CreateDirectory(Settings.Current.DownloadRoot); Ui.Open(Settings.Current.DownloadRoot); });
        menu.Items.Add(L.T("الإعدادات"), null, (_, _) => { app.ShowMain(); app.Main.OpenSettings(); });
        menu.Items.Add(L.T("البحث عن تحديث…"), null, (_, _) => { app.ShowMain(); Updater.ShowWindow(app.Main); });
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(L.T("خروج"), null, (_, _) => app.Quit());

        System.Drawing.Icon? icon = null;
        try { if (Environment.ProcessPath is { } exe) icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); } catch (Exception) { }
        _icon = new WF.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "IBDM Pro",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) app.ShowMain(); };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_onClick is { } click) { click(); return; }
            if (_lastFile is not null && File.Exists(_lastFile)) Ui.ShowInFolder(_lastFile);
            else app.ShowMain();
        };
    }

    /// <summary>A Windows notice that runs <paramref name="onClick"/> when clicked.</summary>
    public void NotifyAction(string title, string text, Action onClick)
    {
        _lastFile = null;
        _onClick = onClick;
        _icon.ShowBalloonTip(8000, title, text.Length > 200 ? text[..200] : text, WF.ToolTipIcon.Info);
    }

    public void Notify(string title, string text, string? file)
    {
        _onClick = null;
        _lastFile = file;
        _icon.ShowBalloonTip(4000, title, text.Length > 200 ? text[..200] : text, WF.ToolTipIcon.None);
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
