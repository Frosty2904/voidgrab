using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace VoidGrab.Services;

/// <summary>
/// Makes sure yt-dlp and ffmpeg exist on disk, fetching them when they do not.
/// </summary>
/// <remarks>
/// Both live in a <c>tools</c> folder beside the executable rather than inside
/// it. That is a deliberate choice: yt-dlp needs replacing whenever a site
/// changes how it serves media, which is often, and a binary the app can
/// overwrite in place — see <see cref="UpdateYtDlpAsync"/> — is worth more than
/// one sealed into a single-file publish. It also keeps launch instant, since
/// nothing has to be unpacked to temp on every start.
/// </remarks>
public sealed class ToolProvisioner
{
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <remarks>
    /// BtbN's GitHub release asset rather than gyan.dev. gyan.dev answers the
    /// documented download URL with a 303 to a versioned file and then serves it
    /// at roughly 300 KB/s from here; this asset is a direct 200 measured at
    /// ~14 MB/s. It is the larger archive of the two and still finishes in a
    /// fraction of the time, and it is the same host yt-dlp already comes from.
    /// </remarks>
    private const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(20),
    };

    public string ToolsDirectory { get; }

    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");

    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");

    public string FfprobePath => Path.Combine(ToolsDirectory, "ffprobe.exe");

    public bool HasYtDlp => File.Exists(YtDlpPath);

    public bool HasFfmpeg => File.Exists(FfmpegPath);

    static ToolProvisioner()
    {
        // Some CDNs reject requests with no User-Agent outright; sending a real
        // one costs nothing and removes a whole class of confusing failure.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VoidGrab/1.0 (+https://github.com/)");
    }

    public ToolProvisioner()
    {
        var baseDir = AppContext.BaseDirectory;
        ToolsDirectory = Path.Combine(baseDir, "tools");
    }

    /// <summary>
    /// Fetches whatever is missing. Safe to call on every start — present tools
    /// are left alone, so the normal path does no network work at all.
    /// </summary>
    public async Task EnsureAsync(IProgress<string> log, CancellationToken token = default)
    {
        Directory.CreateDirectory(ToolsDirectory);

        if (!HasYtDlp)
        {
            log.Report("Fetching yt-dlp…");
            await DownloadFileAsync(YtDlpUrl, YtDlpPath, token);
            log.Report("yt-dlp ready.");
        }

        if (!HasFfmpeg)
        {
            log.Report("Fetching ffmpeg (~185 MB, first run only)…");
            await DownloadFfmpegAsync(log, token);
            log.Report("ffmpeg ready.");
        }
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
        var staging = YtDlpPath + ".new";

        log.Report("Downloading the latest yt-dlp…");
        await DownloadFileAsync(YtDlpUrl, staging, token);

        File.Move(staging, YtDlpPath, overwrite: true);
        log.Report("yt-dlp updated.");
    }

    private async Task DownloadFfmpegAsync(IProgress<string> log, CancellationToken token)
    {
        var zipPath = Path.Combine(ToolsDirectory, "ffmpeg-download.zip");

        try
        {
            await DownloadFileAsync(FfmpegZipUrl, zipPath, token, log);
            EnsureLooksLikeZip(zipPath);

            log.Report("Unpacking ffmpeg…");
            using var archive = ZipFile.OpenRead(zipPath);

            // The archive nests everything under a versioned folder; take just
            // the two binaries out of its bin/ directory and flatten them.
            foreach (var entry in archive.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (name is not ("ffmpeg.exe" or "ffprobe.exe")) continue;
                if (!entry.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) continue;

                entry.ExtractToFile(Path.Combine(ToolsDirectory, name), overwrite: true);
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

        if (!HasFfmpeg)
        {
            throw new InvalidOperationException(
                "The ffmpeg archive did not contain ffmpeg.exe. Try again, or drop " +
                "ffmpeg.exe into the tools folder by hand.");
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
        // a hang, which is exactly how the previous ffmpeg source presented.
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
    /// Guards the failure this replaced: a host answering with a redirect page
    /// or an error body produces a small file that is not a zip, and without
    /// this check the only symptom is an unhelpful exception from deep inside
    /// the zip reader.
    /// </remarks>
    private static void EnsureLooksLikeZip(string path)
    {
        var info = new FileInfo(path);
        var magic = new byte[2];

        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(magic, 0, 2) < 2 || magic[0] != (byte)'P' || magic[1] != (byte)'K')
            {
                throw new InvalidOperationException(
                    $"The ffmpeg download was not a zip archive ({info.Length} bytes). " +
                    "The host may have returned an error page. Try again, or drop " +
                    "ffmpeg.exe and ffprobe.exe into the tools folder by hand.");
            }
        }
    }
}
