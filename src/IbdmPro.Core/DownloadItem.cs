using System.Text.Json;
using System.Text.Json.Serialization;

namespace IbdmPro.Core;

public enum DownloadStatus { Queued, Downloading, Processing, Paused, Completed, Error }

public enum DownloadKind { File, Media }

/// <summary>A byte range fetched over its own connection. <see cref="End"/> is inclusive and can shrink while running
/// when the range is split to give a finished connection more work.</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Done { get; set; }
    [JsonIgnore] public long Position => Start + Done;
    [JsonIgnore] public long Remaining => End - Position + 1;
    [JsonIgnore] public bool Finished => Position > End;
}

/// <summary>Request headers copied from the browser so protected downloads behave as they did there.</summary>
public sealed class RequestInfo
{
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DownloadKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string? PageUrl { get; set; }
    public RequestInfo Request { get; set; } = new();

    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>The named waiting list this download belongs to.</summary>
    public string QueueName { get; set; } = Settings.DefaultQueueName;
    public string? Description { get; set; }

    public long TotalBytes { get; set; } = -1;
    private long _downloaded;
    public long Downloaded { get => Interlocked.Read(ref _downloaded); set => Interlocked.Exchange(ref _downloaded, value); }
    public void AddDownloaded(long n) => Interlocked.Add(ref _downloaded, n);

    public bool Resumable { get; set; }
    public List<Segment> Segments { get; set; } = [];
    public int Connections { get; set; }

    // Video/audio through yt-dlp
    public string? FormatSpec { get; set; }
    public bool AudioOnly { get; set; }
    public string? AudioFormat { get; set; }
    public string? Quality { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }
    public string? Phase { get; set; }
    /// <summary>Cancelled by the person: gone from the list and can never be started again.</summary>
    [JsonIgnore] public bool Removed { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }

    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public object Gate { get; } = new();

    [JsonIgnore] public string FullPath => Path.Combine(Folder, FileName);
    [JsonIgnore] public string PartPath => FullPath + ".ibdmpart";
    [JsonIgnore] public double Percent => TotalBytes > 0 ? Math.Min(100, Downloaded * 100.0 / TotalBytes) : (Status == DownloadStatus.Completed ? 100 : 0);
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Downloading or DownloadStatus.Processing;

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => L.T("بالانتظار"),
        DownloadStatus.Downloading => L.T("جاري التحميل"),
        DownloadStatus.Processing => Phase ?? L.T("جاري المعالجة"),
        DownloadStatus.Paused => L.T("متوقف"),
        DownloadStatus.Completed => L.T("مكتمل"),
        DownloadStatus.Error => L.T("خطأ"),
        _ => "",
    };
}

/// <summary>The download list, saved as JSON in the app folder.</summary>
public sealed class Store
{
    private readonly string _file;
    private readonly object _gate = new();
    public List<DownloadItem> Items { get; } = [];

    public Store(string? file = null)
    {
        _file = file ?? Path.Combine(Paths.AppData, "downloads.json");
        try
        {
            if (File.Exists(_file))
            {
                var list = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(_file), Json.Options);
                if (list is not null) Items.AddRange(list);
            }
        }
        catch (Exception e)
        {
            Log.Write($"store load: {e.Message}");
            try { File.Copy(_file, _file + ".broken", true); } catch (Exception) { }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                List<DownloadItem> snapshot;
                lock (Items) snapshot = [.. Items];
                // Segments change on worker threads; take each item's lock while it is written.
                var json = JsonSerializer.Serialize(snapshot.Select(Snapshot).ToList(), Json.Options);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _file, true);
            }
            catch (Exception e) { Log.Write($"store save: {e.Message}"); }
        }
    }

    private static DownloadItem Snapshot(DownloadItem item)
    {
        lock (item.Gate)
        {
            var copy = (DownloadItem)typeof(DownloadItem).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(item, null)!;
            copy.Segments = item.Segments.Select(s => new Segment { Start = s.Start, End = s.End, Done = s.Done }).ToList();
            return copy;
        }
    }
}


