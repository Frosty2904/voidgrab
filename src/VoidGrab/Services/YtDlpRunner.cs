using System.IO;
using System.Diagnostics;
using System.Globalization;
using VoidGrab.Models;

namespace VoidGrab.Services;

public sealed record DownloadResult(bool Success, string? FilePath, string? Error);

public sealed record PlaylistEntry(string Url, string Title);

/// <summary>
/// Drives yt-dlp as a child process.
/// </summary>
/// <remarks>
/// Wrapping the tool rather than reimplementing it is the whole point. Site
/// extractors break constantly and yt-dlp fixes them within days; anything this
/// app reimplemented would be stale by the time it shipped. So the job here is
/// narrow: build correct arguments, read progress back, and get out of the way.
///
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a
/// concatenated string. Video titles routinely contain quotes, ampersands and
/// non-Latin scripts, and hand-quoting a command line is how those turn into
/// either a broken download or an argument-injection bug.
/// </remarks>
public sealed class YtDlpRunner(ToolProvisioner tools)
{
    private const string ProgressMarker = "@P@";
    private const string FileMarker = "@F@";
    private const string PostMarker = "@PP@";

    /// <summary>Runs one job to completion.</summary>
    public async Task<DownloadResult> RunAsync(
        DownloadJob job,
        IProgress<DownloadProgress> progress,
        IProgress<string> log,
        CancellationToken token)
    {
        Directory.CreateDirectory(job.OutputDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = tools.YtDlpPath,
            WorkingDirectory = job.OutputDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var arg in BuildArguments(job)) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        string? producedFile = null;
        var errors = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;

            if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
            {
                var tick = ParseProgress(line[ProgressMarker.Length..]);
                if (tick is not null) progress.Report(tick.Value);
                return;
            }

            if (line.StartsWith(FileMarker, StringComparison.Ordinal))
            {
                producedFile = line[FileMarker.Length..].Trim();
                return;
            }

            if (line.StartsWith(PostMarker, StringComparison.Ordinal))
            {
                progress.Report(new DownloadProgress(null, "Converting", "Running ffmpeg…"));
                return;
            }

            log.Report(line);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            errors.Add(e.Data);
            log.Report(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree: yt-dlp spawns ffmpeg, and leaving that behind
            // would keep writing to a file the user believes they cancelled.
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var detail = errors.LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                         ?? errors.LastOrDefault()
                         ?? $"yt-dlp exited with code {process.ExitCode}.";
            return new DownloadResult(false, null, Clean(detail));
        }

        return new DownloadResult(true, producedFile, null);
    }

    /// <summary>
    /// Expands a playlist or channel link into its individual entries.
    /// </summary>
    /// <remarks>
    /// Done up front so one queue row always means one file. Letting yt-dlp
    /// walk a playlist internally would leave a single row claiming a percentage
    /// that silently covers anywhere from one to several hundred downloads.
    /// </remarks>
    public async Task<IReadOnlyList<PlaylistEntry>> ResolvePlaylistAsync(
        string url,
        CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tools.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var arg in new[]
                 {
                     "--ignore-config", "--flat-playlist", "--no-warnings",
                     "--print", "%(url)s\u001f%(title)s",
                     url,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var entries = new List<PlaylistEntry>();

        process.Start();
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            // Unit separator, because a title can contain anything printable.
            var parts = line.Split('\u001f', 2);
            if (parts.Length == 2 && Uri.IsWellFormedUriString(parts[0], UriKind.Absolute))
            {
                entries.Add(new PlaylistEntry(parts[0], parts[1]));
            }
        }

        await process.WaitForExitAsync(token);
        return entries;
    }

    /// <summary>
    /// The exact argument list <see cref="RunAsync"/> would use for this job.
    /// </summary>
    /// <remarks>
    /// Exposed for the <c>--selftest</c> report. Argument construction is the
    /// part of this class most worth inspecting and the hardest to verify from
    /// the UI, where it is invisible until a download already went wrong.
    /// </remarks>
    public IReadOnlyList<string> DescribeArguments(DownloadJob job) =>
        BuildArguments(job).ToList();

