# VoidGrab

A stylised downloader for **YouTube**, **TikTok**, and audio matched from
**Spotify** links — in MP4, MKV, WEBM, MP3, M4A, FLAC, WAV, Opus, AAC, Vorbis or ALAC.

Runs on **Windows** and **macOS**, built around [yt-dlp](https://github.com/yt-dlp/yt-dlp)
and [ffmpeg](https://ffmpeg.org/).

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

## Installing

### Windows

Run `build.ps1` and use `publish\VoidGrab.exe`. Nothing else to install.

### macOS

Download the tarball for your chip, then:

```bash
tar -xzf VoidGrab-macos-arm64.tar.gz     # or -x64 on an Intel Mac
xattr -dr com.apple.quarantine VoidGrab.app
open VoidGrab.app
```

**That middle line is not optional.** These builds are unsigned — signing and
notarising require an Apple Developer ID and a Mac to run the tooling on, and I
had neither. Without it, macOS quarantines the app and refuses to open it with a
message that suggests the file is damaged, which it is not. The command clears
the quarantine flag that Safari or Chrome attached on download.

| Your Mac | Tarball |
| --- | --- |
| Apple silicon (M1–M4) | `VoidGrab-macos-arm64.tar.gz` |
| Intel | `VoidGrab-macos-x64.tar.gz` |

**ffmpeg on macOS.** VoidGrab uses an ffmpeg already on your machine if it finds
one — Homebrew's `/opt/homebrew/bin` and `/usr/local/bin`, MacPorts, or anything
on `PATH`. That is the better outcome: a Homebrew ffmpeg on Apple silicon is
native arm64, where the copy VoidGrab downloads for itself is an x86_64 build
that runs through Rosetta. If you want the faster one:

```bash
brew install ffmpeg
```

Otherwise VoidGrab fetches its own on first run and everything still works.

---

## Two UIs, one core

WPF has no implementation outside Windows, so the Mac build could never be a
matter of retargeting the existing project — the UI had to be rebuilt on a
toolkit that runs there.

| Project | What it is |
| --- | --- |
| `VoidGrab.Core` | Everything that is not a window: models, the yt-dlp process wrapper, Spotify metadata, tool provisioning, queue state. Plain `net10.0`. |
| `VoidGrab` | The Windows head. WPF, hand-templated to the Void palette. |
| `VoidGrab.Desktop` | The cross-platform head. Avalonia — macOS, Linux, and Windows if you want it. |

Core knows nothing about either. The two things it genuinely cannot do portably
— show a folder picker and reveal a directory — sit behind `IPlatformServices`,
which each head implements in about twenty lines.

The Avalonia head layers the Void palette over Fluent's dark theme rather than
re-templating every control the way the WPF head does. Fluent's dark defaults are
already close, and controls that look native on macOS are worth more than an
exact pixel match with Windows.

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
an account, library, or private playlist. They are stored as plain text in the
settings file, which is why the app asks for that kind of credential and no other.

---

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\build.ps1        # Windows  -> publish\VoidGrab.exe
.\build-mac.ps1    # macOS    -> artifacts\VoidGrab-macos-{arm64,x64}.tar.gz
```

`build-mac.ps1` cross-compiles from Windows and needs Python available, because
the `.app` bundle is assembled by `scripts/package-macos.py`.

**Why a script and not `Compress-Archive`.** NTFS has no executable bit, and a
zip written on Windows carries no Unix mode. An `.app` packed that way arrives on
a Mac with a non-executable binary and fails to launch with no useful error. The
packaging script writes a tarball with each entry's mode set explicitly — 0755
for the app host and native libraries, 0644 for everything else — and generates
the `.icns` icon so the mark stays in sync with the one the app draws in its own
header.

### Where yt-dlp and ffmpeg come from

They are **not** bundled. On first run the app downloads them into a `tools`
folder beside itself.

That is deliberate. yt-dlp needs replacing whenever a site changes something, and
a tool the app can overwrite in place is worth far more than one sealed inside a
build and frozen until the next release. It also keeps launch instant, since
nothing has to be unpacked to temp on every start.

On Windows, ffmpeg comes from [BtbN's builds](https://github.com/BtbN/FFmpeg-Builds)
rather than gyan.dev: the latter answers its documented download URL with a 303
to a versioned file and then serves it at roughly 300 KB/s, where the GitHub
asset is a direct response measured at ~14 MB/s here. On macOS an installed
ffmpeg is preferred, falling back to [evermeet.cx](https://evermeet.cx/ffmpeg/).
Those builds are GPL and x86_64; they are fetched by the user at runtime, not
redistributed with this app.

---

## Checking an install

Both heads take the same diagnostics, and both run headlessly — no window, so
they work over SSH.

```bash
VoidGrab.app/Contents/MacOS/VoidGrab --selftest
VoidGrab.app/Contents/MacOS/VoidGrab --fetch-tools
VoidGrab.app/Contents/MacOS/VoidGrab --probe-download "<url>" mp3
```

`--selftest` prints the detected OS and architecture, where the tools and
settings live, how links are classified, and the exact argument list it would
hand yt-dlp for each format. `--fetch-tools` downloads yt-dlp and ffmpeg without
opening the app. `--probe-download` runs one real download through the app's own
pipeline and reports whether progress ticks arrived, whether conversion was
detected, and what file came out.

---

## Known limitations

**The macOS build has not been run on a Mac.** It was cross-compiled from
Windows; the binaries are verified as Mach-O for the right architectures and the
bundle layout and file modes are verified inside the tarball, but nobody has
watched the window open. The shared core and every binding are exercised by the
Windows Avalonia build, which is the same XAML — but treat the first Mac launch
as the real test.

**The macOS builds are unsigned.** See the `xattr` step above. Distributing
without that friction needs an Apple Developer ID and a Mac to notarise on.

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
src/VoidGrab.Core/      models, tool provisioning, yt-dlp wrapper, Spotify, queue state
src/VoidGrab/           the WPF head (Windows)
src/VoidGrab.Desktop/   the Avalonia head (macOS, Linux, Windows)
scripts/                macOS .app assembly and icon generation
build.ps1               publishes the Windows .exe
build-mac.ps1           publishes and packages the macOS .app bundles
```

---

## A note on what you download

This is a tool, and what it is pointed at is your call. Your own uploads,
Creative Commons material, or anything you have permission to keep is
uncontroversial; redistributing other people's work generally is not. Bulk
downloading also runs against the terms of service of every site listed here.
