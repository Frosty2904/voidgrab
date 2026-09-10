using System.Text;
using VoidGrab.Models;
using VoidGrab.Services;

namespace VoidGrab.Desktop;

/// <summary>
/// The <c>--selftest</c> and <c>--probe-download</c> diagnostics, run before any
/// window exists.
/// </summary>
/// <remarks>
/// Deliberately headless. These are the only way to check a build on a machine
/// I cannot see — a Mac reached over SSH, or one where the app will not start
/// and the interesting question is whether the tools were fetched at all.
/// </remarks>
internal static class CommandLine
{
    public static bool WantsHeadless(string[] args) =>
        args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)) ||
        args.Any(a => a.Equals("--probe-download", StringComparison.OrdinalIgnoreCase)) ||
        args.Any(a => a.Equals("--fetch-tools", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        if (args.Any(a => a.Equals("--fetch-tools", StringComparison.OrdinalIgnoreCase)))
        {
            return FetchTools();
        }

        var probeIndex = Array.FindIndex(
            args, a => a.Equals("--probe-download", StringComparison.OrdinalIgnoreCase));

        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            var format = probeIndex + 2 < args.Length ? args[probeIndex + 2] : "mp3";
            return Probe(args[probeIndex + 1], format);
        }

        return SelfTest();
    }

    private static int SelfTest()
    {
        var tools = new ToolProvisioner();
        var runner = new YtDlpRunner(tools);
        var report = new StringBuilder();

        void Line(string text = "") => report.AppendLine(text);

        Line("VoidGrab - self test");
        Line($"  version    : {typeof(CommandLine).Assembly.GetName().Version}");
        Line($"  os         : {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        Line($"  runtime id : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        Line($"  base dir   : {AppContext.BaseDirectory}");
        Line($"  tools dir  : {tools.ToolsDirectory}");
        Line($"  yt-dlp     : {(tools.HasYtDlp ? tools.YtDlpPath : "MISSING - fetched on first run")}");
        Line($"  ffmpeg     : {(tools.HasFfmpeg ? tools.FfmpegPath : "MISSING - fetched on first run")}");
        Line($"  ffmpeg from: {(tools.UsingInstalledFfmpeg ? "already installed on this machine" : "VoidGrab's tools folder")}");
        Line($"  settings   : {SettingsStore.Directory}");
        Line($"  saves to   : {AppSettings.DefaultOutputDirectory()}");
        Line();

        Line("Link classification");
        foreach (var url in new[]
                 {
                     "https://www.youtube.com/watch?v=YE7VzlLtp-4",
                     "https://www.tiktok.com/@user/video/1234567890",
                     "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M",
                     "https://soundcloud.com/artist/track",
                 })
        {
            Line($"  {LinkClassifier.Classify(url),-8}  {url}");
        }

        Line();
        Line("Generated yt-dlp arguments");

        foreach (var formatId in new[] { "mp4", "mkv", "mp3", "flac" })
        {
            var format = MediaFormats.ById(formatId);
            var job = new DownloadJob
            {
                Url = "https://www.youtube.com/watch?v=TEST",
                Source = SourceKind.YouTube,
                Format = format,
                Quality = (format.IsAudio ? QualityOptions.Audio : QualityOptions.Video)[0],
                OutputDirectory = AppSettings.DefaultOutputDirectory(),
            };

            Line();
            Line($"  [{formatId}]");
            Line("    " + string.Join(" ", runner.DescribeArguments(job).Select(Quote)));
        }

        var text = report.ToString();
        Console.WriteLine(text);

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), text);
        }
        catch
        {
            // The console copy is what matters; a read-only app bundle should not
            // turn a diagnostic into a crash.
        }

        return 0;
    }

    /// <summary>Downloads the tools without opening a window.</summary>
    private static int FetchTools()
    {
        var tools = new ToolProvisioner();
        var log = new InlineProgress<string>(Console.WriteLine);

        try
        {
            tools.EnsureAsync(log).GetAwaiter().GetResult();
            Console.WriteLine($"yt-dlp : {tools.YtDlpPath}");
            Console.WriteLine($"ffmpeg : {tools.FfmpegPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tool setup failed: {ex.Message}");
            return 1;
        }
    }

    private static int Probe(string url, string formatId)
    {
        var tools = new ToolProvisioner();
        var runner = new YtDlpRunner(tools);

        if (!tools.HasYtDlp || !tools.HasFfmpeg)
        {
            Console.WriteLine("Tools are missing. Run with --fetch-tools first.");
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

        // Not Progress<T>: that captures a SynchronizationContext, and reporting
        // through one that nothing is pumping would silently drop every tick.
        var progress = new InlineProgress<DownloadProgress>(p =>
        {
            ticks++;
            if (p.Status == "Converting") sawConverting = true;
        });

        DownloadResult result;
        try
        {
            result = runner
                .RunAsync(job, progress, new InlineProgress<string>(_ => { }), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Threw: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  success        : {result.Success}");
        Console.WriteLine($"  progress ticks : {ticks}");
        Console.WriteLine($"  saw converting : {sawConverting}");
        Console.WriteLine($"  file           : {result.FilePath ?? "(none reported)"}");
        Console.WriteLine($"  error          : {result.Error ?? "(none)"}");

        if (result.FilePath is { Length: > 0 } path && File.Exists(path))
        {
            Console.WriteLine($"  size on disk   : {new FileInfo(path).Length / 1024 / 1024} MB");
        }

        return result.Success ? 0 : 1;
    }

    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