    private IEnumerable<string> BuildArguments(DownloadJob job)
    {
        yield return "--ignore-config";
        yield return "--newline";
        yield return "--no-simulate";
        yield return "--no-playlist";

        // `--print` below implies `--quiet`, which silences both the progress
        // stream and the ordinary output the log pane shows. Undo that: without
        // it the download still succeeds, but every progress bar sits at zero
        // for the whole transfer and the log stays empty.
        yield return "--no-quiet";
        yield return "--progress";

        yield return "--progress-template";
        yield return ProgressMarker +
                     "%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
                     "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

        yield return "--progress-template";
        yield return "postprocess:" + PostMarker + "%(progress.status)s";

        yield return "--print";
        yield return "after_move:" + FileMarker + "%(filepath)s";

        yield return "--paths";
        yield return job.OutputDirectory;
        yield return "--output";
        yield return "%(title)s.%(ext)s";
        yield return "--windows-filenames";
        yield return "--no-overwrites";

        yield return "--ffmpeg-location";
        yield return tools.ToolsDirectory;

        yield return "--retries";
        yield return "5";
        yield return "--fragment-retries";
        yield return "5";

        if (job.EmbedMetadata) yield return "--embed-metadata";
        if (job.EmbedThumbnail) yield return "--embed-thumbnail";

        if (job.Format.IsAudio)
        {
            yield return "--format";
            yield return "bestaudio/best";
            yield return "--extract-audio";
            yield return "--audio-format";
            yield return job.Format.Id;
            yield return "--audio-quality";
            // yt-dlp takes a bitrate like "192K", or 0 for "best available".
            yield return job.Quality.MaxHeight is { } kbps
                ? kbps.ToString(CultureInfo.InvariantCulture) + "K"
                : "0";
        }
        else
        {
            yield return "--format";
            yield return VideoFormatChain(job);
            yield return "--merge-output-format";
            yield return job.Format.Id;
        }

        // A Spotify job carries a phrase to find rather than a page to open.
        yield return job.SearchQuery is { Length: > 0 } query
            ? "ytsearch1:" + query
            : job.Url;
    }

    /// <summary>
    /// Builds the format preference chain for a video download.
    /// </summary>
    /// <remarks>
    /// MP4 asks for H.264 + AAC before anything else. The genuinely highest
    /// quality streams are now usually AV1 or VP9 with Opus — smaller files that
    /// are still valid MP4s, and that Windows' built-in player, most TVs and
    /// most editors refuse to open. Someone choosing MP4 wants a file that
    /// plays, so the newer codecs are a fallback for when nothing else is
    /// offered, which is what happens above 1080p.
    ///
    /// MKV and WEBM get no such treatment: both are chosen precisely to keep
    /// whatever the site considers best, without a re-encode.
    /// </remarks>
    private static string VideoFormatChain(DownloadJob job)
    {
        var cap = job.Quality.MaxHeight is { } h
            ? $"[height<=?{h.ToString(CultureInfo.InvariantCulture)}]"
            : "";

        if (job.Format.Id != "mp4")
        {
            return $"bestvideo{cap}+bestaudio/best{cap}/best";
        }

        return
            $"bestvideo{cap}[vcodec^=avc1]+bestaudio[acodec^=mp4a]/" +
            $"bestvideo{cap}[ext=mp4]+bestaudio[ext=m4a]/" +
            $"bestvideo{cap}+bestaudio/" +
            $"best{cap}/best";
    }

    private static DownloadProgress? ParseProgress(string payload)
    {
        var parts = payload.Split('|');
        if (parts.Length < 5) return null;

        var done = ParseLong(parts[0]);
        var total = ParseLong(parts[1]) ?? ParseLong(parts[2]);
        var speed = ParseDouble(parts[3]);
        var eta = ParseDouble(parts[4]);

        double? fraction = done is { } d && total is { } t && t > 0
            ? Math.Clamp((double)d / t, 0, 1)
            : null;

        var detail = new List<string>();
        if (done is not null) detail.Add($"{Human(done.Value)} of {(total is null ? "?" : Human(total.Value))}");
        if (speed is > 0) detail.Add($"{Human((long)speed.Value)}/s");
        if (eta is > 0) detail.Add($"{(int)eta.Value}s left");

        return new DownloadProgress(fraction, "Downloading", string.Join("   ·   ", detail));
    }

    // yt-dlp writes "NA" for fields that do not apply to the current stage.
    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    public static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string Clean(string message)
    {
        var trimmed = message.Trim();
        const string prefix = "ERROR:";
        var index = trimmed.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? trimmed[(index + prefix.Length)..].Trim() : trimmed;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or gone between the check and the call.
        }
    }
}
