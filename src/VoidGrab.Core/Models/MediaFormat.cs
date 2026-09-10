namespace VoidGrab.Models;

public enum FormatKind
{
    Video,
    Audio,
}

/// <summary>
/// One selectable output format.
/// </summary>
/// <param name="Id">The value handed to yt-dlp (<c>--audio-format</c> or <c>--merge-output-format</c>).</param>
/// <param name="Label">What the picker shows.</param>
/// <param name="Note">A caveat worth reading before choosing it, or empty.</param>
public sealed record MediaFormat(string Id, string Label, FormatKind Kind, string Note = "")
{
    public bool IsAudio => Kind == FormatKind.Audio;

    public override string ToString() => Label;
}

public static class MediaFormats
{
    /// <remarks>
    /// The lossless entries carry a warning rather than being hidden. Every
    /// source here is already lossy — YouTube has no lossless audio to give —
    /// so FLAC or WAV re-encodes compressed audio into a much larger file
    /// without recovering anything the encoder threw away. People still ask for
    /// them (archival tooling, DJ software and some editors want a specific
    /// container), so they stay available and honestly labelled.
    /// </remarks>
    public static readonly IReadOnlyList<MediaFormat> All =
    [
        new("mp4", "MP4  ·  video", FormatKind.Video, "H.264 + AAC where offered — plays everywhere."),
        new("mkv", "MKV  ·  video", FormatKind.Video, "Keeps the best streams as-is, no re-encode."),
        new("webm", "WEBM  ·  video", FormatKind.Video, "VP9/AV1 + Opus. Small, but fussier playback."),

        new("mp3", "MP3  ·  audio", FormatKind.Audio, "The universal choice."),
        new("m4a", "M4A  ·  audio", FormatKind.Audio, "AAC. Often a direct copy, so no quality loss."),
        new("opus", "Opus  ·  audio", FormatKind.Audio, "Usually the original stream, copied untouched."),
        new("aac", "AAC  ·  audio", FormatKind.Audio, "Raw AAC stream."),
        new("vorbis", "Vorbis  ·  audio", FormatKind.Audio, "Ogg Vorbis."),
        new("flac", "FLAC  ·  audio", FormatKind.Audio, "Lossless container, lossy source — larger, not better."),
        new("wav", "WAV  ·  audio", FormatKind.Audio, "Uncompressed. Very large, lossy source."),
        new("alac", "ALAC  ·  audio", FormatKind.Audio, "Apple lossless. Same caveat as FLAC."),
    ];

    public static MediaFormat ById(string id) =>
        All.FirstOrDefault(f => f.Id == id) ?? All[0];
}

/// <summary>Video ceiling, ignored for audio-only formats.</summary>
public sealed record QualityOption(string Label, int? MaxHeight)
{
    public override string ToString() => Label;
}

public static class QualityOptions
{
    public static readonly IReadOnlyList<QualityOption> Video =
    [
        new("Best available", null),
        new("2160p · 4K", 2160),
        new("1440p", 1440),
        new("1080p", 1080),
        new("720p", 720),
        new("480p", 480),
        new("360p", 360),
    ];

    /// <summary>Audio bitrates, in the 0-10 / kbps form yt-dlp accepts.</summary>
    public static readonly IReadOnlyList<QualityOption> Audio =
    [
        new("Best available", null),
        new("320 kbps", 320),
        new("256 kbps", 256),
        new("192 kbps", 192),
        new("128 kbps", 128),
        new("96 kbps", 96),
    ];
}
