using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoidGrab.Services;

public sealed record SpotifyTrack(string Title, string Artist)
{
    /// <summary>The phrase handed to YouTube search.</summary>
    public string SearchQuery => $"{Artist} - {Title}";

    public override string ToString() => SearchQuery;
}

public sealed class SpotifyException(string message) : Exception(message);

/// <summary>
/// Reads public catalogue metadata from Spotify.
/// </summary>
/// <remarks>
/// <para>
/// This class does not — and cannot — download audio from Spotify. Spotify's
/// streams are protected by Widevine DRM, and breaking that is both illegal in
/// most places and flatly out of scope. What it does is read the public
/// catalogue: given a track, album or playlist link, it returns the artist and
/// title of each entry.
/// </para>
/// <para>
/// Those names are then used as a YouTube search, and the audio comes from
/// YouTube like every other download in this app. That is the same approach
/// spotdl and similar tools take, and it is worth being blunt about in the UI:
/// the result is a re-recording found by name, not the file Spotify would have
/// played. Matching is imperfect, and a wrong match is a normal outcome rather
/// than a bug.
/// </para>
/// <para>
/// Authentication uses the Client Credentials flow, which reaches only public
/// catalogue data and never a user's account, library or private playlists.
/// </para>
/// </remarks>
public sealed partial class SpotifyResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    [GeneratedRegex(@"(?:open\.spotify\.com/(?:intl-[a-z]{2}/)?|spotify:)(track|playlist|album)[/:]([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex LinkRe();

    public static bool IsSpotifyLink(string url) => LinkRe().IsMatch(url);

    /// <summary>Expands a Spotify link into the tracks it names.</summary>
    public async Task<IReadOnlyList<SpotifyTrack>> ResolveAsync(
        string url,
        string clientId,
        string clientSecret,
        CancellationToken token)
    {
        var match = LinkRe().Match(url);
        if (!match.Success)
        {
            throw new SpotifyException("That does not look like a Spotify track, album or playlist link.");
        }

        var kind = match.Groups[1].Value.ToLowerInvariant();
        var id = match.Groups[2].Value;

        await EnsureTokenAsync(clientId, clientSecret, token);

        return kind switch
        {
            "track" => [await GetTrackAsync(id, token)],
            "album" => await GetAlbumTracksAsync(id, token),
            "playlist" => await GetPlaylistTracksAsync(id, token),
            _ => throw new SpotifyException($"Unsupported Spotify link type '{kind}'."),
        };
    }

    private async Task EnsureTokenAsync(string clientId, string clientSecret, CancellationToken token)
    {
        // A minute of headroom, so a long playlist walk cannot expire mid-way.
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-1)) return;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new SpotifyException(
                "Spotify needs a Client ID and Secret. Create a free app at " +
                "developer.spotify.com/dashboard and paste the two values into Settings.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" });

        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            throw new SpotifyException(
                response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    ? "Spotify rejected those credentials. Check the Client ID and Secret in Settings."
                    : $"Spotify authentication failed ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = document.RootElement;

        _token = root.GetProperty("access_token").GetString();
        var lifetime = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(lifetime);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            throw new SpotifyException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound =>
                    "Spotify could not find that — is the link public?",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "Spotify is rate limiting this app. Wait a moment and try again.",
                _ => $"Spotify request failed ({(int)response.StatusCode}).",
            });
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private async Task<SpotifyTrack> GetTrackAsync(string id, CancellationToken token)
    {
        using var document = await GetAsync($"https://api.spotify.com/v1/tracks/{id}", token);
        return ReadTrack(document.RootElement)
               ?? throw new SpotifyException("That Spotify track has no playable metadata.");
    }

    private async Task<IReadOnlyList<SpotifyTrack>> GetAlbumTracksAsync(string id, CancellationToken token) =>
        await PageAsync($"https://api.spotify.com/v1/albums/{id}/tracks?limit=50", ReadTrack, token);

    private async Task<IReadOnlyList<SpotifyTrack>> GetPlaylistTracksAsync(string id, CancellationToken token) =>
        await PageAsync(
            $"https://api.spotify.com/v1/playlists/{id}/tracks?limit=100",
            // A playlist wraps each entry in an item envelope, and a removed or
            // region-blocked track leaves that envelope with a null track.
            element => element.TryGetProperty("track", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? ReadTrack(inner)
                : null,
            token);

    /// <summary>Walks Spotify's <c>next</c> cursor until the collection runs out.</summary>
    private async Task<IReadOnlyList<SpotifyTrack>> PageAsync(
        string firstUrl,
        Func<JsonElement, SpotifyTrack?> read,
        CancellationToken token)
    {
        var tracks = new List<SpotifyTrack>();
        var url = firstUrl;

        while (!string.IsNullOrEmpty(url))
        {
            using var document = await GetAsync(url, token);
            var root = document.RootElement;

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (read(item) is { } track) tracks.Add(track);
                }
            }

            url = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }

        if (tracks.Count == 0)
        {
            throw new SpotifyException("That Spotify link resolved to no playable tracks.");
        }

        return tracks;
    }

    private static SpotifyTrack? ReadTrack(JsonElement element)
    {
        if (!element.TryGetProperty("name", out var nameNode)) return null;
        var title = nameNode.GetString();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var artist = element.TryGetProperty("artists", out var artists) &&
                     artists.ValueKind == JsonValueKind.Array &&
                     artists.GetArrayLength() > 0 &&
                     artists[0].TryGetProperty("name", out var artistNode)
            ? artistNode.GetString() ?? ""
            : "";

        return new SpotifyTrack(title, artist);
    }
}
