using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace VoidGrab.Services;

/// <summary>
/// Makes sure yt-dlp and ffmpeg exist, fetching them when they do not.
/// </summary>
/// <remarks>
/// <para>
/// Both live in a <c>tools</c> folder beside the executable rather than inside
/// it. yt-dlp needs replacing whenever a site changes how it serves media, and a
/// binary the app can overwrite in place — see <see cref="UpdateYtDlpAsync"/> —
/// is worth more than one sealed into a single-file publish. It also keeps
/// launch instant, since nothing is unpacked to temp on every start.
/// </para>
/// <para>
/// The platforms differ in one important way. On Windows nothing is expected to
/// be installed, so both tools are downloaded. On macOS and Linux an existing
/// ffmpeg is far more likely — and far more likely to be the right architecture
/// — so <see cref="FindInstalledFfmpeg"/> looks for one first and only falls
/// back to downloading.
/// </para>
/// </remarks>
public sealed class ToolProvisioner
{
    private const string YtDlpWindows =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <remarks>A universal2 binary, so it is native on both Intel and Apple silicon.</remarks>
    private const string YtDlpMac =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos";

    private const string YtDlpLinux =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";

    /// <remarks>
    /// BtbN's GitHub asset rather than gyan.dev: the latter answers its
    /// documented URL with a 303 to a versioned file and then serves it at
    /// roughly 300 KB/s, where this is a direct response measured at ~14 MB/s.
    /// </remarks>
    private const string FfmpegZipWindows =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    /// <remarks>
    /// evermeet.cx ships ffmpeg and ffprobe as separate archives, each holding a
    /// single binary at the root. These builds are x86_64 only; on Apple silicon
    /// they run through Rosetta 2. That is precisely why an installed copy is
    /// preferred — a Homebrew ffmpeg on an M-series Mac is native arm64.
    /// </remarks>
    private const string FfmpegZipMac = "https://evermeet.cx/ffmpeg/getrelease/ffmpeg/zip";

    private const string FfprobeZipMac = "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    private string? _installedFfmpegDirectory;

    static ToolProvisioner()
    {
        // Some CDNs reject requests with no User-Agent outright; sending a real
        // one costs nothing and removes a whole class of confusing failure.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VoidGrab/1.0 (+https://github.com/)");
    }

    public ToolProvisioner()
    {
        ToolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        _installedFfmpegDirectory = FindInstalledFfmpeg();
    }

    public string ToolsDirectory { get; }

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public string YtDlpPath => Path.Combine(ToolsDirectory, Exe("yt-dlp"));

    /// <summary>Where ffmpeg actually is: an installed copy if there is one, else ours.</summary>
    public string FfmpegDirectory => _installedFfmpegDirectory ?? ToolsDirectory;

    public string FfmpegPath => Path.Combine(FfmpegDirectory, Exe("ffmpeg"));

    public string FfprobePath => Path.Combine(FfmpegDirectory, Exe("ffprobe"));

    public bool HasYtDlp => File.Exists(YtDlpPath);

    public bool HasFfmpeg => File.Exists(FfmpegPath);

    /// <summary>True when ffmpeg came from the system rather than from us.</summary>
    public bool UsingInstalledFfmpeg => _installedFfmpegDirectory is not null;

