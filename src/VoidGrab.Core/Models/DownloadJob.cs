namespace VoidGrab.Models;

public enum JobState
{
    Queued,
    Resolving,
    Downloading,
    Converting,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// One unit of work in the queue.
/// </summary>
/// <remarks>
/// A Spotify link expands into one job per track before anything is queued, so
/// by the time a job exists it always names exactly one file to produce. That
/// keeps progress reporting honest — a single job never means "somewhere
/// between one and four hundred downloads".
/// </remarks>
public sealed class DownloadJob
{
    public required string Url { get; init; }
    public required SourceKind Source { get; init; }
    public required MediaFormat Format { get; init; }
    public required QualityOption Quality { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>Best-effort name shown before the real title is known.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Set for Spotify-sourced jobs: the "artist - track" phrase to search
    /// YouTube for. When present the runner searches instead of opening
    /// <see cref="Url"/> directly.
    /// </summary>
    public string? SearchQuery { get; set; }

    public bool EmbedMetadata { get; init; } = true;
    public bool EmbedThumbnail { get; init; } = true;
}

/// <summary>A progress tick from the running tool.</summary>
public readonly record struct DownloadProgress(
    double? Fraction,
    string Status,
    string Detail);
