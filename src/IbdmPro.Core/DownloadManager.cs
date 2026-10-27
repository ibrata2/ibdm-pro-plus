namespace IbdmPro.Core;

/// <summary>Owns the download list: starts, pauses, resumes and removes downloads, keeps at most
/// <see cref="Settings.MaxParallel"/> running, measures speeds and saves the list.</summary>
public sealed class DownloadManager : IDisposable
{
    public Store Store { get; }
    public SpeedLimiter Limiter { get; } = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly Dictionary<string, (long Bytes, long Tick)> _samples = [];
    private readonly Timer _timer;
    private bool _dirty;
    private volatile bool _closing;
    private readonly Func<Settings> _settings;

    /// <summary>Raised on worker threads; the UI marshals to its own thread.</summary>
    public event Action<DownloadItem>? ItemCompleted;
    public event Action<DownloadItem>? ItemFailed;
    public event Action? ListChanged;
    /// <summary>A download the person started (new, resumed or restarted) — the app shows its progress window.</summary>
    public event Action<DownloadItem>? UserStarted;

    public DownloadManager(Store? store = null, Func<Settings>? settings = null)
    {
        _settings = settings ?? (() => Settings.Current);
        Store = store ?? new Store();
        foreach (var item in Store.Items)
        {
            if (string.IsNullOrWhiteSpace(item.QueueName)) item.QueueName = Settings.DefaultQueueName;
            item.Speed = 0;
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Processing or DownloadStatus.Queued)
                item.Status = _settings().ResumeOnStart ? DownloadStatus.Queued : DownloadStatus.Paused;
        }
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public IReadOnlyList<DownloadItem> Items { get { lock (Store.Items) return [.. Store.Items]; } }

    public void CreateQueue(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _settings().Queues.Any(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _settings().Queues.Add(new Settings.DownloadQueue { Name = name, IsRunning = false });
        _settings().Save();
    }

    public void SetQueueRunning(string name, bool running)
    {
        var queue = _settings().Queues.FirstOrDefault(q => q.Name == name);
        if (queue is null) return;
        queue.IsRunning = running;
        if (!running)
            foreach (var item in Items.Where(i => i.QueueName == name && (i.IsActive || i.Status == DownloadStatus.Queued))) Pause(item);
        else
        {
            foreach (var item in Items.Where(i => i.QueueName == name && i.Status == DownloadStatus.Paused)) item.Status = DownloadStatus.Queued;
            Pump();
        }
        Save();
        ListChanged?.Invoke();
    }

    public void MoveToQueue(IEnumerable<DownloadItem> items, string name)
    {
        if (!_settings().Queues.Any(q => q.Name == name)) return;
        foreach (var item in items)
        {
            var wasRunning = IsRunning(item);
            item.QueueName = name;
            if (wasRunning && !_settings().Queues.First(q => q.Name == name).IsRunning) Pause(item);
            if (item.Status == DownloadStatus.Paused && _settings().Queues.First(q => q.Name == name).IsRunning) item.Status = DownloadStatus.Queued;
        }
        Save();
        Pump();
        ListChanged?.Invoke();
    }

    public void Begin() => Pump();

    public void Add(DownloadItem item, bool start)
    {
        item.Status = start ? DownloadStatus.Queued : DownloadStatus.Paused;
        lock (Store.Items) Store.Items.Insert(0, item);
        Save();
        ListChanged?.Invoke();
        if (start && _settings().Queues.Any(q => q.Name == item.QueueName && q.IsRunning))
        {
            Start(item);
            UserStarted?.Invoke(item);
        }
    }

    /// <summary>Starts now, even if the parallel limit is reached (the person asked for this one).</summary>
    public void Start(DownloadItem item)
    {
        lock (_running)
        {
            if (item.Removed || _running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return;
            var cts = new CancellationTokenSource();
            _running[item.Id] = cts;
            item.Status = DownloadStatus.Downloading;
            item.Error = null;
            item.Phase = null;
            _ = Task.Run(() => Run(item, cts.Token));
        }
        ListChanged?.Invoke();
    }

    public void Resume(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Queued) { Start(item); UserStarted?.Invoke(item); }
    }

    public void Pause(DownloadItem item)
    {
        CancellationTokenSource? cts;
        lock (_running) _running.TryGetValue(item.Id, out cts);
        if (cts is not null) cts.Cancel();
        else if (item.Status == DownloadStatus.Queued) { item.Status = DownloadStatus.Paused; ListChanged?.Invoke(); }
    }

    public void PauseAll()
    {
        foreach (var i in Items.Where(i => i.IsActive || i.Status == DownloadStatus.Queued)) Pause(i);
    }

    public void ResumeAll()
    {
        foreach (var i in Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Error)) i.Status = DownloadStatus.Queued;
        Pump();
        ListChanged?.Invoke();
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFile)
    {
        // Out of the list straight away (never shown as "paused" in between), then stop it and clean up.
        item.Removed = true;
        lock (Store.Items) Store.Items.Remove(item);
        Save();
        ListChanged?.Invoke();
        Pause(item);
        for (var i = 0; i < 100 && IsRunning(item); i++) await Task.Delay(100);
        try
        {
            if (File.Exists(item.PartPath)) File.Delete(item.PartPath);
            if (deleteFile && item.Status == DownloadStatus.Completed && File.Exists(item.FullPath)) File.Delete(item.FullPath);
            if (item.Kind == DownloadKind.Media && item.Status != DownloadStatus.Completed)
                foreach (var f in Directory.Exists(item.Folder) ? Directory.GetFiles(item.Folder, Path.GetFileNameWithoutExtension(item.FileName) + ".*") : [])
                    if (f.EndsWith(".part") || f.EndsWith(".ytdl") || f.Contains(".part-Frag")) File.Delete(f);
        }
        catch (Exception e) { Log.Write($"remove: {e.Message}"); }
        Save();
        ListChanged?.Invoke();
    }