    /// <summary>
    /// Locates an ffmpeg already on the machine.
    /// </summary>
    /// <remarks>
    /// Only consulted off Windows. Homebrew's two prefixes cover the large
    /// majority of Macs (<c>/opt/homebrew</c> on Apple silicon, <c>/usr/local</c>
    /// on Intel); PATH covers MacPorts, Nix and everything else. ffprobe must be
    /// alongside it, because yt-dlp uses both and half an install is worse than
    /// none — it fails later, during postprocessing, with a file already on disk.
    /// </remarks>
    private static string? FindInstalledFfmpeg()
    {
        if (OperatingSystem.IsWindows()) return null;

        var candidates = new List<string>
        {
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/opt/local/bin",
            "/usr/bin",
        };

        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path)
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var directory in candidates)
        {
            if (File.Exists(Path.Combine(directory, "ffmpeg")) &&
                File.Exists(Path.Combine(directory, "ffprobe")))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches whatever is missing. Safe to call on every start — present tools
    /// are left alone, so the normal path does no network work at all.
    /// </summary>
    public async Task EnsureAsync(IProgress<string> log, CancellationToken token = default)
    {
        Directory.CreateDirectory(ToolsDirectory);

        // A yt-dlp left behind by an interrupted download is still a file, so
        // File.Exists alone would happily hand a truncated binary to every later
        // run. Ask whether it actually works before trusting it.
        if (HasYtDlp && !await IsYtDlpWorkingAsync(token))
        {
            log.Report("The installed yt-dlp is damaged - replacing it.");
            TryDelete(YtDlpPath);
        }

        if (!HasYtDlp)
        {
            log.Report("Fetching yt-dlp…");
            await DownloadToFinalPathAsync(YtDlpUrl(), YtDlpPath, token, log);
            log.Report("yt-dlp ready.");
        }

        if (HasFfmpeg)
        {
            if (UsingInstalledFfmpeg)
            {
                log.Report($"Using the ffmpeg already installed at {FfmpegDirectory}.");
            }

            return;
        }

        log.Report(OperatingSystem.IsWindows()
            ? "Fetching ffmpeg (~185 MB, first run only)…"
            : "Fetching ffmpeg (first run only)…");

        await DownloadFfmpegAsync(log, token);
        log.Report("ffmpeg ready.");
    }

    /// <summary>
    /// Replaces yt-dlp with the current release.
    /// </summary>
    /// <remarks>
    /// Downloads to a temporary name and swaps it in only once the transfer has
    /// finished, so an interrupted update leaves the working copy intact rather
    /// than a truncated binary that cannot run.
    /// </remarks>
    public async Task UpdateYtDlpAsync(IProgress<string> log, CancellationToken token = default)
    {
        Directory.CreateDirectory(ToolsDirectory);

        log.Report("Downloading the latest yt-dlp…");
        await DownloadToFinalPathAsync(YtDlpUrl(), YtDlpPath, token, log);
        log.Report("yt-dlp updated.");
    }

    private static string YtDlpUrl() =>
        OperatingSystem.IsWindows() ? YtDlpWindows
        : OperatingSystem.IsMacOS() ? YtDlpMac
        : YtDlpLinux;

    private async Task DownloadFfmpegAsync(IProgress<string> log, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            await DownloadAndExtractAsync(FfmpegZipWindows, ["ffmpeg.exe", "ffprobe.exe"], log, token);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Two archives, one binary each, rather than a single bundle.
            await DownloadAndExtractAsync(FfmpegZipMac, ["ffmpeg"], log, token);
            await DownloadAndExtractAsync(FfprobeZipMac, ["ffprobe"], log, token);
        }
        else
        {
            throw new InvalidOperationException(
                "Automatic ffmpeg setup is not available on this platform. Install it with " +
                "your package manager (for example: sudo apt install ffmpeg), or place " +
                $"ffmpeg and ffprobe in {ToolsDirectory}.");
        }

        if (!File.Exists(Path.Combine(ToolsDirectory, Exe("ffmpeg"))))
        {
            throw new InvalidOperationException(
                "The ffmpeg download did not contain the expected binary. " +
                (OperatingSystem.IsMacOS()
                    ? "Install it with 'brew install ffmpeg' instead, then restart VoidGrab."
                    : $"Try again, or place ffmpeg in {ToolsDirectory} by hand."));
        }

        // It went into tools/, so stop pointing at any system directory.
        _installedFfmpegDirectory = null;
    }

    private async Task DownloadAndExtractAsync(
        string url,
        string[] wanted,
        IProgress<string> log,
        CancellationToken token)
    {
        var zipPath = Path.Combine(ToolsDirectory, "download.zip");

        try
        {
            await DownloadFileAsync(url, zipPath, token, log);
            EnsureLooksLikeZip(zipPath);

            log.Report("Unpacking…");
            using var archive = ZipFile.OpenRead(zipPath);

            foreach (var entry in archive.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                // Windows builds nest binaries under a versioned bin/ folder; the
                // macOS archives hold one binary at the root. Matching on the
                // leaf name alone handles both without a second code path.
                var destination = Path.Combine(ToolsDirectory, name);
                entry.ExtractToFile(destination, overwrite: true);
                MakeExecutable(destination);
                log.Report($"Extracted {name}.");
            }
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { /* a stale zip is harmless */ }
            }
        }
    }

    /// <summary>
    /// Grants the execute bit that an extracted file does not carry.
    /// </summary>
    /// <remarks>
    /// Unix permissions inside a zip entry do not survive .NET's extraction, and
    /// a downloaded file arrives without them either. Without this every tool
    /// fails with "permission denied" the first time it runs. A no-op on
    /// Windows, which has no such bit.
    /// </remarks>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not mark {Path.GetFileName(path)} as executable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Downloads to a staging name and only moves it into place once it has
    /// arrived whole.
    /// </summary>
    /// <remarks>
    /// This is the difference that mattered. Writing straight to the final path
    /// means any interruption — a dropped connection, the app being closed, an
    /// exception — leaves a truncated file under the real name. Since presence
    /// is what decides whether to download again, that half-file is then used
    /// forever, and a PyInstaller-packed tool like yt-dlp fails with "Could not
    /// load PyInstaller's embedded PKG archive", which says nothing about the
    /// actual problem. Staging means the final path only ever holds a complete
    /// file, and a failure leaves no trace to poison the next run.
    /// </remarks>
    private static async Task DownloadToFinalPathAsync(
        string url,
        string destination,
        CancellationToken token,
        IProgress<string> log)
    {
        var staging = destination + ".part";

        try
        {
            await DownloadFileAsync(url, staging, token, log);
            EnsureLooksLikeProgram(staging);
            MakeExecutable(staging);
            File.Move(staging, destination, overwrite: true);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Rejects an error page or a truncated transfer saved under a program's name.</summary>
    private static void EnsureLooksLikeProgram(string path)
    {
        var length = new FileInfo(path).Length;

        // Every build of these tools is megabytes; anything this small is an
        // error page or a stub, whatever its extension claims.
        if (length < 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"The download was only {length} bytes, which is far too small to be " +
                "the real program. The host may have returned an error page.");
        }

        if (!OperatingSystem.IsWindows()) return;

        var magic = new byte[2];
        using var stream = File.OpenRead(path);
        if (stream.Read(magic, 0, 2) < 2 || magic[0] != (byte)'M' || magic[1] != (byte)'Z')
        {
            throw new InvalidOperationException(
                "The download is not a Windows executable. The host may have returned " +
                "an error page.");
        }
    }

    /// <summary>
    /// Asks yt-dlp for its version, as a liveness check.
    /// </summary>
    /// <remarks>
    /// Cheap, and the only check that catches the failure this guards against:
    /// a truncated PyInstaller binary has a valid PE header and a plausible
    /// size, and only reveals itself when its bootloader cannot find the
    /// archive inside it. Running it is the test.
    /// </remarks>
    private async Task<bool> IsYtDlpWorkingAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = YtDlpPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.StartInfo.ArgumentList.Add("--version");
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return process.ExitCode == 0 && output.Trim().Length > 0;
        }
        catch
        {
            // Cannot start, times out, or exits non-zero: whatever the reason,
            // it is not a tool worth handing a download to.
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Locked by a virus scanner or a sync client; the caller reports the
            // real failure and a later run will try again.
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        CancellationToken token,
        IProgress<string>? log = null)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var expected = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(destination);

        // Copied by hand rather than with CopyToAsync so a large download can
        // report progress. Without it a slow transfer is indistinguishable from
        // a hang, which is exactly how a previous ffmpeg source presented.
        var buffer = new byte[81920];
        long written = 0;
        var lastReport = 0L;

        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            written += read;

            if (log is not null && written - lastReport >= 8L * 1024 * 1024)
            {
                lastReport = written;
                log.Report(expected is { } total
                    ? $"  {YtDlpRunner.Human(written)} of {YtDlpRunner.Human(total)}"
                    : $"  {YtDlpRunner.Human(written)}");
            }
        }

        if (expected is { } size && written != size)
        {
            throw new IOException(
                $"Download ended early: got {YtDlpRunner.Human(written)} of {YtDlpRunner.Human(size)}.");
        }
    }

    /// <summary>
    /// Confirms the download is actually an archive.
    /// </summary>
    /// <remarks>
    /// Guards a failure seen in practice: a host answering with a redirect page
    /// or an error body produces a small file that is not a zip, and without this
    /// check the only symptom is an unhelpful exception from the zip reader.
    /// </remarks>
    private static void EnsureLooksLikeZip(string path)
    {
        var length = new FileInfo(path).Length;
        var magic = new byte[2];

        using var stream = File.OpenRead(path);
        if (stream.Read(magic, 0, 2) < 2 || magic[0] != (byte)'P' || magic[1] != (byte)'K')
        {
            throw new InvalidOperationException(
                $"The download was not a zip archive ({length} bytes). The host may have " +
                "returned an error page. Try again, or place the binaries in the tools " +
                "folder by hand.");
        }
    }
}
