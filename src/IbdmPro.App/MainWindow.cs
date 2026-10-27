using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IbdmPro.Core;

namespace IbdmPro.App;

/// <summary>One row of the download list.</summary>
public sealed class Row : INotifyPropertyChanged
{
    public DownloadItem Item { get; }
    public Row(DownloadItem item) { Item = item; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private Category Cat => Settings.Current.CategoryNamed(Item.Category) ?? Settings.Current.CategoryFor(Item.FileName);
    public string Name => Item.FileName;
    public FlowDirection NameFlow => Ui.DirectionOf(Item.FileName);
    public string Glyph => Cat.Icon;
    public Brush GlyphBrush => Theme.Br(Cat.Color);
    public string Size => Item.TotalBytes > 0 ? Fmt.Size(Item.TotalBytes) : (Item.Downloaded > 0 ? Fmt.Size(Item.Downloaded) : "—");
    public double Percent => Item.Percent;
    public string PercentText => Item.Status == DownloadStatus.Completed ? "100%" : Item.TotalBytes > 0 ? $"{Item.Percent:0.#}%" : (Item.Downloaded > 0 ? Fmt.Size(Item.Downloaded) : "");
    public string Speed => Item.Status == DownloadStatus.Downloading ? Fmt.Speed(Item.Speed) : "";
    public string Eta => Item.Status == DownloadStatus.Downloading && Item.TotalBytes > 0 ? Fmt.Eta(Item.TotalBytes - Item.Downloaded, Item.Speed) : "";
    public string Status => Item.Status == DownloadStatus.Error ? L.T("خطأ — ") + Item.Error
        : Item.Status == DownloadStatus.Completed ? Item.StatusText
        : Item.TotalBytes > 0 && Item.Downloaded > 0 ? $"{Item.StatusText} — {Item.Percent:0.#}%" : Item.StatusText;
    public string Category => L.T(Item.Category);
    public string Description => Item.Description ?? Item.Quality ?? "";
    public Brush StatusBrush => Item.Status switch
    {
        DownloadStatus.Completed => Theme.Green,
        DownloadStatus.Error => Theme.Red,
        DownloadStatus.Downloading or DownloadStatus.Processing => Theme.Gold,
        _ => Theme.Muted,
    };
    public Brush BarBrush => Item.Status switch
    {
        DownloadStatus.Completed => Theme.Green,
        DownloadStatus.Error => Theme.Red,
        DownloadStatus.Paused => Theme.Muted,
        _ => Theme.ProgressFill,
    };
    public bool Indeterminate => Item.Status == DownloadStatus.Processing || (Item.Status == DownloadStatus.Downloading && Item.TotalBytes <= 0);
    public string Date => (Item.Finished ?? Item.Added).ToString("yyyy/MM/dd HH:mm");
    // which row buttons show
    public Visibility PauseVisible => Item.IsActive || Item.Status == DownloadStatus.Queued ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeVisible => Item.Status is DownloadStatus.Paused or DownloadStatus.Error ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisible => Item.Status == DownloadStatus.Completed ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class MainWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly ObservableCollection<Row> _rows = [];
    private readonly ListView _list = new();
    private readonly ICollectionView _view;
    private readonly TextBox _search = Ui.Input();
    private readonly StackPanel _nav = new() { Margin = new Thickness(10, 12, 10, 12) };
    private readonly Dictionary<string, TextBlock> _counts = [];
    private readonly TextBlock _empty = Ui.Text(L.T("لا توجد تحميلات هنا بعد.\nاضغط «إضافة رابط» أو حمّل أي ملف من المتصفح وسيلتقطه IBDM Pro."), 14, Theme.Muted, wrap: TextWrapping.Wrap);
    private readonly TextBlock _extStatus = Ui.Text("", 12, Theme.Muted);
    private readonly TextBlock _extDot = Ui.Glyph("", 10, Theme.Muted);
    private readonly TextBlock _speed = Ui.Text("", 12, Theme.Gold);
    private readonly TextBlock _speedValue = Ui.Ltr("", 12, Theme.Gold, bold: true);
    private readonly TextBlock _detail = Ui.Text("", 12, Theme.Muted);

    private readonly Border _updateBanner = new() { Visibility = Visibility.Collapsed };

    /// <summary>Marks the toolbar's update button and shows a banner when a newer version was found in the background.</summary>
    public void ShowUpdateBadge(string version)
    {
        var row = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
        var close = Ui.Button("", "\uE711", Theme.ToolButton, () => _updateBanner.Visibility = Visibility.Collapsed);
        close.Padding = new Thickness(8, 4, 8, 4);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        var go = Ui.Button(L.T("تحديث الآن"), "\uE896", Theme.PrimaryButton, () => Updater.ShowWindow(this));
        go.Padding = new Thickness(14, 5, 14, 5);
        go.Margin = new Thickness(8, 0, 8, 0);
        DockPanel.SetDock(go, Dock.Right);
        row.Children.Add(go);
        var icon = Ui.Glyph("\uE895", 16, Theme.Gold);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(Ui.Text(L.F("يتوفر إصدار جديد من IBDM Pro: {0}", version), 13, Theme.Ink, bold: true));
        _updateBanner.Child = row;
        _updateBanner.Background = Theme.GoldSoft;
        _updateBanner.BorderBrush = Theme.Gold;
        _updateBanner.BorderThickness = new Thickness(0, 0, 0, 1);
        _updateBanner.Visibility = Visibility.Visible;
    }
    private string _filter = "all";
    private string _sortBy = "date";
    private bool _sortDesc = true;

    public MainWindow(DownloadManager manager)
    {
        _manager = manager;
        Ui.Setup(this, $"IBDM Pro {Program.Version}", 1280, 720);
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => Matches((Row)o);

        var root = new DockPanel();
        var toolbar = Toolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_updateBanner, Dock.Top);
        root.Children.Add(_updateBanner);
        var status = StatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var sidebar = new Border { Width = 180, Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        DockPanel.SetDock(sidebar, Dock.Left);
        root.Children.Add(sidebar);
        BuildNav();

        var detailBar = new Border { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 6, 14, 6), Child = _detail };
        DockPanel.SetDock(detailBar, Dock.Bottom);
        root.Children.Add(detailBar);

        BuildList();
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.IsHitTestVisible = false;
        var center = new Grid();
        center.Children.Add(_list);
        center.Children.Add(_empty);
        root.Children.Add(center);
        Content = root;

        _manager.ListChanged += () => Dispatcher.BeginInvoke(Sync);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Sync();
        timer.Start();
        Sync();

        Closing += OnClosing;
        PreviewKeyDown += OnKey;
        Settings.Changed += () => Dispatcher.BeginInvoke(() => { BuildNav(); Sync(); });
    }

