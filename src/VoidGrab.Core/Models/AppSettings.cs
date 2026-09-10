using System.IO;
namespace VoidGrab.Models;

/// <summary>
/// Everything that survives a restart, persisted as JSON under
/// <c>%APPDATA%\VoidGrab\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    public string OutputDirectory { get; set; } = DefaultOutputDirectory();

    public string FormatId { get; set; } = "mp4";

    public string VideoQualityLabel { get; set; } = "Best available";

    public string AudioQualityLabel { get; set; } = "Best available";

    public bool EmbedMetadata { get; set; } = true;

    public bool EmbedThumbnail { get; set; } = true;

    /// <summary>
    /// Turn a playlist or channel link into one queue row per item, rather
    /// than a single row whose percentage secretly covers hundreds of files.
    /// </summary>
    public bool ExpandPlaylists { get; set; } = true;

    /// <summary>
    /// Spotify application credentials, used only to read public catalogue
    /// metadata. They are stored in the clear, so they are deliberately the
    /// kind of credential that grants nothing else: a Client Credentials token
    /// can read public tracks and playlists and cannot touch an account.
    /// </summary>
    public string SpotifyClientId { get; set; } = "";

    public string SpotifyClientSecret { get; set; } = "";

    public bool HasSpotifyCredentials =>
        !string.IsNullOrWhiteSpace(SpotifyClientId) &&
        !string.IsNullOrWhiteSpace(SpotifyClientSecret);

    public static string DefaultOutputDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? Path.Combine(downloads, "VoidGrab") : profile;
    }
}
