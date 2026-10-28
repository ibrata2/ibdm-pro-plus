using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IbdmPro.Core;

namespace IbdmPro.App;

/// <summary>Where downloads arrive from the browser or "Add link".</summary>
public static class Capture
{
    public static void Handle(CaptureRequest req)
    {
        if (req.Kind == "media" || Settings.Current.ShowInfoBeforeDownload) { InfoDialog.Open(req); return; }
        _ = Quick(req);
    }

    /// <summary>"Show file info before downloading" is off: check the link and start straight away.</summary>
    private static async Task Quick(CaptureRequest req)
    {
        var probe = await Http.ProbeAsync(req.Url, req.ToRequestInfo());
        if (probe.Error is not null || probe.LooksLikeWebPage) { InfoDialog.Open(req); return; }
        var s = Settings.Current;
        var name = FileNames.Clean(string.IsNullOrWhiteSpace(req.FileName) ? probe.FileName : Path.GetFileName(req.FileName));
        var cat = s.CategoryFor(name);
        var item = new DownloadItem
        {
            Kind = DownloadKind.File, Url = probe.FinalUrl, PageUrl = req.PageUrl, Request = req.ToRequestInfo(),
            FileName = name, TotalBytes = probe.Size, Resumable = probe.Resumable, Category = cat.Name, Folder = s.FolderFor(cat), QueueName = req.QueueName,
        };
        IbdmApp.Instance.Manager.Add(item, start: true);
        IbdmApp.Instance.Tray?.Notify(L.T("بدأ التحميل"), $"{name} — {Fmt.Size(probe.Size)}", null);
    }
}

/// <summary>"معلومات الملف": shown before every download. It checks the link and shows the name, size and type,
/// lets the person pick the folder and category, and for videos lists every available quality with its size
/// (plus audio only), then starts now or adds the download for later.</summary>
public sealed class InfoDialog : Window
{
    private readonly CaptureRequest _req;
    private readonly CancellationTokenSource _cts = new();
    private readonly Settings _s = Settings.Current;

    private readonly StackPanel _body = new();
    private readonly TextBlock _headIcon = Ui.Glyph("", 26, Theme.Gold);
    private readonly Border _headTile = new() { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = Theme.GoldSoft };
    private readonly TextBlock _headTitle = Ui.Text(L.T("جاري فحص الرابط…"), 16, Theme.Ink, bold: true);
    private readonly TextBlock _headSub = Ui.Text("", 12, Theme.Muted);
    private readonly Button _start, _later;

    private readonly TextBox _name = Ui.Input();
    private readonly TextBox _folder = Ui.Input();
    private readonly ComboBox _queue = new() { MinWidth = 150, Padding = new Thickness(5, 2, 5, 2) };
    private readonly CheckBox _remember = Ui.Check(L.T("تذكر هذا المجلد لهذا النوع من الملفات"), false);
    private readonly TextBlock _space = Ui.Text("", 12, Theme.Muted, wrap: TextWrapping.Wrap);
    private readonly TextBlock _sizeValue = Ui.Ltr("", 13, Theme.Ink, bold: true);
    private readonly TextBlock _freeValue = Ui.Ltr("", 12, Theme.Muted);
    private readonly TextBlock _freeLabel = Ui.Text("", 12, Theme.Muted);
    private readonly TextBlock _typeValue = Ui.Text("", 13, Theme.Ink);
    private readonly WrapPanel _categories = new();
    private Category _category;
    private bool _folderEdited;

    private ProbeResult? _probe;
    private MediaInfo? _media;
    private MediaOption? _option;