    public void Redownload(DownloadItem item)
    {
        Pause(item);
        lock (item.Gate) item.Segments.Clear();
        item.Downloaded = 0;
        item.Status = DownloadStatus.Queued;
        try { if (File.Exists(item.PartPath)) File.Delete(item.PartPath); } catch (Exception) { }
        Start(item);
        UserStarted?.Invoke(item);
    }

    public bool IsRunning(DownloadItem item) { lock (_running) return _running.ContainsKey(item.Id); }

    private void Pump()
    {
        var s = _settings();
        int active;
        lock (_running) active = _running.Count;
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Queued && _settings().Queues.Any(q => q.Name == i.QueueName && q.IsRunning)).OrderBy(i => i.Added))
        {
            if (active >= s.MaxParallel) break;
            Start(item);
            active++;
        }
    }

    private async Task Run(DownloadItem item, CancellationToken ct)
    {
        var s = _settings();
        Limiter.BytesPerSecond = s.SpeedLimitKBps * 1024L;
        try
        {
            if (item.Kind == DownloadKind.File)
            {
                await HttpDownloader.RunAsync(item, s.Connections, Limiter, ct);
                var final = FileNames.Unique(item.FullPath);
                File.Move(item.PartPath, final);
                item.FileName = Path.GetFileName(final);
            }
            else
            {
                var path = await YtDlp.DownloadAsync(item, s.Connections, Limiter.BytesPerSecond, p =>
                {
                    if (p.Phase is not null) { item.Status = DownloadStatus.Processing; item.Phase = p.Phase; return; }
                    item.Status = DownloadStatus.Downloading;
                    item.Downloaded = p.Downloaded;
                    if (p.Total > item.TotalBytes) item.TotalBytes = p.Total;
                }, ct);
                item.Folder = Path.GetDirectoryName(path) ?? item.Folder;
                item.FileName = Path.GetFileName(path);
                item.TotalBytes = new FileInfo(path).Length;
                item.Downloaded = item.TotalBytes;
            }
            item.Status = DownloadStatus.Completed;
            item.Finished = DateTime.Now;
            item.Speed = 0;
            lock (item.Gate) item.Segments.Clear();
            ItemCompleted?.Invoke(item);
        }
        catch (OperationCanceledException) when (item.Removed)
        {
            item.Speed = 0;
        }
        catch (OperationCanceledException)
        {
            item.Status = _closing && _settings().ResumeOnStart ? DownloadStatus.Queued : DownloadStatus.Paused;
            item.Speed = 0;
        }
        catch (Exception e)
        {
            Log.Write($"{item.FileName}: {e}");
            item.Status = DownloadStatus.Error;
            item.Error = e is HttpRequestException h ? L.T("خطأ في الاتصال: ") + (h.InnerException?.Message ?? h.Message) : e.Message;
            item.Speed = 0;
            ItemFailed?.Invoke(item);
        }
        finally
        {
            lock (_running) _running.Remove(item.Id);
            Save();
            ListChanged?.Invoke();
            Pump();
        }
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        foreach (var item in Items)
        {
            if (item.Status != DownloadStatus.Downloading) { _samples.Remove(item.Id); continue; }
            var bytes = item.Downloaded;
            if (_samples.TryGetValue(item.Id, out var last) && now > last.Tick)
            {
                var instant = Math.Max(0, (bytes - last.Bytes) * 1000.0 / (now - last.Tick));
                item.Speed = item.Kind == DownloadKind.Media && item.Speed == 0 ? instant : item.Speed * 0.6 + instant * 0.4;
            }
            _samples[item.Id] = (bytes, now);
            _dirty = true;
        }
        if (_dirty) { _dirty = false; Store.Save(); }
    }

    public double TotalSpeed => Items.Where(i => i.Status == DownloadStatus.Downloading).Sum(i => i.Speed);

    public void Save() { _dirty = false; Store.Save(); }

    public void Dispose()
    {
        _closing = true;
        _timer.Dispose();
        lock (_running) foreach (var c in _running.Values) c.Cancel();
        Store.Save();
    }
}


