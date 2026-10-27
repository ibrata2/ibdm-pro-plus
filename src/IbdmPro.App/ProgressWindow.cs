using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IbdmPro.Core;

namespace IbdmPro.App;

/// <summary>The window each download gets while it runs (like IDM): file details, how far it has got, speed,
/// time left, each connection's range and progress, and what to do when it finishes.</summary>
public sealed class ProgressWindow : Window
{
    private static readonly Dictionary<string, ProgressWindow> OpenWindows = [];

    private readonly DownloadItem _item;
    private readonly DownloadManager _manager;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _finishedHandled;

    private readonly TextBlock _icon = Ui.Glyph("", 24, Theme.Gold);
    private readonly Border _iconTile = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(12) };
    private readonly TextBlock _name = Ui.Text("", 15, Theme.Ink, bold: true);
    private readonly TextBlock _url = Ui.Ltr("", 11.5, Theme.Muted);
    private readonly TextBlock _status = Ui.Text("", 13, Theme.Gold, bold: true);
    private readonly TextBlock _size = Ui.Ltr("", 13, Theme.Ink, bold: true);
    private readonly TextBlock _done = Ui.Ltr("", 13, Theme.Ink);
    private readonly TextBlock _speed = Ui.Ltr("", 13, Theme.Gold, bold: true);
    private readonly TextBlock _eta = Ui.Ltr("", 13, Theme.Ink);
    private readonly TextBlock _resume = Ui.Text("", 13, Theme.Ink);
    private readonly TextBlock _conns = Ui.Ltr("", 13, Theme.Ink);
    private readonly ProgressBar _bar = new() { Style = Theme.Progress, Maximum = 100, Height = Theme.IsGlass ? 16 : 12 };
    private readonly TextBlock _percent = Ui.Ltr("", 22, Theme.Ink, bold: true);
    private readonly SegmentBar _segments = new();
    private readonly StackPanel _connRows = new();
    private readonly StackPanel _details = new();
    private readonly Button _pause, _cancel, _toggleDetails, _toApp;
    private readonly StackPanel _runningButtons = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _doneButtons = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly CheckBox _autoClose, _openFile, _openFolder, _dontShow;
    private readonly TextBlock _error = Ui.Text("", 12.5, Theme.Red, wrap: TextWrapping.Wrap);

    public static void Open(DownloadItem item, DownloadManager manager, bool activate = true)
    {
        if (OpenWindows.TryGetValue(item.Id, out var w))
        {
            if (activate) Ui.ForceFront(w);
            return;
        }
        w = new ProgressWindow(item, manager);
        OpenWindows[item.Id] = w;
        w.Show();
        if (activate) Ui.ForceFront(w);
    }

    /// <summary>For the build's snapshots.</summary>
    public static Window Preview(DownloadItem item, DownloadManager manager) => new ProgressWindow(item, manager);

    public static void CloseFor(DownloadItem item)
    {
        if (OpenWindows.TryGetValue(item.Id, out var w)) w.Close();
    }

    private ProgressWindow(DownloadItem item, DownloadManager manager)
    {
        _item = item;
        _manager = manager;
        Ui.Setup(this, item.FileName, 600, 520);
        ResizeMode = ResizeMode.CanMinimize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var s = Settings.Current;

        // header
        _iconTile.Child = _icon;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_iconTile, Dock.Left);
        head.Children.Add(_iconTile);
        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _name.FlowDirection = Ui.DirectionOf(item.FileName);
        _name.HorizontalAlignment = HorizontalAlignment.Left;
        titles.Children.Add(_name);
        titles.Children.Add(_url);
        head.Children.Add(titles);

        // facts
        var facts = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = new (string, UIElement)[]
        {
            (L.T("الحالة"), _status), (L.T("حجم الملف"), _size), (L.T("المحمَّل"), _done), (L.T("معدل النقل"), _speed),
            (L.T("الوقت المتبقي"), _eta), (L.T("إمكانية الاستئناف"), _resume), (L.T("الاتصالات"), _conns),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            facts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var l = Ui.Text(rows[i].Item1, 12.5, Theme.Muted);
            Grid.SetRow(l, i);
            facts.Children.Add(l);
            var v = rows[i].Item2;
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            facts.Children.Add(v);
        }
        var factsCard = Ui.Card(facts, new Thickness(16, 10, 16, 10));

        // big progress
        var progress = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _percent.Margin = new Thickness(12, 0, 0, 0);
        _percent.MinWidth = 76;
        _percent.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(_percent, Dock.Right);
        progress.Children.Add(_percent);
        _bar.VerticalAlignment = VerticalAlignment.Center;
        progress.Children.Add(_bar);

        // buttons
        _pause = Ui.Button(L.T("إيقاف مؤقت"), "", Theme.Button, TogglePause);
        _cancel = Ui.Button(L.T("إلغاء التحميل"), "", Theme.DangerButton, Cancel);
        _toggleDetails = Ui.Button(L.T("إخفاء التفاصيل"), "", Theme.Button, ToggleDetails);
        _toApp = Ui.Button(L.T("للبرنامج"), "", Theme.Button, SendToApp);
        _toApp.ToolTip = L.T("إغلاق هذه النافذة ومتابعة التحميل داخل نافذة البرنامج");
        foreach (var b in new[] { _pause, _cancel, _toggleDetails, _toApp }) { b.Margin = new Thickness(0, 0, 8, 0); _runningButtons.Children.Add(b); }

        var openFile = Ui.Button(L.T("فتح الملف"), "", Theme.PrimaryButton, () => { OpenFileNow(); Close(); });
        var openFolder = Ui.Button(L.T("فتح المجلد"), "", Theme.Button, () => { Ui.ShowInFolder(_item.FullPath); Close(); });
        var close = Ui.Button(L.T("إغلاق"), style: Theme.Button, click: Close);
        foreach (var b in new[] { openFile, openFolder, close }) { b.Margin = new Thickness(0, 0, 8, 0); _doneButtons.Children.Add(b); }
        var buttonRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttonRow.Children.Add(_runningButtons);
        buttonRow.Children.Add(_doneButtons);

        // per-connection details
        _details.Margin = new Thickness(0, 14, 0, 0);
        _details.Children.Add(Ui.Text(L.T("نقاط البداية لكل اتصال وحالة تقدّمه"), 12, Theme.Muted));
        _segments.Margin = new Thickness(0, 6, 0, 8);
        _details.Children.Add(_segments);
        var listHead = ConnRow("N.", L.T("المحمَّل"), L.T("المعلومات"), header: true);
        var list = new StackPanel();
        list.Children.Add(listHead);
        list.Children.Add(new ScrollViewer { Content = _connRows, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _details.Children.Add(Ui.Card(list, new Thickness(0, 4, 0, 4)));

        // when it finishes
        _autoClose = Ui.Check(L.T("إغلاق هذه النافذة تلقائياً بعد انتهاء التحميل"), s.ProgressAutoClose);
        _openFile = Ui.Check(L.T("فتح الملف بعد انتهاء التحميل"), s.ProgressOpenFile);
        _openFolder = Ui.Check(L.T("فتح المجلد بعد انتهاء التحميل"), s.ProgressOpenFolder);
        _dontShow = Ui.Check(L.T("عدم إظهار هذه النافذة عند التحميل (تقدر ترجعها من الإعدادات)"), !s.ShowProgressWindow);
        void Persist()
        {
            var st = Settings.Current;
            st.ProgressAutoClose = _autoClose.IsChecked == true;
            st.ProgressOpenFile = _openFile.IsChecked == true;
            st.ProgressOpenFolder = _openFolder.IsChecked == true;
            st.ShowProgressWindow = _dontShow.IsChecked != true;
            st.Save();
        }
        foreach (var c in new[] { _autoClose, _openFile, _openFolder, _dontShow }) { c.Checked += (_, _) => Persist(); c.Unchecked += (_, _) => Persist(); }
        var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        options.Children.Add(_autoClose);
        options.Children.Add(_openFile);
        options.Children.Add(_openFolder);
        options.Children.Add(_dontShow);

        _error.Margin = new Thickness(0, 8, 0, 0);
        _error.Visibility = Visibility.Collapsed;

        var credit = Ui.Text(L.T("IBDM Pro — برمجة و تطوير ابراهيم الخزرجي"), 11, Theme.Muted);
        credit.Margin = new Thickness(0, 12, 0, 0);
        credit.Opacity = 0.75;

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        root.Children.Add(head);
        root.Children.Add(factsCard);
        root.Children.Add(progress);
        root.Children.Add(_error);
        root.Children.Add(buttonRow);
        root.Children.Add(_details);
        root.Children.Add(options);
        root.Children.Add(credit);
        Content = root;

        if (item.Kind == DownloadKind.Media) ToggleDetails(); // videos have no byte ranges to show
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); OpenWindows.Remove(_item.Id); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Refresh();
    }

    private static Grid ConnRow(string n, string done, string info, bool header = false)
    {
        var g = new Grid { Margin = new Thickness(10, header ? 4 : 2, 10, header ? 6 : 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var fg = header ? Theme.Muted : Theme.Ink;
        var a = Ui.Ltr(n, 12, fg);
        var b = Ui.Ltr(done, 12, fg);
        var c = Ui.Text(info, 12, fg);
        Grid.SetColumn(b, 1);
        Grid.SetColumn(c, 2);
        g.Children.Add(a);
        g.Children.Add(b);
        g.Children.Add(c);
        return g;
    }

    private void Refresh()
    {
        var i = _item;
        var cat = Settings.Current.CategoryNamed(i.Category) ?? Settings.Current.CategoryFor(i.FileName);
        _icon.Text = cat.Icon;
        _icon.Foreground = Theme.Br(cat.Color);
        var c = (Color)ColorConverter.ConvertFromString(cat.Color);
        _iconTile.Background = new SolidColorBrush(Color.FromArgb(0x2A, c.R, c.G, c.B));
        _name.Text = i.FileName;
        Title = (i.TotalBytes > 0 && i.Status != DownloadStatus.Completed ? $"{i.Percent:0}% " : "") + i.FileName;
        string url;
        try { url = Uri.UnescapeDataString(i.PageUrl ?? i.Url); } catch (Exception) { url = i.PageUrl ?? i.Url; }
        _url.Text = url;
        _url.ToolTip = url;

        _status.Text = i.Status switch
        {
            DownloadStatus.Downloading => i.Kind == DownloadKind.Media && i.Quality is not null ? L.F("جاري التحميل — {0}", i.Quality.TrimStart('‎')) : L.T("جاري التحميل"),
            DownloadStatus.Queued => L.T("بالانتظار (يبدأ بعد انتهاء تحميل آخر)"),
            _ => i.StatusText,
        };
        _status.Foreground = i.Status switch { DownloadStatus.Completed => Theme.Green, DownloadStatus.Error => Theme.Red, DownloadStatus.Paused => Theme.Muted, _ => Theme.Gold };
        _size.Text = i.TotalBytes > 0 ? Fmt.Size(i.TotalBytes) : L.T("غير معروف");
        _done.Text = i.TotalBytes > 0 ? $"{Fmt.Size(i.Downloaded).TrimStart('‎')}  ({i.Percent:0.##}%)" : Fmt.Size(i.Downloaded);
        _speed.Text = i.Status == DownloadStatus.Downloading ? (i.Speed > 0 ? Fmt.Speed(i.Speed) : "…") : "—";
        _eta.Text = i.Status == DownloadStatus.Downloading && i.TotalBytes > 0 ? (Fmt.Eta(i.TotalBytes - i.Downloaded, i.Speed) is { Length: > 0 } e ? e : "…") : "—";
        _resume.Text = i.Kind == DownloadKind.Media ? L.T("نعم") : i.Resumable ? L.T("نعم ✓") : L.T("لا (الخادم لا يدعمها)");
        _resume.Foreground = i.Kind == DownloadKind.Media || i.Resumable ? Theme.Green : Theme.Red;

        List<Segment> segs;
        lock (i.Gate) segs = i.Segments.Select(x => new Segment { Start = x.Start, End = x.End, Done = x.Done }).OrderBy(x => x.Start).ToList();
        var active = segs.Count(x => !x.Finished);
        _conns.Text = i.Kind == DownloadKind.Media ? L.F("{0} (أجزاء الفيديو)", Settings.Current.Connections)
            : i.Resumable ? (i.Status == DownloadStatus.Downloading ? L.F("{0} نشط من {1}", active, segs.Count) : $"{segs.Count}") : "1";

        _bar.IsIndeterminate = i.Status == DownloadStatus.Processing || (i.Status == DownloadStatus.Downloading && i.TotalBytes <= 0);
        _bar.Value = i.Status == DownloadStatus.Completed ? 100 : i.Percent;
        _bar.Foreground = i.Status switch { DownloadStatus.Error => Theme.Red, DownloadStatus.Paused => Theme.Muted, DownloadStatus.Completed => Theme.Green, _ => Theme.ProgressFill };
        _percent.Text = i.Status == DownloadStatus.Completed ? "100%" : i.TotalBytes > 0 ? $"{i.Percent:0.#}%" : "";

        _segments.Update(segs, i.TotalBytes, i.Kind == DownloadKind.Media ? i.Percent : -1);
        _connRows.Children.Clear();
        if (i.Kind == DownloadKind.File && segs.Count > 0)
        {
            for (var n = 0; n < segs.Count; n++)
            {
                var sg = segs[n];
                var info = sg.Finished ? L.T("اكتمل") : i.Status == DownloadStatus.Downloading ? L.T("يستقبل البيانات…") : i.Status == DownloadStatus.Paused ? L.T("متوقف") : L.T("بالانتظار");
                _connRows.Children.Add(ConnRow((n + 1).ToString(), Fmt.Size(Math.Min(sg.Done, sg.End - sg.Start + 1)).TrimStart('‎'), info));
            }
        }
        else _connRows.Children.Add(ConnRow("1", Fmt.Size(i.Downloaded).TrimStart('‎'),
            i.Kind == DownloadKind.Media ? (i.Phase ?? L.T("تحميل الفيديو بعدة أجزاء")) : i.Status == DownloadStatus.Downloading ? L.T("يستقبل البيانات…") : i.StatusText));

        _pause.IsEnabled = i.Status != DownloadStatus.Processing;
        SetButton(_pause, i.Status is DownloadStatus.Paused or DownloadStatus.Error ? (L.T("استئناف"), "") : (L.T("إيقاف مؤقت"), ""));
        _error.Text = i.Status == DownloadStatus.Error ? "⚠ " + i.Error : "";
        _error.Visibility = i.Status == DownloadStatus.Error ? Visibility.Visible : Visibility.Collapsed;

        if (i.Status == DownloadStatus.Completed && !_finishedHandled)
        {
            _finishedHandled = true;
            Finished();
        }
        if (!_manager.Items.Contains(i)) Close(); // removed from the list
    }

    private void Finished()
    {
        _runningButtons.Visibility = Visibility.Collapsed;
        _doneButtons.Visibility = Visibility.Visible;
        _status.Text = L.T("✓ اكتمل التحميل");
        if (_openFile.IsChecked == true) OpenFileNow();
        if (_openFolder.IsChecked == true) Ui.ShowInFolder(_item.FullPath);
        if (_autoClose.IsChecked == true) { Close(); return; }
        Ui.ForceFront(this);
    }

    private void OpenFileNow()
    {
        if (File.Exists(_item.FullPath)) Ui.Open(_item.FullPath);
        else Ui.Message(this, L.T("الملف غير موجود"), _item.FullPath);
    }

    private static void SetButton(Button b, (string Text, string Glyph) v)
    {
        if (b.Content is StackPanel sp && sp.Children.Count == 2 && sp.Children[1] is TextBlock t && t.Text != v.Text)
        {
            t.Text = v.Text;
            if (sp.Children[0] is TextBlock g) g.Text = v.Glyph;
        }
    }

    private void TogglePause()
    {
        if (_item.Status is DownloadStatus.Paused or DownloadStatus.Error) _manager.Resume(_item);
        else _manager.Pause(_item);
        Refresh();
    }

    /// <summary>"Cancel download": stops it and closes this window. The download stays in the list as paused,
    /// with what was already downloaded, so it can be resumed later (deleting is done from the main window).</summary>
    private void Cancel()
    {
        _manager.Pause(_item);
        Close();
    }

    private void ToggleDetails()
    {
        var show = _details.Visibility != Visibility.Visible;
        _details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetButton(_toggleDetails, show ? (L.T("إخفاء التفاصيل"), "") : (L.T("عرض التفاصيل"), ""));
    }

    /// <summary>"Into the app": the download keeps going in the main window's list.</summary>
    private void SendToApp()
    {
        Close();
        IbdmApp.Instance.ShowMain();
        IbdmApp.Instance.Main.Select(_item);
    }
}

/// <summary>One bar showing where every connection started and how far it has got.</summary>
public sealed class SegmentBar : FrameworkElement
{
    private List<Segment> _segs = [];
    private long _total;
    private double _mediaPercent = -1;

    public SegmentBar() { Height = Theme.IsGlass ? 18 : 14; }

    public void Update(List<Segment> segs, long total, double mediaPercent)
    {
        _segs = segs;
        _total = total;
        _mediaPercent = mediaPercent;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Theme.Track, new Pen(Theme.Line, 1), rect, 3, 3);
        // Drawn left to right like the file itself, whatever the window's direction.
        double X(long b) => FlowDirection == FlowDirection.RightToLeft ? w - b * w / _total : b * w / _total;
        if (_mediaPercent >= 0)
        {
            var fw = w * Math.Clamp(_mediaPercent, 0, 100) / 100;
            dc.DrawRoundedRectangle(Theme.ProgressFill, null, new Rect(FlowDirection == FlowDirection.RightToLeft ? w - fw : 0, 1, fw, h - 2), 3, 3);
            return;
        }
        if (_total <= 0) return;
        foreach (var s in _segs)
        {
            var done = Math.Min(s.Done, s.End - s.Start + 1);
            if (done > 0)
            {
                var a = X(s.Start);
                var b = X(s.Start + done);
                dc.DrawRectangle(Theme.ProgressFill, null, new Rect(Math.Min(a, b), 1, Math.Max(1, Math.Abs(b - a)), h - 2));
            }
            var start = X(s.Start);
            dc.DrawRectangle(Theme.Ink, null, new Rect(start, 0, 1.2, h)); // where this connection started
        }
    }
}
