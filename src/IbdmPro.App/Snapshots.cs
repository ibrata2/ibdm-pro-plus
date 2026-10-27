using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IbdmPro.Core;

namespace IbdmPro.App;

/// <summary>Renders every window to PNG files with sample data (IBDMPro.exe --snapshot &lt;folder&gt;).
/// The build uses it to check the windows really open and to see how they look; it never touches real settings.</summary>
public static class Snapshots
{
    public static void Run(IbdmApp app, string dir)
    {
        Directory.CreateDirectory(dir);
        var m = app.Manager;
        var s = Settings.Current;
        DownloadItem Item(string name, long total, double pct, DownloadStatus st, double speed = 0, string? error = null, int minutesAgo = 5)
        {
            var cat = s.CategoryFor(name);
            var i = new DownloadItem
            {
                FileName = name, TotalBytes = total, Category = cat.Name, Folder = s.FolderFor(cat), Url = "https://example.com/" + Uri.EscapeDataString(name),
                Resumable = true, Added = DateTime.Now.AddMinutes(-minutesAgo),
            };
            m.Add(i, start: false);
            i.Status = st;
            i.Downloaded = (long)(total * pct);
            i.Speed = speed;
            i.Error = error;
            if (st == DownloadStatus.Completed) i.Finished = DateTime.Now.AddMinutes(-minutesAgo + 1);
            return i;
        }
        Item("Windows 11 ISO.iso", 5_400_000_000, 0.42, DownloadStatus.Downloading, 9_800_000, minutesAgo: 1);
        Item("أغنية جديدة.mp3", 8_200_000, 1, DownloadStatus.Completed, minutesAgo: 30);
        Item("فيديو تجربة — Full HD.mp4", 245_000_000, 0.73, DownloadStatus.Downloading, 4_100_000, minutesAgo: 2);
        Item("برنامج التصميم.zip", 1_250_000_000, 0.18, DownloadStatus.Paused, minutesAgo: 60);
        Item("التقرير السنوي.pdf", 3_400_000, 1, DownloadStatus.Completed, minutesAgo: 90);
        Item("setup-x64.exe", 92_000_000, 0.05, DownloadStatus.Error, error: "الخادم رفض الطلب (403 Forbidden)", minutesAgo: 120);
        Item("صورة الغلاف.png", 2_100_000, 1, DownloadStatus.Completed, minutesAgo: 200);

        app.Main.ShowUpdateBadge("1.0.99");
        app.Main.Sync();
        Save(app.Main, Path.Combine(dir, "1-main.png"));

        var probe = new ProbeResult { FinalUrl = "https://download.example.com/files/برنامج%20التصميم.zip", FileName = "برنامج التصميم.zip", Size = 1_250_000_000, Resumable = true, ContentType = "application/zip" };
        Save(InfoDialog.Preview(new CaptureRequest { Url = probe.FinalUrl }, probe, null), Path.Combine(dir, "2-file-info.png"));

        var media = YtDlp.Parse(SampleMedia, "https://www.youtube.com/watch?v=x");
        media.Thumbnail = null;
        Save(InfoDialog.Preview(new CaptureRequest { Kind = "media", Url = media.Url }, null, media), Path.Combine(dir, "3-video-qualities.png"));

        Save(new SettingsWindow(m), Path.Combine(dir, "4-settings.png"));

        // A download in progress over 8 connections, each at a different point.
        var running = m.Items.First(i => i.FileName.StartsWith("Windows"));
        running.Kind = DownloadKind.File;
        running.Url = "https://software.download.example.com/Windows11_24H2_Arabic_x64.iso";
        lock (running.Gate)
        {
            running.Segments.Clear();
            var part = running.TotalBytes / 8;
            double[] done = [1, 0.62, 0.48, 0.71, 0.35, 0.55, 0.2, 0.41];
            for (var k = 0; k < 8; k++)
            {
                var st = k * part;
                var en = k == 7 ? running.TotalBytes - 1 : st + part - 1;
                running.Segments.Add(new Segment { Start = st, End = en, Done = (long)((en - st + 1) * done[k]) });
            }
            running.Downloaded = running.Segments.Sum(x => Math.Min(x.Done, x.End - x.Start + 1));
        }
        Save(ProgressWindow.Preview(running, m), Path.Combine(dir, "6-progress.png"));
        Save(ExtensionGuide.Build(null), Path.Combine(dir, "7-extension-guide.png"));
        Save(new AboutWindow(), Path.Combine(dir, "5-about.png"));
    }

    private static void Save(Window w, string file)
    {
        try
        {
            var content = (FrameworkElement)w.Content;
            w.Content = null;
            var size = new Size(w.Width, double.IsNaN(w.Height) ? 900 : w.Height);
            var frame = new Border { Background = w.Background, Child = content, FlowDirection = w.FlowDirection, Width = size.Width };
            TextElement.SetFontFamily(frame, w.FontFamily);
            TextElement.SetFontSize(frame, w.FontSize);
            TextElement.SetForeground(frame, w.Foreground);
            frame.Measure(new Size(size.Width, double.PositiveInfinity));
            var h = w.SizeToContent == SizeToContent.Height ? Math.Min(frame.DesiredSize.Height, 1400) : size.Height;
            frame.Height = h;
            frame.Measure(new Size(size.Width, h));
            frame.Arrange(new Rect(0, 0, size.Width, h));
            frame.UpdateLayout();
            const double scale = 1.25;
            var bmp = new RenderTargetBitmap((int)(size.Width * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(frame);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create(file);
            enc.Save(f);
            Console.WriteLine($"snapshot {file}");
        }
        catch (Exception e)
        {
            File.WriteAllText(file + ".error.txt", e.ToString());
            Console.WriteLine($"snapshot failed {file}: {e}");
        }
    }

    private const string SampleMedia = """
    {"title":"فيديو تجربة | IBDM Pro","duration":212,"extractor_key":"Youtube","webpage_url":"https://www.youtube.com/watch?v=x","formats":[
     {"format_id":"140","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","abr":129,"filesize":3400000},
     {"format_id":"251","ext":"webm","vcodec":"none","acodec":"opus","abr":140,"filesize":3600000},
     {"format_id":"401","ext":"mp4","vcodec":"av01","acodec":"none","height":2160,"fps":30,"tbr":18000,"filesize":480000000},
     {"format_id":"137","ext":"mp4","vcodec":"avc1.640028","acodec":"none","height":1080,"fps":30,"tbr":4000,"filesize":96000000},
     {"format_id":"136","ext":"mp4","vcodec":"avc1.4d401f","acodec":"none","height":720,"fps":30,"tbr":2000,"filesize":48000000},
     {"format_id":"135","ext":"mp4","vcodec":"avc1.4d401e","acodec":"none","height":480,"fps":30,"tbr":1000,"filesize_approx":24000000},
     {"format_id":"18","ext":"mp4","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"fps":30,"tbr":600,"filesize":15000000}]}
    """;
}
