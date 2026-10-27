using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IbdmPro.App;

/// <summary>The IBDM Pro look: gold accents, Arabic, right to left — dark (black) or light (white) following
/// the Windows "app mode" (Settings › Personalization › Colors), unless chosen in IBDM Pro's settings.</summary>
public static class Theme
{
    /// <summary>The experimental "glass" style: white frosted glass with red glass accents, like the app icon.</summary>
    public static readonly bool IsGlass = IbdmPro.Core.Settings.Current.Theme == "glass";
    public static readonly bool IsDark = !IsGlass && ReadDark();
    private static string P(string dark, string light, string? glass = null) => IsGlass ? glass ?? light : IsDark ? dark : light;

    public static readonly Brush Bg = IsGlass ? Grad("#FBFCFE", "#E8EBF1") : Br(P("#0F0F11", "#F3F3F5"));
    public static readonly Brush Panel = IsGlass ? Grad("#F2FFFFFF", "#D9F4F6FA") : Br(P("#16161A", "#FAFAFB"));
    public static readonly Brush Card = IsGlass ? Grad("#F7FFFFFF", "#E6F7F8FB") : Br(P("#1E1E23", "#FFFFFF"));
    public static readonly Brush Hover = Br(P("#2A2A31", "#ECECF0", "#14D7141F"));
    public static readonly Brush Pressed = Br(P("#33333B", "#E1E1E7", "#26D7141F"));
    public static readonly Brush Line = Br(P("#2C2C33", "#DCDCE2", "#D6DAE2"));
    public static readonly Brush Gold = Br(P("#D4AF37", "#A87F0A", "#D7141F"));
    public static readonly Brush GoldHover = IsGlass ? Grad("#F0434C", "#C40F18") : Br(P("#E2C25A", "#B98E17"));
    public static readonly Brush GoldPressed = Br(P("#B8962A", "#8F6C08", "#A30C14"));
    public static readonly Brush GoldSoft = Br(P("#33D4AF37", "#26C49A1C", "#1CD7141F"));
    public static readonly Brush OnGold = Br(P("#111111", "#FFFFFF", "#FFFFFF"));
    public static readonly Brush Ink = Br(P("#F2F2F4", "#1B1B1F", "#16161A"));
    public static readonly Brush Muted = Br(P("#9C9CA6", "#6B6B75", "#6A6E78"));
    public static readonly Brush Red = Br(P("#E5533D", "#C93B26", "#C8121C"));
    public static readonly Brush RedSoft = Br(P("#33E5533D", "#22C93B26", "#1FC8121C"));
    public static readonly Brush Green = Br(P("#4CC26B", "#23914A", "#1E8E46"));
    public static readonly Brush Blue = Br(P("#3B9BF0", "#1F7BD0", "#1F7BD0"));
    public static readonly Brush Track = IsGlass ? Grad("#D9DDE4", "#EEF0F4") : Br(P("#2A2A30", "#E3E3E9"));
    public static readonly Brush BoxLine = Br(P("#55555F", "#9A9AA5", "#A8ADB8"));
    /// <summary>The primary (accent) button face: red glass in the glass style.</summary>
    public static readonly Brush AccentFace = IsGlass ? Grad("#F2363F", "#B80D16") : Gold;
    /// <summary>The progress fill: glossy red glass in the glass style.</summary>
    public static readonly Brush ProgressFill = IsGlass ? Grad("#FF5A62", "#D7141F", "#9E0A12") : Gold;

