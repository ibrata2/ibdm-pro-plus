using System.Globalization;

namespace IbdmPro.Core;

/// <summary>The app's language. Every text is written in Arabic in the code; in English mode it is looked up in
/// <see cref="English"/> (Strings.cs). A text without a translation stays Arabic rather than disappearing.</summary>
public static partial class L
{
    /// <summary>"ar" (default) or "en". Set once at start from the settings; changing it needs a restart.</summary>
    public static string Lang { get; set; } = "ar";
    public static bool En => Lang == "en";
    public static bool Rtl => !En;

    /// <summary>The product name shown to the user (the app sets it at start). Texts are written with "IBDM Pro";
    /// it is replaced here, except where the text names the downloads folder, which keeps its name.</summary>
    public static string Brand { get; set; } = "IBDM Pro";

    public static string T(string arabic)
    {
        var text = En && English.TryGetValue(arabic, out var e) ? e : arabic;
        if (Brand == "IBDM Pro" || !text.Contains("IBDM Pro")) return text;
        if (text.Contains("مجلد IBDM Pro") || text.Contains("IBDM Pro folder")) return text;
        return text.Replace("IBDM Pro", Brand);
    }

    public static string F(string arabic, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(arabic), args);

    /// <summary>For the tests: every Arabic text that has a translation.</summary>
    public static int Count => English.Count;
}
