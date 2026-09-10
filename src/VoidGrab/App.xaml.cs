using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using VoidGrab.Models;
using VoidGrab.Services;

namespace VoidGrab;

public partial class App : Application
{
    // DllImport rather than LibraryImport: the source-generated variant requires
    // <AllowUnsafeBlocks>, which is a large concession for one console attach.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            Shutdown(0);
            return;
        }

        var probeIndex = Array.FindIndex(
            e.Args, a => a.Equals("--probe-download", StringComparison.OrdinalIgnoreCase));
        if (probeIndex >= 0 && probeIndex + 1 < e.Args.Length)
        {
            var format = probeIndex + 2 < e.Args.Length ? e.Args[probeIndex + 2] : "mp3";
            Shutdown(RunDownloadProbe(e.Args[probeIndex + 1], format));
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Reports what the app can see and exactly what it would ask yt-dlp to do,
    /// then exits without showing a window.
    /// </summary>
    /// <remarks>
    /// Two audiences. For a user it answers "is my copy set up correctly?"
    /// without making them start a download to find out. For anyone changing
    /// <see cref="YtDlpRunner"/> it prints the generated argument list, which is
    /// the part most worth eyeballing and the hardest to check from the UI.
    ///
    /// A WinExe has no console of its own, so it borrows the parent's where one
    /// exists and always writes the same report to a file beside the executable.
    /// </remarks>
    private static void RunSelfTest()
    {
        AttachConsole(AttachParentProcess);

        var tools = new ToolProvisioner();
        var runner = new YtDlpRunner(tools);
        var report = new StringBuilder();

        void Line(string text = "") => report.AppendLine(text);

        Line("VoidGrab - self test");
        Line($"  version    : {typeof(App).Assembly.GetName().Version}");
        Line($"  base dir   : {AppContext.BaseDirectory}");
        Line($"  tools dir  : {tools.ToolsDirectory}");
        Line($"  yt-dlp     : {(tools.HasYtDlp ? tools.YtDlpPath : "MISSING - fetched on first run")}");
        Line($"  ffmpeg     : {(tools.HasFfmpeg ? tools.FfmpegPath : "MISSING - fetched on first run")}");
        Line($"  settings   : {SettingsStore.Directory}");
        Line($"  saves to   : {AppSettings.DefaultOutputDirectory()}");
        Line();

        Line("Link classification");
        foreach (var url in new[]
                 {
                     "https://www.youtube.com/watch?v=YE7VzlLtp-4",
                     "https://youtu.be/YE7VzlLtp-4",
                     "https://www.tiktok.com/@user/video/1234567890",
                     "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT",
                     "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M",
                     "https://soundcloud.com/artist/track",
                 })
        {
            Line($"  {LinkClassifier.Classify(url),-8}  {url}");
        }

        Line();
        Line("Generated yt-dlp arguments");

        foreach (var (formatId, qualityLabel) in new[]
                 {
                     ("mp4", "1080p"), ("mp4", "Best available"),
                     ("mkv", "2160p · 4K"), ("mp3", "320 kbps"), ("flac", "Best available"),
                 })
        {
            var format = MediaFormats.ById(formatId);
            var quality = (format.IsAudio ? QualityOptions.Audio : QualityOptions.Video)
                              .FirstOrDefault(q => q.Label == qualityLabel)
                          ?? (format.IsAudio ? QualityOptions.Audio[0] : QualityOptions.Video[0]);

            var job = new DownloadJob
            {
                Url = "https://www.youtube.com/watch?v=TEST",
                Source = SourceKind.YouTube,
                Format = format,
                Quality = quality,
                OutputDirectory = @"C:\Downloads",
            };

            Line();
            Line($"  [{formatId} / {quality.Label}]");
            Line("    " + string.Join(" ", runner.DescribeArguments(job).Select(Quote)));
        }

        var spotifyJob = new DownloadJob
        {
            Url = "https://open.spotify.com/track/TEST",
            Source = SourceKind.Spotify,
            Format = MediaFormats.ById("mp3"),
            Quality = QualityOptions.Audio[0],
            OutputDirectory = @"C:\Downloads",

        };
        spotifyJob.SearchQuery = "Some Artist - Some Track";

        Line();
        Line("  [spotify-matched mp3 - note the search term, not a Spotify URL]");
        Line("    " + string.Join(" ", runner.DescribeArguments(spotifyJob).Select(Quote)));

        var text = report.ToString();
        Console.WriteLine(text);

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), text);
        }
        catch
        {
            // The console copy is the one that matters; a read-only install
            // directory should not turn a diagnostic into a crash.
        }
    }

    /// <summary>
    /// Runs one real download through <see cref="YtDlpRunner"/> and reports the
    /// outcome, then exits.
    /// </summary>
    /// <remarks>
    /// Exercises the code the app actually uses — argument construction,
    /// progress parsing, postprocessor detection and result handling — rather
    /// than a command line retyped by hand, which is the thing most likely to
    /// drift away from what ships.
    /// </remarks>
    private static int RunDownloadProbe(string url, string formatId)
    {
        AttachConsole(AttachParentProcess);

        var tools = new ToolProvisioner();
        var runner = new YtDlpRunner(tools);

        if (!tools.HasYtDlp || !tools.HasFfmpeg)
        {
            Console.WriteLine("Tools are missing; run the app once to fetch them.");
            return 2;
        }

        var format = MediaFormats.ById(formatId);
        var target = Path.Combine(Path.GetTempPath(), "VoidGrabProbe");

        var job = new DownloadJob
        {
            Url = url,
            Source = LinkClassifier.Classify(url),
            Format = format,
            Quality = (format.IsAudio ? QualityOptions.Audio : QualityOptions.Video)
                .First(q => q.Label != "Best available"),
            OutputDirectory = target,
        };

        Console.WriteLine($"Probe: {format.Id} from {job.Source} -> {target}");

        var ticks = 0;
        var sawConverting = false;
        var lastDetail = "";

        // Deliberately NOT Progress<T>. That type captures the creating thread's
        // SynchronizationContext — here the WPF UI thread, which this probe then
        // blocks — so every callback would queue up behind the block and the
        // probe would report zero ticks for a download that reported plenty.
        var progress = new InlineProgress<DownloadProgress>(p =>
        {
            ticks++;
            if (p.Status == "Converting") sawConverting = true;
            if (!string.IsNullOrWhiteSpace(p.Detail)) lastDetail = p.Detail;
        });

        DownloadResult result;
        try
        {
            // Task.Run first, then block. OnStartup runs on the WPF UI thread,
            // which installs a SynchronizationContext that RunAsync's awaits
            // capture; blocking that same thread on the result would leave those
            // continuations with nowhere to resume. Hopping to the thread pool
            // clears the context and makes the wait safe.
            result = Task.Run(() => runner.RunAsync(
                job, progress, new InlineProgress<string>(_ => { }), CancellationToken.None))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Threw: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  success        : {result.Success}");
        Console.WriteLine($"  progress ticks : {ticks}");
        Console.WriteLine($"  last detail    : {lastDetail}");
        Console.WriteLine($"  saw converting : {sawConverting}");
        Console.WriteLine($"  file           : {result.FilePath ?? "(none reported)"}");
        Console.WriteLine($"  error          : {result.Error ?? "(none)"}");

        if (result.FilePath is { Length: > 0 } path && File.Exists(path))
        {
            Console.WriteLine($"  size on disk   : {new FileInfo(path).Length / 1024 / 1024} MB");
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;
}