    private InfoDialog(CaptureRequest req)
    {
        _req = req;
        Ui.Setup(this, L.T("IBDM Pro — معلومات الملف"), 590, 580);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinHeight = 360;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        _category = _s.CategoryFor(req.FileName ?? FileNames.FromUrl(req.Url) ?? "");

        _start = Ui.Button(L.T("بدء التحميل"), "", Theme.PrimaryButton, () => Finish(true));
        _later = Ui.Button(L.T("إضافة إلى قائمة الانتظار"), "", Theme.Button, () => Finish(false));
        _start.IsEnabled = _later.IsEnabled = false;
        _start.IsDefault = true;
        var cancel = Ui.Button(L.T("إلغاء"), style: Theme.Button, click: Close);
        cancel.IsCancel = true;
        foreach (var button in new[] { _start, _later, cancel })
        {
            button.Padding = new Thickness(9, 5, 9, 5);
            button.MinWidth = 0;
        }

        // header
        _headTile.Child = _headIcon;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_headTile, Dock.Left);
        head.Children.Add(_headTile);
        var titles = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_headTitle);
        titles.Children.Add(_headSub);
        head.Children.Add(titles);

        _body.Children.Add(new ProgressBar { Style = Theme.Progress, IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 10) });
        _body.Children.Add(Ui.Text(_req.Url, 12, Theme.Muted));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _start, _later, cancel }) { b.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(b); }

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        root.Children.Add(head);
        root.Children.Add(_body);
        root.Children.Add(buttons);
        root.Children.Add(Credit());
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _folder.TextChanged += (_, _) => { UpdateSpace(); };
        _folder.PreviewKeyDown += (_, _) => _folderEdited = true;
        _name.TextChanged += (_, _) => { _name.FlowDirection = Ui.DirectionOf(_name.Text); UpdateSpace(); };
        Closed += (_, _) => _cts.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => { if (!_preview) await LoadAsync(); };
    }

    private bool _preview;

    /// <summary>For the build's snapshots: the dialog already filled with a checked file or video.</summary>
    public static InfoDialog Preview(CaptureRequest req, ProbeResult? probe, MediaInfo? media)
    {
        var d = new InfoDialog(req) { _preview = true, _probe = probe, _media = media };
        if (media is not null) d.ShowMedia(); else d.ShowFile();
        return d;
    }

    public static void Open(CaptureRequest req)
    {
        var d = new InfoDialog(req);
        d.Show();
        Ui.ForceFront(d);
    }

    private static UIElement Credit()
    {
        var t = Ui.Text(L.T("IBDM Pro — برمجة و تطوير ابراهيم الخزرجي"), 11, Theme.Muted);
        t.Margin = new Thickness(0, 8, 0, 0);
        t.Opacity = 0.7;
        return t;
    }

    // ---------- checking the link ----------

    private async Task LoadAsync()
    {
        var info = _req.ToRequestInfo();
        var ct = _cts.Token;
        try
        {
            if (_req.Kind == "media")
            {
                // A video on a page: the page address gives yt-dlp every quality (YouTube, Facebook…);
                // otherwise the stream itself (m3u8/mp4); otherwise download the file as it is.
                string? lastError = null;
                var sources = new[] { _req.PageUrl, _req.Url }.Where(u => !string.IsNullOrEmpty(u) && !u!.StartsWith("blob:")).Distinct().ToList();
                foreach (var src in sources)
                {
                    _headTitle.Text = L.T("جاري البحث عن الجودات المتوفرة…");
                    _headSub.Text = Host(src!);
                    try { _media = await YtDlp.GetInfoAsync(src!, info, ct); break; }
                    catch (InvalidOperationException e) { Log.Write($"media {src}: {e.Message}"); lastError ??= e.Message; }
                    // On YouTube the page address is the right one; a stream address after it would only hide its error.
                    if (YtDlp.IsYoutube(src!)) break;
                }
                if (_media is not null && _media.Options.Count > 0) { ShowMedia(); return; }
                if (_req.Url.StartsWith("http"))
                {
                    _probe = await Http.ProbeAsync(_req.Url, info, ct);
                    if (_probe.Error is null && !_probe.LooksLikeWebPage) { ShowFile(); return; }
                }
                ShowError(lastError ?? L.T("لم يتم العثور على فيديو يمكن تحميله في هذه الصفحة."));
                return;
            }

            _probe = await Http.ProbeAsync(_req.Url, info, ct);
            if (_probe.Error is null && !_probe.LooksLikeWebPage) { ShowFile(); return; }

            // A page address (YouTube, Instagram…) pasted as a link: look for a video in it.
            _headTitle.Text = L.T("جاري البحث عن فيديو في الصفحة…");
            string? mediaError = null;
            try
            {
                _media = await YtDlp.GetInfoAsync(_req.Url, info, ct);
                if (_media.Options.Count > 0) { ShowMedia(); return; }
            }
            catch (InvalidOperationException e) { Log.Write($"media {_req.Url}: {e.Message}"); mediaError = e.Message; }

            if (YtDlp.IsYoutube(_req.Url)) ShowError(mediaError ?? L.T("لم يتم العثور على فيديو يمكن تحميله في هذه الصفحة."));
            else if (_probe.Error is null) ShowFile(); // a web page with no video: download the page itself
            else ShowError(mediaError ?? _probe.Error);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { ShowError(e.Message); }
    }

    private void ShowError(string message)
    {
        _headTitle.Text = L.T("تعذر تحميل هذا الرابط");
        _headIcon.Text = "";
        _headIcon.Foreground = Theme.Red;
        _headTile.Background = Theme.RedSoft;
        _body.Children.Clear();
        _body.Children.Add(Ui.Card(Ui.Text(message, 13, Theme.Ink, wrap: TextWrapping.Wrap)));
        var url = Ui.Input(YtDlp.CleanUrl(_req.PageUrl ?? _req.Url));
        url.IsReadOnly = true;
        url.FlowDirection = FlowDirection.LeftToRight;
        url.Margin = new Thickness(0, 10, 0, 0);
        _body.Children.Add(url);
        var retry = Ui.Button(L.T("إعادة المحاولة"), "", Theme.Button, async () => { Reset(); await LoadAsync(); });
        // Some sites refuse download programs: hand the link back to the browser, which downloads it itself
        // (the extension is told to leave this link alone).
        var inBrowser = Ui.Button(L.T("التحميل بالمتصفح"), "\uE774", Theme.Button, DownloadInBrowser);
        inBrowser.Margin = new Thickness(8, 0, 0, 0);
        inBrowser.ToolTip = L.T("يتحمل الملف بالمتصفح نفسه بدل IBDM Pro");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(retry);
        row.Children.Add(inBrowser);
        _body.Children.Add(row);
    }

    private void DownloadInBrowser()
    {
        var url = _req.Url;
        LocalServer.LetBrowserDownload(url, _probe?.FinalUrl);
        try { Ui.Open(url); } catch (Exception e) { Log.Write($"open in browser: {e.Message}"); }
        Close();
    }

    private void Reset()
    {
        _headIcon.Text = "";
        _headIcon.Foreground = Theme.Gold;
        _headTile.Background = Theme.GoldSoft;
        _headTitle.Text = L.T("جاري فحص الرابط…");
        _body.Children.Clear();
        _body.Children.Add(new ProgressBar { Style = Theme.Progress, IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 10) });
    }

    // ---------- a plain file ----------

    private void ShowFile()
    {
        var p = _probe!;
        var name = !string.IsNullOrWhiteSpace(_req.FileName) ? FileNames.Clean(Path.GetFileName(_req.FileName)) : p.FileName;
        if (Path.GetExtension(name).Length == 0 && Path.GetExtension(p.FileName).Length > 0) name += Path.GetExtension(p.FileName);
        var size = p.Size > 0 ? p.Size : _req.Size;
        _category = _s.CategoryFor(name);
        SetHead(_category, L.T("معلومات الملف"), Host(p.FinalUrl));

        _body.Children.Clear();
        _body.Children.Add(CompactLabel(L.T("اسم الملف")));
        _name.Text = name;
        _body.Children.Add(_name);

        var facts = new UniformGrid3();
        _sizeValue.Text = Fmt.Size(size);
        facts.Add(L.T("الحجم"), _sizeValue);
        _typeValue.Text = TypeName(name, p.ContentType);
        facts.Add(L.T("النوع"), _typeValue);
        facts.Add(L.T("التحميل المجزأ"), Ui.Text(p.Resumable ? L.F("مدعوم ✓ ({0} اتصالات)", _s.Connections) : L.T("غير مدعوم (اتصال واحد)"), 13, p.Resumable ? Theme.Green : Theme.Muted));
        var card = Ui.Card(facts.Panel);
        card.Margin = new Thickness(0, 12, 0, 0);
        _body.Children.Add(card);

        AddWhere();
        _body.Children.Add(CompactLabel(L.T("الرابط")));
        string shown;
        try { shown = Uri.UnescapeDataString(p.FinalUrl); } catch (Exception) { shown = p.FinalUrl; }
        var url = Ui.Input(shown);
        url.IsReadOnly = true;
        url.Foreground = Theme.Muted;
        url.FlowDirection = FlowDirection.LeftToRight;
        _body.Children.Add(url);

        _start.IsEnabled = _later.IsEnabled = true;
        _start.Focus();
        UpdateSpace();
    }

    // ---------- a video / audio page ----------

    private void ShowMedia()
    {
        var m = _media!;
        _body.Children.Clear();
        SetHead(_s.CategoryNamed("فيديو") ?? _category, L.T("تحميل فيديو"), (m.Site is { Length: > 0 } site && site != "Generic" ? site + " — " : "") + Host(m.Url));

        // thumbnail + title
        var top = new DockPanel();
        if (!string.IsNullOrEmpty(m.Thumbnail))
        {
            try
            {
                var img = new Image { Width = 128, Height = 72, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri(m.Thumbnail)) };
                var frame = new Border { Child = img, CornerRadius = new CornerRadius(7), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0), Background = Theme.Card };
                DockPanel.SetDock(frame, Dock.Left);
                top.Children.Add(frame);
            }
            catch (Exception) { }
        }
        var titleBox = new StackPanel();
        titleBox.Children.Add(Ui.Text(m.Title, 13, Theme.Ink, bold: true, wrap: TextWrapping.Wrap));
        if (m.Duration > 0) titleBox.Children.Add(Ui.Text(L.T("المدة: ") + Fmt.Duration(m.Duration), 12, Theme.Muted));
        top.Children.Add(titleBox);
        _body.Children.Add(top);

        // qualities: one drop-down — the chosen quality shows, the rest open in a list under it
        _body.Children.Add(CompactLabel(L.T("الجودة")));
        var defaultOption = m.Options.FirstOrDefault(o => !o.AudioOnly && o.Height is > 0 and <= 1080 && o.Ext == "mp4")
            ?? m.Options.FirstOrDefault(o => !o.AudioOnly) ?? m.Options[0];

        UIElement OptionRow(MediaOption o, bool header)
        {
            var row = new DockPanel { LastChildFill = true };
            if (header)
            {
                var chevron = Ui.Glyph("\uE70D", 12, Theme.Muted);
                chevron.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(chevron, Dock.Right);
                row.Children.Add(chevron);
            }
            var size = o.Size > 0 ? Ui.Ltr(o.SizeText, 13, Theme.Gold, bold: true) : Ui.Text(o.SizeText, 12.5, Theme.Muted);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            var g = Ui.Glyph(o.AudioOnly ? "\uE8D6" : "\uE714", 15, o.AudioOnly ? Theme.Br("#A86BF0") : Theme.Br("#E5533D"));
            g.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(g, Dock.Left);
            row.Children.Add(g);
            row.Children.Add(o.AudioOnly ? Ui.Text(o.Label, 13, Theme.Ink) : Ui.Ltr(o.Label, 13, Theme.Ink));
            return row;
        }

        var picker = new ToggleButton
        {
            Style = Theme.Button, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(9, 6, 9, 6), Cursor = System.Windows.Input.Cursors.Hand,
        };
        var popupList = new StackPanel { Margin = new Thickness(6) };
        var popup = new Popup
        {
            PlacementTarget = picker, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, FlowDirection = L.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Child = new Border
            {
                Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 4, 0, 12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.25 },
                Child = new ScrollViewer { Content = popupList, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            },
        };
        picker.SizeChanged += (_, _) => popup.MinWidth = popup.MaxWidth = picker.ActualWidth;
        picker.Checked += (_, _) => popup.IsOpen = true;
        picker.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) => picker.IsChecked = false;

        void Choose(MediaOption o)
        {
            picker.Content = OptionRow(o, header: true);
            foreach (var child in popupList.Children)
                if (child is RadioButton r) r.IsChecked = r.Tag == o;
            SelectOption(o);
        }

        var addedAudioHeader = false;
        var addedVideoHeader = false;
        foreach (var o in m.Options)
        {
            if (!o.AudioOnly && !addedVideoHeader)
            {
                addedVideoHeader = true;
                var vh = Ui.Text(L.T("فيديو"), 11.5, Theme.Muted);
                vh.Margin = new Thickness(8, 4, 8, 4);
                popupList.Children.Add(vh);
            }
            if (o.AudioOnly && !addedAudioHeader)
            {
                addedAudioHeader = true;
                var ah = Ui.Text(L.T("صوت فقط"), 11.5, Theme.Muted);
                ah.Margin = new Thickness(8, 8, 8, 4);
                popupList.Children.Add(ah);
            }
            var rb = new RadioButton
            {
                Style = Theme.Choice, Content = OptionRow(o, header: false), GroupName = "q" + GetHashCode(), Tag = o,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 5, 8, 5),
            };
            rb.Click += (_, _) => { Choose((MediaOption)rb.Tag); popup.IsOpen = false; };
            rb.MouseDoubleClick += (_, _) => { if (_start.IsEnabled) Finish(true); };
            popupList.Children.Add(rb);
        }
        _body.Children.Add(picker);
        Choose(defaultOption);

        _body.Children.Add(CompactLabel(L.T("اسم الملف")));
        _body.Children.Add(_name);
        AddWhere();

        _start.IsEnabled = _later.IsEnabled = true;
        _start.Focus();
    }

    private void SelectOption(MediaOption o)
    {
        _option = o;
        var stem = Path.GetFileNameWithoutExtension(_name.Text);
        if (string.IsNullOrWhiteSpace(stem) || _media is not null && _name.Text.Length == 0) stem = FileNames.Clean(_media?.Title, "video");
        _name.Text = stem + "." + o.Ext;
        var cat = o.AudioOnly ? _s.CategoryNamed("صوتيات") : _s.CategoryNamed("فيديو");
        if (cat is not null) SelectCategory(cat);
        UpdateSpace();
    }

    // ---------- where to save ----------

    private void AddWhere()
    {
        _body.Children.Add(CompactLabel(L.T("النوع (يحدد المجلد)")));
        _categories.Children.Clear();
        foreach (var c in _s.Categories)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Ui.Glyph(c.Icon, 13, Theme.Br(c.Color)));
            var t = Ui.Text(L.T(c.Name), 11.5);
            t.Margin = new Thickness(5, 0, 0, 0);
            sp.Children.Add(t);
            var chip = new RadioButton { Style = Theme.Choice, Content = sp, GroupName = "c" + GetHashCode(), Tag = c, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 4) };
            chip.Checked += (_, _) => SelectCategory((Category)chip.Tag, fromChip: true);
            _categories.Children.Add(chip);
        }
        _body.Children.Add(_categories);

        _body.Children.Add(CompactLabel(L.T("قائمة الانتظار")));
        _queue.ItemsSource = _s.Queues.Select(q => q.Name).ToList();
        _queue.SelectedItem = _s.Queues.Any(q => q.Name == _req.QueueName) ? _req.QueueName : _s.Queues.FirstOrDefault()?.Name;
        var queueRow = new DockPanel();
        var newQueue = Ui.Button(L.T("قائمة جديدة"), "", Theme.Button, CreateQueue);
        newQueue.Padding = new Thickness(7, 3, 7, 3);
        newQueue.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(newQueue, Dock.Left);
        queueRow.Children.Add(newQueue);
        queueRow.Children.Add(_queue);
        _body.Children.Add(queueRow);

        _body.Children.Add(CompactLabel(L.T("حفظ في")));
        var browse = Ui.Button(L.T("تغيير…"), "", Theme.Button, Browse);
        browse.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        _folder.FlowDirection = FlowDirection.LeftToRight;
        row.Children.Add(_folder);
        _body.Children.Add(row);
        _body.Children.Add(_remember);
        var free = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        _freeValue.Margin = new Thickness(6, 0, 0, 0);
        free.Children.Add(_freeLabel);
        free.Children.Add(_freeValue);
        _body.Children.Add(free);
        _body.Children.Add(_space);
        SelectCategory(_category);
    }

    private void SelectCategory(Category c, bool fromChip = false)
    {
        _category = c;
        foreach (RadioButton chip in _categories.Children)
            if (chip.Tag == c && chip.IsChecked != true) chip.IsChecked = true;
        if (!_folderEdited || fromChip)
        {
            _folder.Text = _s.FolderFor(c);
            _folderEdited = false;
        }
        SetHeadIcon(c);
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("اختر مجلد الحفظ") };
        try { Directory.CreateDirectory(_folder.Text); dlg.InitialDirectory = _folder.Text; } catch (Exception) { }
        if (dlg.ShowDialog(this) == true)
        {
            _folder.Text = dlg.FolderName;
            _folderEdited = true;
        }
    }

    private void CreateQueue()
    {
        var dialog = new Window
        {
            Title = L.T("قائمة انتظار جديدة"), Width = 340, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
        };
        Ui.Setup(dialog, L.T("قائمة انتظار جديدة"), 340, 140);
        var input = Ui.Input();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(CompactLabel(L.T("اسم القائمة")));
        panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var create = Ui.Button(L.T("إنشاء"), "", Theme.PrimaryButton, () =>
        {
            var name = input.Text.Trim();
            if (name.Length == 0) { input.Focus(); return; }
            IbdmApp.Instance.Manager.CreateQueue(name);
            dialog.DialogResult = true;
        });
        var cancel = Ui.Button(L.T("إلغاء"), style: Theme.Button, click: dialog.Close);
        cancel.Margin = new Thickness(7, 0, 0, 0);
        actions.Children.Add(create);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => input.Focus();
        if (dialog.ShowDialog() == true)
        {
            var selected = input.Text.Trim();
            _queue.ItemsSource = _s.Queues.Select(q => q.Name).ToList();
            _queue.SelectedItem = _s.Queues.FirstOrDefault(q => q.Name == selected)?.Name ?? _s.Queues.LastOrDefault()?.Name;
        }
    }

    private static TextBlock CompactLabel(string text)
    {
        var label = (TextBlock)Ui.Label(text);
        label.Margin = new Thickness(0, 6, 0, 2);
        return label;
    }

    private void UpdateSpace()
    {
        if (_folder.Text.Length == 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_folder.Text));
            var free = root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
            var need = _option?.Size ?? (_probe?.Size > 0 ? _probe.Size : _req.Size);
            var path = Path.Combine(_folder.Text, FileNames.Clean(_name.Text));
            var exists = File.Exists(path) ? L.F("\nيوجد ملف بنفس الاسم — سيتم الحفظ باسم «{0}»", Path.GetFileName(FileNames.Unique(path))) : "";
            var low = free >= 0 && need > 0 && free < need;
            _freeLabel.Text = free < 0 ? "" : low ? L.T("⚠ المساحة غير كافية — المتوفر:") : L.T("المساحة المتوفرة على القرص:");
            _freeValue.Text = free < 0 ? "" : Fmt.Size(free) + (low ? "  /  " + Fmt.Size(need).TrimStart('\u200E') : "");
            _freeLabel.Foreground = _freeValue.Foreground = low ? Theme.Red : Theme.Muted;
            _space.Text = exists.Trim();
            _space.Foreground = Theme.Gold;
            _space.Visibility = exists.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception) { _space.Text = ""; _freeLabel.Text = _freeValue.Text = ""; }
    }

    // ---------- start ----------

    private void Finish(bool startNow)
    {
        var name = FileNames.Clean(_name.Text.Trim());
        if (name.Length == 0) { _name.Focus(); return; }
        var folder = _folder.Text.Trim();
        try
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) { Ui.Message(this, L.T("المجلد غير صالح"), e.Message); return; }

        if (_remember.IsChecked == true)
        {
            _category.Folder = folder;
            _s.Save();
        }

        DownloadItem item;
        var queueName = _queue.SelectedItem as string ?? Settings.DefaultQueueName;
        if (_media is not null && _option is not null)
        {
            var o = _option;
            if (Path.GetExtension(name).TrimStart('.') != o.Ext) name = Path.GetFileNameWithoutExtension(name) + "." + o.Ext;
            var unique = Path.GetFileName(FileNames.Unique(Path.Combine(folder, name)));
            item = new DownloadItem
            {
                Kind = DownloadKind.Media, Url = _media.Url, PageUrl = _req.PageUrl ?? _media.Url, Request = _req.ToRequestInfo(),
                FileName = unique, Folder = folder, Category = _category.Name, QueueName = queueName, TotalBytes = o.Size,
                FormatSpec = o.FormatSpec, AudioOnly = o.AudioOnly, AudioFormat = o.AudioFormat, Quality = o.Label,
            };
        }
        else if (_probe is not null)
        {
            item = new DownloadItem
            {
                Kind = DownloadKind.File, Url = _probe.FinalUrl, PageUrl = _req.PageUrl, Request = _req.ToRequestInfo(),
                FileName = name, Folder = folder, Category = _category.Name, QueueName = queueName,
                TotalBytes = _probe.Size, Resumable = _probe.Resumable,
            };
        }
        else return;

        IbdmApp.Instance.Manager.Add(item, startNow);
        Close();
    }

    // ---------- helpers ----------

    private void SetHead(Category c, string title, string sub)
    {
        _headTitle.Text = title;
        _headSub.Text = sub;
        SetHeadIcon(c);
    }

    private void SetHeadIcon(Category c)
    {
        _headIcon.Text = c.Icon;
        _headIcon.Foreground = Theme.Br(c.Color);
        var col = (Color)ColorConverter.ConvertFromString(c.Color);
        var b = new SolidColorBrush(Color.FromArgb(0x30, col.R, col.G, col.B));
        b.Freeze();
        _headTile.Background = b;
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host; } catch (Exception) { return url; }
    }

    private string TypeName(string name, string? mime)
    {
        var ext = FileNames.ExtOf(name).ToUpperInvariant();
        var cat = _s.CategoryFor(name).Name;
        return ext.Length > 0 ? $"{ext} — {cat}" : (mime ?? cat);
    }

    /// <summary>Three label/value pairs side by side.</summary>
    private sealed class UniformGrid3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Rows = 1 };

        public void Add(string label, UIElement value)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(label, 11.5, Theme.Muted));
            if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 8, 0);
            sp.Children.Add(value);
            Panel.Children.Add(sp);
        }
    }
}