    // ---------- layout ----------

    private UIElement Toolbar()
    {
        MenuItem Command(string title, Action action)
        {
            var item = new MenuItem { Header = L.T(title), Padding = new Thickness(9, 4, 9, 4) };
            item.Click += (_, _) => action();
            return item;
        }

        var menus = new Menu { Height = 29, Background = Theme.Panel, FlowDirection = FlowDirection.LeftToRight, Padding = new Thickness(4, 0, 4, 0) };
        var tasks = new MenuItem { Header = L.T("المهام") };
        tasks.Items.Add(Command("تحميل جديد", AddUrl));
        tasks.Items.Add(Command("قائمة انتظار جديدة…", CreateQueue));
        tasks.Items.Add(new Separator());
        tasks.Items.Add(Command("خروج", Close));
        var file = new MenuItem { Header = L.T("ملف") };
        file.Items.Add(Command("فتح المجلد", OpenSelectedFolder));
        file.Items.Add(Command("الإعدادات", OpenSettings));
        var downloads = new MenuItem { Header = L.T("التحميل") };
        downloads.Items.Add(Command("استئناف", () => ForSelected(i => _manager.Resume(i))));
        downloads.Items.Add(Command("إيقاف", () => ForSelected(i => _manager.Pause(i))));
        downloads.Items.Add(Command("إيقاف الكل", _manager.PauseAll));
        downloads.Items.Add(new Separator());
        downloads.Items.Add(Command("حذف", Delete));
        downloads.Items.Add(Command("حذف المكتملة", DeleteCompleted));
        downloads.Items.Add(Command("نقل إلى قائمة", () =>
        {
            var menu = new ContextMenu();
            foreach (var q in Settings.Current.Queues)
            {
                var target = new MenuItem { Header = q.Name };
                target.Click += (_, _) => { _manager.MoveToQueue(Selected(), q.Name); Sync(); };
                menu.Items.Add(target);
            }
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }));
        var view = new MenuItem { Header = L.T("عرض") };
        view.Items.Add(Command("كل التحميلات", () => SetFilter("all")));
        view.Items.Add(Command("قيد التحميل", () => SetFilter("active")));
        view.Items.Add(Command("غير مكتملة", () => SetFilter("unfinished")));
        view.Items.Add(Command("مكتملة", () => SetFilter("done")));
        view.Items.Add(new Separator());
        foreach (var c in Settings.Current.Categories) view.Items.Add(Command(c.Name, () => SetFilter("cat:" + c.Name)));
        foreach (var q in Settings.Current.Queues) view.Items.Add(Command(q.Name, () => SetFilter("queue:" + q.Name)));
        var help = new MenuItem { Header = L.T("تعليمات") };
        help.Items.Add(Command("الإضافة", () => ExtensionGuide.Show(this)));
        help.Items.Add(Command("التحديثات", () => Updater.ShowWindow(this)));
        help.Items.Add(Command("حول", () => new AboutWindow { Owner = this }.ShowDialog()));
        var registration = new MenuItem { Header = L.T("التسجيل") };
        registration.Click += (_, _) => Ui.Open(Updater.Page);
        menus.Items.Add(tasks);
        menus.Items.Add(file);
        menus.Items.Add(downloads);
        menus.Items.Add(view);
        menus.Items.Add(help);
        menus.Items.Add(registration);

        var tools = new DockPanel { Height = 72, LastChildFill = false, Background = Theme.Panel, FlowDirection = FlowDirection.LeftToRight };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), FlowDirection = FlowDirection.LeftToRight };
        buttons.Children.Add(Ui.Tool(L.T("إضافة رابط"), "", AddUrl));
        buttons.Children.Add(Ui.Tool(L.T("استئناف"), "", () => ForSelected(i => _manager.Resume(i)), Theme.Green));
        buttons.Children.Add(Ui.Tool(L.T("إيقاف"), "", () => ForSelected(i => _manager.Pause(i)), Theme.Ink));
        buttons.Children.Add(Ui.Tool(L.T("إيقاف الكل"), "", _manager.PauseAll, Theme.Ink));
        buttons.Children.Add(Ui.Tool(L.T("حذف"), "", Delete, Theme.Red));
        buttons.Children.Add(Ui.Tool(L.T("حذف المكتملة"), "", DeleteCompleted, Theme.Red));
        buttons.Children.Add(Separator());
        buttons.Children.Add(Ui.Tool(L.T("خيارات"), "", OpenSettings, Theme.Ink));
        buttons.Children.Add(QueueTools());
        var searchBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0) };
        searchBox.Children.Add(_search);
        _search.Width = 190;
        _search.ToolTip = L.T("بحث");
        _search.FlowDirection = FlowDirection.RightToLeft;
        _search.TextChanged += (_, _) => _view.Refresh();
        DockPanel.SetDock(searchBox, Dock.Right);
        tools.Children.Add(searchBox);
        DockPanel.SetDock(buttons, Dock.Left);
        tools.Children.Add(buttons);

        var layout = new StackPanel { Background = Theme.Panel };
        layout.Children.Add(menus);
        layout.Children.Add(new Border { Height = 1, Background = Theme.Line });
        layout.Children.Add(tools);
        return new Border { Child = layout, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private UIElement QueueTools()
    {
        var menu = new ContextMenu { FlowDirection = L.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        var button = Ui.Tool(L.T("قوائم الانتظار"), "", () => { RefreshQueueMenu(menu); menu.IsOpen = true; });
        button.ContextMenu = menu;
        button.ContextMenuOpening += (_, _) => RefreshQueueMenu(menu);
        return button;
    }

    private void RefreshQueueMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var startAll = new MenuItem { Header = L.T("بدء كل القوائم") };
        startAll.Click += (_, _) => { foreach (var q in Settings.Current.Queues) _manager.SetQueueRunning(q.Name, true); };
        menu.Items.Add(startAll);
        var stopAll = new MenuItem { Header = L.T("إيقاف كل القوائم") };
        stopAll.Click += (_, _) => { foreach (var q in Settings.Current.Queues) _manager.SetQueueRunning(q.Name, false); };
        menu.Items.Add(stopAll);
        menu.Items.Add(new Separator());
        foreach (var q in Settings.Current.Queues)
        {
            var parent = new MenuItem { Header = q.Name };
            var start = new MenuItem { Header = L.T("بدء القائمة") };
            start.Click += (_, _) => _manager.SetQueueRunning(q.Name, true);
            var stop = new MenuItem { Header = L.T("إيقاف القائمة") };
            stop.Click += (_, _) => _manager.SetQueueRunning(q.Name, false);
            parent.Items.Add(start);
            parent.Items.Add(stop);
            menu.Items.Add(parent);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = L.T("قائمة انتظار جديدة…") };
        create.Click += (_, _) => CreateQueue();
        menu.Items.Add(create);
    }

    private static UIElement Separator() => new Border { Width = 1, Height = 44, Background = Theme.Line, Margin = new Thickness(6, 10, 6, 10), VerticalAlignment = VerticalAlignment.Center };

    private void SetFilter(string filter)
    {
        _filter = filter;
        foreach (var button in _nav.Children.OfType<RadioButton>())
            if (button.Tag?.ToString() == filter) { button.IsChecked = true; break; }
        _view.Refresh();
        UpdateEmpty();
    }

    private UIElement StatusBar()
    {
        var bar = new DockPanel { Background = Theme.Panel, LastChildFill = false };
        var credit = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 5, 14, 5) };
        credit.Children.Add(Ui.Text(L.T("برمجة و تطوير "), 12, Theme.Muted));
        credit.Children.Add(Ui.Text(L.T("ابراهيم الخزرجي"), 12, Theme.Gold, bold: true));
        DockPanel.SetDock(credit, Dock.Left);
        bar.Children.Add(credit);

        var ext = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 5, 14, 5), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        _extDot.Margin = new Thickness(0, 0, 6, 0);
        ext.Children.Add(_extDot);
        ext.Children.Add(_extStatus);
        ext.MouseLeftButtonUp += (_, _) => ExtensionGuide.Show(this);
        DockPanel.SetDock(ext, Dock.Right);
        bar.Children.Add(ext);
        var speed = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 5, 14, 5) };
        _speedValue.Margin = new Thickness(8, 0, 0, 0);
        speed.Children.Add(_speed);
        speed.Children.Add(_speedValue);
        DockPanel.SetDock(speed, Dock.Right);
        bar.Children.Add(speed);
        return new Border { Child = bar, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        _counts.Clear();
        _nav.Children.Add(NavItem("all", L.T("كل التحميلات"), "", Theme.Gold));
        var h = Ui.Text(L.T("الفئات"), 11.5, Theme.Muted);
        h.Margin = new Thickness(10, 14, 0, 6);
        _nav.Children.Add(h);
        foreach (var c in Settings.Current.Categories) _nav.Children.Add(NavItem("cat:" + c.Name, L.T(c.Name), c.Icon, Theme.Br(c.Color)));
        var queuesHeader = Ui.Text(L.T("قوائم الانتظار"), 11.5, Theme.Muted);
        queuesHeader.Margin = new Thickness(10, 14, 0, 6);
        _nav.Children.Add(queuesHeader);
        foreach (var q in Settings.Current.Queues)
        {
            var queueRow = (FrameworkElement)NavItem("queue:" + q.Name, q.Name, q.IsRunning ? "" : "", q.IsRunning ? Theme.Green : Theme.Muted);
            var menu = new ContextMenu();
            var start = new MenuItem { Header = L.T("بدء القائمة") };
            start.Click += (_, _) => _manager.SetQueueRunning(q.Name, true);
            var stop = new MenuItem { Header = L.T("إيقاف القائمة") };
            stop.Click += (_, _) => _manager.SetQueueRunning(q.Name, false);
            menu.Items.Add(start);
            menu.Items.Add(stop);
            queueRow.ContextMenu = menu;
            _nav.Children.Add(queueRow);
        }
        var newQueue = Ui.Button(L.T("قائمة انتظار جديدة"), "", Theme.Button, CreateQueue);
        newQueue.Margin = new Thickness(0, 6, 0, 0);
        newQueue.FontSize = 12;
        _nav.Children.Add(newQueue);
        var openRoot = Ui.Button(L.T("فتح مجلد IBDM Pro"), "", Theme.Button, () => { Directory.CreateDirectory(Settings.Current.DownloadRoot); Ui.Open(Settings.Current.DownloadRoot); });
        openRoot.Margin = new Thickness(0, 16, 0, 0);
        openRoot.FontSize = 12;
        openRoot.Padding = new Thickness(8, 6, 8, 6);
        _nav.Children.Add(openRoot);
    }

    private UIElement NavItem(string key, string text, string glyph, Brush color)
    {
        var row = new DockPanel();
        var g = Ui.Glyph(glyph, 14, color);
        g.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(g, Dock.Left);
        row.Children.Add(g);
        var count = Ui.Text("", 11.5, Theme.Muted);
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(count);
        row.Children.Add(Ui.Text(text, 13));
        _counts[key] = count;
        var rb = new RadioButton { Style = Theme.Nav, Content = row, Tag = key, GroupName = "nav", IsChecked = key == _filter, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        rb.Checked += (_, _) => { _filter = key; _view.Refresh(); UpdateEmpty(); };
        return rb;
    }

    private void BuildList()
    {
        _list.Background = Theme.Bg;
        _list.Foreground = Theme.Ink;
        _list.FlowDirection = FlowDirection.LeftToRight;
        _list.BorderThickness = new Thickness(0);
        _list.ItemContainerStyle = Theme.Row;
        _list.ItemsSource = _view;
        _list.SelectionMode = SelectionMode.Extended;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        var grid = new GridView { ColumnHeaderContainerStyle = Theme.Header, AllowsColumnReorder = false };

        // name with its type icon
        var name = new FrameworkElementFactory(typeof(DockPanel));
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.FontFamilyProperty, Theme.Icons);
        icon.SetValue(TextBlock.FontSizeProperty, 16.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 10, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Glyph)));
        icon.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Row.GlyphBrush)));
        name.AppendChild(icon);
        var nameText = TextCell(nameof(Row.Name), null, 13.5);
        nameText.SetBinding(FrameworkElement.FlowDirectionProperty, new Binding(nameof(Row.NameFlow)));
        nameText.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        name.AppendChild(nameText);
        grid.Columns.Add(Column(L.T("اسم الملف"), 300, name, "name"));
        grid.Columns.Add(Column(L.T("الفئة"), 78, TextCell(nameof(Row.Category), Theme.Muted, 12), "category"));
        grid.Columns.Add(Column(L.T("الحجم"), 88, TextCell(nameof(Row.Size), ltr: true), "size"));
        grid.Columns.Add(Column(L.T("الحالة"), 110, TextCell(nameof(Row.Status), null, 12, nameof(Row.StatusBrush)), "status"));
        grid.Columns.Add(Column(L.T("الوقت المتبقي"), 110, TextCell(nameof(Row.Eta), ltr: true), "eta"));
        grid.Columns.Add(Column(L.T("معدل النقل"), 94, TextCell(nameof(Row.Speed), Theme.Gold, ltr: true), "speed"));
        grid.Columns.Add(Column(L.T("آخر تاريخ"), 120, TextCell(nameof(Row.Date), Theme.Muted, 12, ltr: true), "date"));
        grid.Columns.Add(Column(L.T("الوصف"), 155, TextCell(nameof(Row.Description), Theme.Muted, 12), "description"));
        _list.View = grid;

        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListViewItem { DataContext: Row r })
            {
                if (r.Item.Status == DownloadStatus.Completed) OpenFile(r.Item);
                else ProgressWindow.Open(r.Item, _manager);
            }
        };
        _list.SelectionChanged += (_, _) => UpdateDetail();
        var menu = new ContextMenu { FlowDirection = L.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        _list.ContextMenu = menu;
        _list.ContextMenuOpening += (_, e) => { if (Selected().Count == 0) e.Handled = true; else FillMenu(menu); };
    }

    /// <summary>A small square icon button in a list row; <paramref name="visibleWhen"/> names a Row property.</summary>
    private FrameworkElementFactory RowButton(string glyph, string tip, string? visibleWhen, Brush color, Action<Row> act)
    {
        var b = new FrameworkElementFactory(typeof(Button));
        b.SetValue(FrameworkElement.StyleProperty, Theme.Button);
        b.SetValue(Control.PaddingProperty, new Thickness(0));
        b.SetValue(FrameworkElement.WidthProperty, 28.0);
        b.SetValue(FrameworkElement.HeightProperty, 26.0);
        b.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
        b.SetValue(FrameworkElement.ToolTipProperty, tip);
        if (visibleWhen is not null) b.SetBinding(UIElement.VisibilityProperty, new Binding(visibleWhen));
        var g = new FrameworkElementFactory(typeof(TextBlock));
        g.SetValue(TextBlock.TextProperty, glyph);
        g.SetValue(TextBlock.FontFamilyProperty, Theme.Icons);
        g.SetValue(TextBlock.FontSizeProperty, 12.0);
        g.SetValue(TextBlock.ForegroundProperty, color);
        g.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
        b.AppendChild(g);
        b.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
        {
            e.Handled = true;
            if (s is FrameworkElement { DataContext: Row r }) { act(r); Sync(); }
        }));
        return b;
    }

    private GridViewColumn Column(string header, double width, FrameworkElementFactory cell, string sortKey)
    {
        var h = new GridViewColumnHeader { Content = header, Tag = sortKey };
        h.Click += (_, _) =>
        {
            _sortDesc = _sortBy == sortKey ? !_sortDesc : sortKey is "date" or "size";
            _sortBy = sortKey;
            Resort();
        };
        return new GridViewColumn { Header = h, Width = width, CellTemplate = new DataTemplate { VisualTree = cell } };
    }

    private static FrameworkElementFactory TextCell(string path, Brush? fg = null, double size = 13, string? brushPath = null, bool ltr = false)
    {
        var t = new FrameworkElementFactory(typeof(TextBlock));
        if (ltr)
        {
            t.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
            t.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        }
        t.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        t.SetValue(TextBlock.FontSizeProperty, size);
        t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        t.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        if (brushPath is not null) t.SetBinding(TextBlock.ForegroundProperty, new Binding(brushPath));
        else if (fg is not null) t.SetValue(TextBlock.ForegroundProperty, fg);
        t.SetBinding(FrameworkElement.ToolTipProperty, new Binding(path) { Mode = BindingMode.OneWay });
        return t;
    }

    private void FillMenu(ContextMenu m)
    {
        m.Items.Clear();
        void Add(string text, Action a) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        var one = Selected().FirstOrDefault();
        if (one?.Status == DownloadStatus.Completed) Add(L.T("فتح الملف"), OpenSelectedFile);
        Add(L.T("فتح المجلد"), OpenSelectedFolder);
        if (one is not null && one.Status != DownloadStatus.Completed) Add(L.T("عرض نافذة التقدّم"), () => ProgressWindow.Open(one, _manager));
        m.Items.Add(new Separator());
        Add(L.T("استئناف"), () => ForSelected(i => _manager.Resume(i)));
        Add(L.T("إيقاف"), () => ForSelected(i => _manager.Pause(i)));
        Add(L.T("إعادة التحميل من البداية"), () => ForSelected(i => _manager.Redownload(i)));
        var move = new MenuItem { Header = L.T("نقل إلى قائمة") };
        foreach (var q in Settings.Current.Queues)
        {
            var target = new MenuItem { Header = q.Name };
            target.Click += (_, _) => { _manager.MoveToQueue(Selected(), q.Name); Sync(); };
            move.Items.Add(target);
        }
        m.Items.Add(move);
        m.Items.Add(new Separator());
        Add(L.T("نسخ الرابط"), () => { if (one is not null) Clipboard.SetText(one.PageUrl ?? one.Url); });
        m.Items.Add(new Separator());
        Add(L.T("حذف…"), Delete);
    }

    // ---------- keeping the list current ----------

    /// <summary>Shows and selects one download in the list (used by "into the app" in the progress window).</summary>
    public void Select(DownloadItem item)
    {
        Sync();
        var row = _rows.FirstOrDefault(r => r.Item == item);
        if (row is null) return;
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
        _list.Focus();
    }

    public void Sync()
    {
        var items = _manager.Items;
        var ids = items.Select(i => i.Id).ToHashSet();
        for (var i = _rows.Count - 1; i >= 0; i--) if (!ids.Contains(_rows[i].Item.Id)) _rows.RemoveAt(i);
        var have = _rows.Select(r => r.Item.Id).ToHashSet();
        var added = false;
        foreach (var item in items.Where(i => !have.Contains(i.Id)).Reverse()) { _rows.Insert(0, new Row(item)); added = true; }
        foreach (var r in _rows) r.Refresh();
        if (added) Resort();

        Count("all", items.Count);
        Count("active", items.Count(i => i.IsActive || i.Status == DownloadStatus.Queued));
        Count("unfinished", items.Count(i => i.Status != DownloadStatus.Completed));
        Count("done", items.Count(i => i.Status == DownloadStatus.Completed));
        foreach (var c in Settings.Current.Categories) Count("cat:" + c.Name, items.Count(i => i.Category == c.Name));
        foreach (var q in Settings.Current.Queues) Count("queue:" + q.Name, items.Count(i => i.QueueName == q.Name));

        var speed = _manager.TotalSpeed;
        var active = items.Count(i => i.IsActive);
        _speed.Text = active > 0 ? L.F("⬇ {0} قيد التحميل", active) : "";
        _speedValue.Text = active > 0 ? Fmt.Speed(speed) : "";
        IbdmApp.Instance.Tray?.SetTooltip(active > 0 ? L.F("IBDM Pro — {0} قيد التحميل {1}", active, Fmt.Speed(speed)) : "IBDM Pro");
        Title = active > 0 && items.FirstOrDefault(i => i.Status == DownloadStatus.Downloading) is { TotalBytes: > 0 } a
            ? $"{a.Percent:0}% — IBDM Pro {Program.Version}" : $"IBDM Pro {Program.Version}";

        var server = IbdmApp.Instance.Server;
        if (server is null) { _extStatus.Text = L.T("خادم الإضافة متوقف"); _extDot.Foreground = Theme.Red; }
        else if ((DateTime.Now - server.LastExtensionContact).TotalMinutes < 3) { _extStatus.Text = L.T("إضافة المتصفح متصلة"); _extDot.Foreground = Theme.Green; }
        else { _extStatus.Text = L.T("إضافة المتصفح غير متصلة — اضغط لتثبيتها"); _extDot.Foreground = Theme.Muted; }

        UpdateEmpty();
        UpdateDetail();
    }

    private void Count(string key, int n)
    {
        if (_counts.TryGetValue(key, out var t)) t.Text = n > 0 ? n.ToString() : "";
    }

    private void UpdateEmpty() => _empty.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateDetail()
    {
        var sel = Selected();
        if (sel.Count != 1) { _detail.Text = sel.Count > 1 ? L.F("تم تحديد {0} تحميلات", sel.Count) : L.T("IBDM Pro — مدير التحميل"); return; }
        var i = sel[0];
        var where = i.Status == DownloadStatus.Completed ? i.FullPath : i.Folder;
        var q = i.Quality is not null ? L.F("الجودة: {0}   •   ", i.Quality) : "";
        var conn = i.Kind == DownloadKind.File && i.Resumable && i.Status == DownloadStatus.Downloading ? L.F("الاتصالات: {0}   •   ", i.Segments.Count(s => !s.Finished)) : "";
        _detail.Text = L.F("{0}{1}المكان: {2}   •   الرابط: {3}", q, conn, where, i.PageUrl ?? i.Url) + (i.Error is { Length: > 0 } e && i.Status == DownloadStatus.Error ? $"   •   {e}" : "");
    }

    private bool Matches(Row r)
    {
        var i = r.Item;
        var ok = _filter switch
        {
            "all" => true,
            "active" => i.IsActive || i.Status == DownloadStatus.Queued,
            "unfinished" => i.Status != DownloadStatus.Completed,
            "done" => i.Status == DownloadStatus.Completed,
            _ when _filter.StartsWith("cat:") => i.Category == _filter[4..],
            _ when _filter.StartsWith("queue:") => i.QueueName == _filter[6..],
            _ => true,
        };
        var q = _search.Text.Trim();
        return ok && (q.Length == 0 || i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Resort()
    {
        _view.SortDescriptions.Clear();
        var prop = _sortBy switch
        {
            "name" => "Item.FileName",
            "category" => "Item.Category",
            "size" => "Item.TotalBytes",
            "description" => "Item.Description",
            "progress" => "Percent",
            "speed" => "Item.Speed",
            "status" => "Item.Status",
            _ => "Item.Added",
        };
        _view.SortDescriptions.Add(new SortDescription(prop, _sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    // ---------- actions ----------

    private List<DownloadItem> Selected() => _list.SelectedItems.Cast<Row>().Select(r => r.Item).ToList();

    private void ForSelected(Action<DownloadItem> a)
    {
        var sel = Selected();
        if (sel.Count == 0) { Ui.Message(this, L.T("لم يتم تحديد تحميل"), L.T("اختر تحميلاً من القائمة أولاً.")); return; }
        foreach (var i in sel) a(i);
        Sync();
    }

    public void AddUrl()
    {
        var url = AddUrlDialog.Ask(this);
        if (url is null) return;
        Capture.Handle(new CaptureRequest { Kind = "link", Url = url, QueueName = CurrentQueueName() });
    }

    private string CurrentQueueName() => _filter.StartsWith("queue:") ? _filter[6..] : Settings.Current.Queues.FirstOrDefault(q => q.IsRunning)?.Name ?? Settings.DefaultQueueName;

    private void CreateQueue()
    {
        var w = new Window { Title = L.T("قائمة انتظار جديدة"), Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
        Ui.Setup(w, L.T("قائمة انتظار جديدة"), 380, 150);
        var input = Ui.Input();
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Ui.Label(L.T("اسم القائمة")));
        root.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var save = Ui.Button(L.T("إنشاء"), "", Theme.PrimaryButton, () => { if (!string.IsNullOrWhiteSpace(input.Text)) { _manager.CreateQueue(input.Text); w.Close(); } });
        var cancel = Ui.Button(L.T("إلغاء"), style: Theme.Button, click: w.Close);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        root.Children.Add(actions);
        w.Content = root;
        w.Loaded += (_, _) => input.Focus();
        w.ShowDialog();
    }

    public void OpenSettings()
    {
        var w = new SettingsWindow(_manager) { Owner = this };
        w.ShowDialog();
    }

    private void OpenSelectedFile()
    {
        var i = Selected().FirstOrDefault();
        if (i is null) return;
        if (i.Status != DownloadStatus.Completed) { Ui.Message(this, L.T("الملف غير مكتمل"), L.T("انتظر حتى يكتمل التحميل.")); return; }
        OpenFile(i);
    }

    private void OpenFile(DownloadItem i)
    {
        if (File.Exists(i.FullPath)) Ui.Open(i.FullPath);
        else Ui.Message(this, L.T("الملف غير موجود"), L.F("ربما تم نقله أو حذفه:\n{0}", i.FullPath));
    }

    private void OpenSelectedFolder()
    {
        var i = Selected().FirstOrDefault();
        if (i is null) { Directory.CreateDirectory(Settings.Current.DownloadRoot); Ui.Open(Settings.Current.DownloadRoot); return; }
        if (i.Status == DownloadStatus.Completed) Ui.ShowInFolder(i.FullPath);
        else { Directory.CreateDirectory(i.Folder); Ui.Open(i.Folder); }
    }

    private async void Delete()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"«{sel[0].FileName}»" : L.F("{0} تحميلات", sel.Count);
        var anyDone = sel.Any(i => i.Status == DownloadStatus.Completed);
        var choice = anyDone
            ? Ui.Ask(this, L.T("حذف"), L.F("هل تريد حذف {0} من القائمة؟", what), L.T("حذف من القائمة فقط"), L.T("حذف مع الملف من الجهاز"), L.T("إلغاء"))
            : Ui.Ask(this, L.T("حذف"), L.F("هل تريد حذف {0}؟ سيتم حذف الجزء غير المكتمل.", what), L.T("حذف"), L.T("إلغاء")) switch { 0 => 0, _ => 2 };
        if (choice is < 0 or 2) return;
        foreach (var i in sel) await _manager.RemoveAsync(i, deleteFile: choice == 1);
        Sync();
    }

    /// <summary>Clears every finished download from the list (the files stay on the computer unless asked).</summary>
    private async void DeleteCompleted()
    {
        var done = _manager.Items.Where(i => i.Status == DownloadStatus.Completed).ToList();
        if (done.Count == 0) { Ui.Message(this, L.T("حذف المكتملة"), L.T("لا توجد تحميلات مكتملة في القائمة.")); return; }
        var choice = Ui.Ask(this, L.T("حذف المكتملة"), L.F("هل تريد حذف {0} من القائمة؟", L.F("{0} تحميلات مكتملة", done.Count)),
            L.T("حذف من القائمة فقط"), L.T("حذف مع الملف من الجهاز"), L.T("إلغاء"));
        if (choice is < 0 or 2) return;
        foreach (var i in done) await _manager.RemoveAsync(i, deleteFile: choice == 1);
        Sync();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _search.IsKeyboardFocused == false) { Delete(); e.Handled = true; }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { AddUrl(); e.Handled = true; }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !_search.IsKeyboardFocused)
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http"))
            {
                Capture.Handle(new CaptureRequest { Kind = "link", Url = text });
                e.Handled = true;
            }
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !_search.IsKeyboardFocused) { _list.SelectAll(); e.Handled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IbdmApp.Instance.Quitting) return;
        if (Settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!Settings.Current.HasSeenTrayHint)
            {
                Settings.Current.HasSeenTrayHint = true;
                Settings.Current.Save();
                IbdmApp.Instance.Tray?.Notify(L.T("IBDM Pro يعمل في الخلفية"), L.T("البرنامج بجانب الساعة حتى يلتقط التحميلات من المتصفح. للخروج: زر يمين على الأيقونة ← خروج."), null);
            }
        }
        else
        {
            e.Cancel = true;
            IbdmApp.Instance.Quit();
        }
    }
}


