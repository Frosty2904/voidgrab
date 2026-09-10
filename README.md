# VoidGrab

A stylised Windows downloader for **YouTube**, **TikTok**, and audio matched from
**Spotify** links — in MP4, MKV, WEBM, MP3, M4A, FLAC, WAV, Opus, AAC, Vorbis or ALAC.

WPF on .NET 10, built around [yt-dlp](https://github.com/yt-dlp/yt-dlp) and
[ffmpeg](https://ffmpeg.org/).

---

## What it does

Paste one or more links, choose a format and quality, press **Add to queue**.
Downloads run one at a time with live progress, speed and time remaining, and a
log pane that shows exactly what the tool underneath is saying.

- **Playlists, albums and channels** expand into one queue row per item, so a row
  always means one file rather than a percentage secretly covering three hundred.
- **Cover art and metadata** are embedded by default.
- **Update yt-dlp** is a button. When a site changes how it serves media — which
  happens often — that button is usually the whole fix.

---

## Formats

| Video | Audio |
| --- | --- |
| MP4, MKV, WEBM | MP3, M4A, Opus, AAC, Vorbis, FLAC, WAV, ALAC |

**MP4 asks for H.264 + AAC first.** The genuinely highest-quality streams are now
usually AV1 or VP9 with Opus — smaller files that are still valid `.mp4`s, and
that Windows' built-in player, most TVs and most editors refuse to open. Anyone
choosing MP4 wants a file that plays, so the newer codecs are a fallback for when
nothing else is offered. **MKV and WEBM** get no such treatment: they are chosen
precisely to keep whatever the site considers best, with no re-encode.

**FLAC, WAV and ALAC are lossless containers holding a lossy source.** Nothing
here has lossless audio to give — YouTube does not serve it — so these re-encode
compressed audio into a much larger file without recovering anything the original
encoder discarded. They are available because real workflows ask for them, and
labelled in the UI so nobody chooses one expecting better sound.

---

## About Spotify

**Spotify audio cannot be downloaded, and VoidGrab does not try.** Spotify's
streams are protected by Widevine DRM; circumventing that is illegal in most
places and is out of scope for this project.

What a Spotify link actually does here:

1. The public Web API is asked for the **track names** on that link — title,
   artist, and every entry for an album or playlist.
2. Each name becomes a YouTube search, and the audio comes from YouTube like
   every other download in this app.

So the result is a **recording found by name, not the file Spotify would have
played**. Matching is imperfect: a live version, a cover, or the wrong edit is a
normal outcome rather than a bug. This is the same approach spotdl and similar
tools take, and the UI says so plainly rather than implying otherwise.

To enable it, create a free app at
[developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) and
paste the Client ID and Secret into the Spotify panel. Those credentials use the
Client Credentials flow, which reads only the public catalogue — it cannot reach
an account, library, or private playlist. They are stored as plain text in
`%APPDATA%\VoidGrab\settings.json`, which is why the app asks for that kind of
credential and no other.

---

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
.\build.ps1
```

Produces `publish\VoidGrab.exe` — a self-contained executable that needs no .NET
runtime installed.

```powershell
.\build.ps1 -Configuration Debug     # unoptimised build
dotnet run --project src\VoidGrab    # run from source
```

### Where yt-dlp and ffmpeg come from

They are **not** bundled. On first run the app downloads them into a `tools\`
folder beside the executable (~190 MB, once).

That is deliberate. yt-dlp needs replacing whenever a site changes something, and
a tool the app can overwrite in place is worth far more than one sealed inside a
single-file build and frozen until the next release. It also keeps launch
instant, since nothing has to be unpacked to temp on every start.

ffmpeg comes from [BtbN's builds](https://github.com/BtbN/FFmpeg-Builds) rather
than gyan.dev: the latter answers its documented download URL with a 303 to a
versioned file and then serves it at roughly 300 KB/s, where the GitHub asset is
a direct response measured at ~14 MB/s here. Those builds are GPL; they are
fetched by the user at runtime, not redistributed with this app.

---

## Checking an install

```powershell
.\VoidGrab.exe --selftest
```

Prints where the tools and settings live, how links are classified, and the exact
argument list it would hand yt-dlp for each format. Also written to
`selftest.txt` beside the executable.

```powershell
.\VoidGrab.exe --probe-download "<url>" mp3
```

Runs one real download through the app's own pipeline and reports whether
progress ticks arrived, whether conversion was detected, and what file came out.
Useful after changing argument construction, which is invisible from the UI until
something has already gone wrong.

---

## Known limitations

**YouTube formats above 720p may be missing.** yt-dlp now needs a JavaScript
runtime to reach YouTube's full format list, and warns when it cannot find one
(the warning appears in the log pane). Install [Deno](https://deno.com/) and it
is picked up automatically. Without it, downloads still work but may cap lower
than the quality you selected.

**Downloads are sequential.** Running them in parallel would make per-item speed
readings meaningless, have them fight for the same bandwidth, and give sites a
much better reason to rate limit.

---

## Layout

```
src/VoidGrab/
  Models/       formats, quality, jobs, settings, link classification
  Services/     tool provisioning, the yt-dlp process wrapper, Spotify metadata
  ViewModels/   queue state and the download pump
  Theme/        the Void palette and every control template
build.ps1       publishes the self-contained .exe
```

---

## A note on what you download

This is a tool, and what it is pointed at is your call. Your own uploads,
Creative Commons material, or anything you have permission to keep is
uncontroversial; redistributing other people's work generally is not. Bulk
downloading also runs against the terms of service of every site listed here.