    public static Brush Grad(string top, string bottom, string? end = null)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(top), 0));
        if (end is null) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1));
        else
        {
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 0.55));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(end), 1));
        }
        g.Freeze();
        return g;
    }

    /// <summary>A plain colour for places that cannot take a gradient (the title bar).</summary>
    public static Color Solid(Brush b) => b is SolidColorBrush sc ? sc.Color : b is GradientBrush gb && gb.GradientStops.Count > 0 ? gb.GradientStops[0].Color : Colors.White;

    /// <summary>"auto" follows Windows; "dark" or "light" if chosen in the settings.</summary>
    private static bool ReadDark()
    {
        var mode = IbdmPro.Core.Settings.Current.Theme;
        if (mode == "dark") return true;
        if (mode == "light") return false;
        return WindowsIsDark();
    }

    public static bool WindowsIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch (Exception) { return true; }
    }

    public static readonly FontFamily Font = new("Segoe UI, Tahoma");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Brush Br(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // ---------- styles built in code (no XAML) ----------

    public static readonly Style Button = MakeButton(Card, Hover, Pressed, Ink, Line);
    public static readonly Style PrimaryButton = MakeButton(AccentFace, GoldHover, GoldPressed, OnGold, IsGlass ? Br("#9E0A12") : Gold);
    public static readonly Style DangerButton = MakeButton(RedSoft, Hover, Pressed, Red, RedSoft);
    public static readonly Style ToolButton = MakeButton(Brushes.Transparent, Hover, Pressed, Ink, Brushes.Transparent);
    public static readonly Style Choice = MakeChoice();
    public static readonly Style Nav = MakeNav();
    public static readonly Style Progress = MakeProgress();
    public static readonly Style Row = MakeRow();
    public static readonly Style Header = MakeHeader();
    public static readonly Style Input = MakeInput();
    public static readonly Style CheckBox = MakeCheck();

    /// <summary>A gold tick in a dark box (the default box draws a mirrored tick in right-to-left windows).</summary>
    private static Style MakeCheck()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(System.Windows.Controls.Panel.BackgroundProperty, Brushes.Transparent);
        var box = new FrameworkElementFactory(typeof(Border), "Box");
        box.SetValue(FrameworkElement.WidthProperty, 18.0);
        box.SetValue(FrameworkElement.HeightProperty, 18.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        box.SetValue(Border.BorderBrushProperty, BoxLine);
        box.SetValue(Border.BackgroundProperty, Card);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 0));
        box.SetValue(DockPanel.DockProperty, Dock.Left);
        var mark = new FrameworkElementFactory(typeof(TextBlock), "Mark");
        mark.SetValue(TextBlock.TextProperty, "\uE73E");
        mark.SetValue(TextBlock.FontFamilyProperty, Icons);
        mark.SetValue(TextBlock.FontSizeProperty, 12.0);
        mark.SetValue(TextBlock.ForegroundProperty, OnGold);
        mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(mark);
        root.AppendChild(box);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(cp);
        var t = new ControlTemplate(typeof(CheckBox)) { VisualTree = root };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BorderBrushProperty, Gold, "Box") } });
        t.Triggers.Add(new Trigger
        {
            Property = ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Mark"), new Setter(Border.BackgroundProperty, Gold, "Box"), new Setter(Border.BorderBrushProperty, Gold, "Box") },
        });
        t.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.45) } });
        var s = new Style(typeof(CheckBox));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        return s;
    }

    private static Style MakeButton(Brush bg, Brush hover, Brush pressed, Brush fg, Brush border)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetBinding(Border.BackgroundProperty, TP("Background"));
        bd.SetBinding(Border.BorderBrushProperty, TP("BorderBrush"));
        bd.SetBinding(Border.BorderThicknessProperty, TP("BorderThickness"));
        bd.SetBinding(Border.PaddingProperty, TP("Padding"));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetBinding(FrameworkElement.HorizontalAlignmentProperty, TP("HorizontalContentAlignment"));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = bd };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, hover, "Bd") } });
        t.Triggers.Add(new Trigger { Property = ButtonBase.IsPressedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, pressed, "Bd") } });
        t.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.4) } });
        var s = new Style(typeof(ButtonBase));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        s.Setters.Add(new Setter(Control.BackgroundProperty, bg));
        s.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 16, 7)));
        s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        s.Setters.Add(new Setter(UIElement.FocusableProperty, true));
        return s;
    }

    /// <summary>A RadioButton drawn as a selectable card (qualities, categories).</summary>
    private static Style MakeChoice()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetBinding(Border.BackgroundProperty, TP("Background"));
        bd.SetBinding(Border.BorderBrushProperty, TP("BorderBrush"));
        bd.SetBinding(Border.PaddingProperty, TP("Padding"));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var t = new ControlTemplate(typeof(RadioButton)) { VisualTree = bd };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Hover, "Bd") } });
        t.Triggers.Add(new Trigger
        {
            Property = ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, Gold, "Bd"), new Setter(Border.BackgroundProperty, GoldSoft, "Bd") },
        });
        var s = new Style(typeof(RadioButton));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Card));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6)));
        return s;
    }

    /// <summary>Side navigation entry.</summary>
    private static Style MakeNav()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        bd.AppendChild(cp);
        var t = new ControlTemplate(typeof(RadioButton)) { VisualTree = bd };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Hover, "Bd") } });
        t.Triggers.Add(new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, GoldSoft, "Bd") } });
        var s = new Style(typeof(RadioButton));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        return s;
    }

    private static Style MakeProgress()
    {
        var radius = new CornerRadius(IsGlass ? 5 : 3);
        var grid = new FrameworkElementFactory(typeof(Grid));
        var track = new FrameworkElementFactory(typeof(Border), "PART_Track");
        track.SetValue(Border.CornerRadiusProperty, radius);
        track.SetValue(Border.BackgroundProperty, Track);
        if (IsGlass)
        {
            track.SetValue(Border.BorderBrushProperty, Br("#C9CED8"));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        }
        var ind = new FrameworkElementFactory(typeof(Grid), "PART_Indicator");
        ind.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        var fill = new FrameworkElementFactory(typeof(Border));
        fill.SetValue(Border.CornerRadiusProperty, radius);
        fill.SetBinding(Border.BackgroundProperty, TP("Foreground"));
        ind.AppendChild(fill);
        if (IsGlass)
        {
            // The shine on the top half that makes it look like red glass.
            var shine = new FrameworkElementFactory(typeof(Border));
            shine.SetValue(Border.CornerRadiusProperty, new CornerRadius(5, 5, 2, 2));
            shine.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            shine.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 1, 1, 0));
            shine.SetValue(Border.BackgroundProperty, Grad("#B3FFFFFF", "#14FFFFFF"));
            shine.SetValue(FrameworkElement.HeightProperty, 4.0);
            ind.AppendChild(shine);
        }
        grid.AppendChild(track);
        grid.AppendChild(ind);
        var s = new Style(typeof(ProgressBar));
        s.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ProgressBar)) { VisualTree = grid }));
        s.Setters.Add(new Setter(Control.ForegroundProperty, ProgressFill));
        s.Setters.Add(new Setter(FrameworkElement.HeightProperty, IsGlass ? 9.0 : 6.0));
        return s;
    }

    private static Style MakeRow()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.BorderBrushProperty, Line);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        bd.SetValue(Border.PaddingProperty, new Thickness(0, 2, 0, 2));
        var rp = new FrameworkElementFactory(typeof(GridViewRowPresenter));
        rp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(rp);
        var t = new ControlTemplate(typeof(ListViewItem)) { VisualTree = bd };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Card, "Bd") } });
        t.Triggers.Add(new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, GoldSoft, "Bd") } });
        var s = new Style(typeof(ListViewItem));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return s;
    }

    private static Style MakeHeader()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Panel);
        bd.SetValue(Border.BorderBrushProperty, Line);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        bd.SetValue(Border.PaddingProperty, new Thickness(7, 5, 7, 5));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        bd.AppendChild(cp);
        var t = new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = bd };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Card, "Bd") } });
        var s = new Style(typeof(GridViewColumnHeader));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
        s.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        return s;
    }

    private static Style MakeInput()
    {
        var s = new Style(typeof(TextBox));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Card));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        s.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Gold));
        s.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, Gold));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        return s;
    }

    private static Binding TP(string path) => new(path) { RelativeSource = RelativeSource.TemplatedParent };
}

