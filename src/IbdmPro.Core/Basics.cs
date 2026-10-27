using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace IbdmPro.Core;

/// <summary>Where the app keeps its own files and finds its bundled tools.</summary>
public static class Paths
{
    /// <summary>Tests point this somewhere else so they never touch the real settings.</summary>
    public static string? AppDataOverride { get; set; }

    public static string AppData
    {
        get
        {
            var dir = AppDataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IBDM Pro");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ToolsDir => Environment.GetEnvironmentVariable("IBDM_TOOLS") is { Length: > 0 } t ? t : Path.Combine(AppContext.BaseDirectory, "tools");
    public static string YtDlp => Tool("yt-dlp");
    public static string Ffmpeg => Tool("ffmpeg");
    /// <summary>JavaScript runtime yt-dlp needs for YouTube (without it YouTube gives "Requested format is not available").</summary>
    public static string Deno => Tool("deno");
    public static string ExtensionDir => Path.Combine(AppContext.BaseDirectory, "extension");

    private static string Tool(string name)
    {
        var local = Path.Combine(ToolsDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
        return File.Exists(local) ? local : name; // falls back to PATH (used by the Linux tests)
    }

    /// <summary>The user's real Downloads folder (it can be moved in Windows, so it is asked for, not guessed).</summary>
    public static string UserDownloads
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                    if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
                    {
                        var s = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch (Exception) { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Writes to a temporary file first so a crash never leaves a half-written file behind.</summary>
    public static void SaveAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(Paths.AppData, "log.txt");
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".old", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}

/// <summary>Numbers shown to people: sizes, speeds and times.</summary>
public static class Fmt
{
    public static string Size(long bytes)
    {
        if (bytes < 0) return L.T("غير معروف");
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        // U+200E keeps "461.01 MB" in this order inside Arabic (right-to-left) text.
        return "\u200E" + (i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}");
    }

    public static string Speed(double bps) => bps <= 0 ? "" : Size((long)bps) + "/s";

    public static string Eta(long remaining, double bps)
    {
        if (bps <= 1 || remaining <= 0) return "";
        var t = TimeSpan.FromSeconds(remaining / bps);
        if (t.TotalDays >= 1) return L.T("+يوم");
        return "\u200E" + (t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}");
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        return "\u200E" + (t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}");
    }
}

public static class FileNames
{
    private static readonly char[] Bad = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Makes any title or server-supplied name safe to use as a Windows file name.</summary>
    public static string Clean(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var chars = name.Select(c => c < 32 || Bad.Contains(c) ? ' ' : c).ToArray();
        var s = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (s.Length == 0) return fallback;
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(s))) s = "_" + s;
        if (s.Length > 180)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 12) ext = "";
            s = s[..(180 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return s;
    }

    /// <summary>"name.ext" → "name (2).ext", "name (3).ext"… until nothing on disk has that name.</summary>
    public static string Unique(string path, Func<string, bool>? taken = null)
    {
        taken ??= p => File.Exists(p) || Directory.Exists(p);
        if (!taken(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!taken(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string ExtOf(string name) => Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

    /// <summary>A sensible extension for a MIME type when the URL and server name have none.</summary>
    public static string? ExtForMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return null;
        mime = mime.Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "video/mp4" => "mp4", "video/webm" => "webm", "video/x-matroska" => "mkv", "video/quicktime" => "mov",
            "video/x-msvideo" => "avi", "video/x-flv" => "flv", "video/3gpp" => "3gp",
            "audio/mpeg" => "mp3", "audio/mp4" => "m4a", "audio/aac" => "aac", "audio/wav" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg", "audio/flac" => "flac", "audio/webm" => "weba",
            "application/zip" or "application/x-zip-compressed" => "zip", "application/x-rar-compressed" or "application/vnd.rar" => "rar",
            "application/x-7z-compressed" => "7z", "application/gzip" => "gz", "application/x-tar" => "tar",
            "application/pdf" => "pdf", "application/x-msdownload" or "application/x-msdos-program" or "application/vnd.microsoft.portable-executable" => "exe",
            "application/x-msi" or "application/x-ms-installer" => "msi", "application/vnd.android.package-archive" => "apk",
            "application/x-iso9660-image" => "iso",
            "image/jpeg" => "jpg", "image/png" => "png", "image/gif" => "gif", "image/webp" => "webp",
            "text/plain" => "txt", "application/msword" => "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
            _ => null,
        };
    }

    /// <summary>The last part of a URL's path, decoded — what most sites use as the file's name.</summary>
    public static string? FromUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            var last = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrEmpty(last)) return null;
            return Uri.UnescapeDataString(last);
        }
        catch (Exception) { return null; }
    }
}
