using System.Text.RegularExpressions;

namespace VoidGrab.Models;

public enum SourceKind
{
    YouTube,
    TikTok,
    Spotify,
    Other,
}

/// <summary>
/// Works out which service a pasted link belongs to.
/// </summary>
/// <remarks>
/// This only ever affects presentation and which pipeline a job takes. Anything
/// unrecognised is handed to yt-dlp as <see cref="SourceKind.Other"/> rather
/// than rejected — it supports well over a thousand sites, and refusing a link
/// this class has not heard of would be the app inventing a limit the tool
/// underneath does not have.
/// </remarks>
public static partial class LinkClassifier
{
    [GeneratedRegex(@"(youtube\.com|youtu\.be|youtube-nocookie\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeRe();

    [GeneratedRegex(@"tiktok\.com", RegexOptions.IgnoreCase)]
    private static partial Regex TikTokRe();

    [GeneratedRegex(@"(open\.spotify\.com|^spotify:)", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyRe();

    public static SourceKind Classify(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return SourceKind.Other;

        var value = url.Trim();
        if (SpotifyRe().IsMatch(value)) return SourceKind.Spotify;
        if (TikTokRe().IsMatch(value)) return SourceKind.TikTok;
        if (YouTubeRe().IsMatch(value)) return SourceKind.YouTube;
        return SourceKind.Other;
    }

    public static string DisplayName(SourceKind kind) => kind switch
    {
        SourceKind.YouTube => "YouTube",
        SourceKind.TikTok => "TikTok",
        SourceKind.Spotify => "Spotify",
        _ => "Link",
    };

    /// <summary>Splits pasted text into candidate links, one per line or space.</summary>
    public static IEnumerable<string> ExtractLinks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (var token in text.Split(
                     ['\n', '\r', ' ', '\t', ','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            {
                yield return token;
            }
        }
    }
}