/// <summary>Small builders so windows read like a description of their layout.</summary>
public static class Ui
{
    public static TextBlock Text(string s, double size = 13, Brush? fg = null, bool bold = false, TextWrapping wrap = TextWrapping.NoWrap) => new()
    {
        Text = s,
        FontSize = size,
        Foreground = fg ?? Theme.Ink,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap,
        TextTrimming = wrap == TextWrapping.NoWrap ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>English/number text (sizes, speeds, qualities) shown left-to-right inside the Arabic layout,
    /// so "94.8 MB" never turns into "MB 94.8".</summary>
    public static TextBlock Ltr(string s, double size = 13, Brush? fg = null, bool bold = false)
    {
        var t = Text(s, size, fg, bold);
        t.FlowDirection = FlowDirection.LeftToRight;
        t.HorizontalAlignment = HorizontalAlignment.Left; // = the right edge inside a right-to-left parent
        return t;
    }

    /// <summary>Left-to-right for text that starts with a Latin letter or digit (an English file name), else right-to-left.</summary>
    public static FlowDirection DirectionOf(string? s)
    {
        foreach (var c in s ?? "")
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC') return FlowDirection.RightToLeft;
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return L.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    public static TextBlock Glyph(string g, double size = 16, Brush? fg = null) => new()
    {
        Text = g,
        FontFamily = Theme.Icons,
        FontSize = size,
        Foreground = fg ?? Theme.Ink,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        FlowDirection = FlowDirection.LeftToRight,
    };

    public static Button Button(string text, string? glyph = null, Style? style = null, Action? click = null)
    {
        var b = new Button { Style = style ?? Theme.Button };
        if (glyph is null) b.Content = text;
        else
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Glyph(glyph, 14, null));
            ((TextBlock)sp.Children[0]).SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = b });
            var t = Text(text);
            t.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = b });
            t.Margin = new Thickness(8, 0, 0, 0);
            sp.Children.Add(t);
            b.Content = sp;
        }
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The big icon-over-text buttons of the main toolbar.</summary>
    public static Button Tool(string text, string glyph, Action click, Brush? color = null)
    {
        var sp = new StackPanel { MinWidth = 70 };
        sp.Children.Add(Glyph(glyph, 27, color ?? Theme.Gold));
        var t = Text(text, 11, Theme.Ink);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(t);
        var b = new Button { Style = Theme.ToolButton, Content = sp, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(1, 0, 1, 0), ToolTip = text };
        b.Click += (_, _) => click();
        return b;
    }

    public static TextBox Input(string text = "", bool multiline = false)
    {
        var t = new TextBox { Style = Theme.Input, Text = text };
        if (multiline)
        {
            t.AcceptsReturn = true;
            t.TextWrapping = TextWrapping.Wrap;
            t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            t.VerticalContentAlignment = VerticalAlignment.Top;
        }
        return t;
    }

    public static CheckBox Check(string text, bool value) => new()
    {
        Style = Theme.CheckBox,
        Content = Text(text, 13, Theme.Ink, wrap: TextWrapping.Wrap),
        IsChecked = value,
        Foreground = Theme.Ink,
        Margin = new Thickness(0, 4, 0, 4),
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = Cursors.Hand,
    };

    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Child = child,
        Background = Theme.Card,
        BorderBrush = Theme.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(14),
    };

    public static UIElement Label(string text) => new TextBlock
    {
        Text = text, FontSize = 12, Foreground = Theme.Muted, Margin = new Thickness(0, 10, 0, 4),
    };

    public static ImageSource AppIcon(int size = 256)
    {
        try
        {
            var uri = new Uri(size > 48 ? "pack://application:,,,/Assets/ibdm-256.png" : "pack://application:,,,/Assets/ibdm.ico");
            return BitmapFrame.Create(uri);
        }
        catch (Exception) { return new DrawingImage(); }
    }

    public static Image Logo(double size) => new() { Source = AppIcon(), Width = size, Height = size };

    /// <summary>Every window: dark, Arabic, right to left, dark title bar.</summary>
    public static void Setup(Window w, string title, double width, double height)
    {
        w.Title = title;
        w.Width = width;
        w.Height = height;
        w.Background = Theme.Bg;
        w.Foreground = Theme.Ink;
        w.FontFamily = Theme.Font;
        w.FontSize = 13;
        // The window stays left-to-right so Windows keeps minimize / maximize / close on the right in both
        // languages; its content (set after this) follows the language.
        w.FlowDirection = FlowDirection.LeftToRight;
        var contentDir = L.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Window))
            .AddValueChanged(w, (_, _) => { if (w.Content is FrameworkElement fe) fe.FlowDirection = contentDir; });
        w.Icon = AppIcon(32);
        w.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        w.SourceInitialized += (_, _) => DarkTitleBar(w);
    }

    public static void DarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int on = Theme.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            var bg = Theme.Solid(Theme.Bg);
            int caption = bg.R | bg.G << 8 | bg.B << 16; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
        }
        catch (Exception) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Brings a window to the front even when another program (the browser) has focus.</summary>
    public static void ForceFront(Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Topmost = true;
        w.Activate();
        w.Topmost = false;
        w.Focus();
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Message(null, L.T("تعذر الفتح"), e.Message); }
    }

    public static void ShowInFolder(string file)
    {
        try
        {
            if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (Directory.Exists(Path.GetDirectoryName(file))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(file)}\"");
            else Message(null, L.T("المجلد غير موجود"), Path.GetDirectoryName(file) ?? file);
        }
        catch (Exception e) { Message(null, L.T("تعذر فتح المجلد"), e.Message); }
    }

    /// <summary>A dark question box. Returns the index of the button pressed, or -1 if closed.</summary>
    public static int Ask(Window? owner, string title, string message, params string[] buttons)
    {
        var w = new Window { ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, ShowInTaskbar = owner is null };
        Setup(w, title, 440, 200);
        w.WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        if (owner is { IsVisible: true }) w.Owner = owner;
        var result = -1;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Text(title, 16, Theme.Ink, bold: true));
        var msg = Text(message, 13, Theme.Muted, wrap: TextWrapping.Wrap);
        msg.Margin = new Thickness(0, 10, 0, 18);
        root.Children.Add(msg);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = Button(buttons[i], style: i == 0 ? Theme.PrimaryButton : Theme.Button, click: () => { result = index; w.Close(); });
            b.Margin = new Thickness(0, 0, 8, 0);
            if (i == 0) b.IsDefault = true;
            if (i == buttons.Length - 1) b.IsCancel = true;
            row.Children.Add(b);
        }
        root.Children.Add(row);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    public static void Message(Window? owner, string title, string message) => Ask(owner, title, message, L.T("حسناً"));
}


