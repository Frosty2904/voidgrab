"""
Assemble a macOS .app bundle (and a distributable tarball) from a publish output.

    python scripts/package-macos.py artifacts/osx-arm64 --version 1.0.0

Why this is a script and not a few shell lines
----------------------------------------------
Two things have to be right, and neither survives being done on Windows the
obvious way:

1. **The executable bit.** NTFS has no concept of it, and a .zip written on
   Windows carries no Unix mode, so an .app packed that way arrives on a Mac
   with a non-executable binary and fails to launch with no useful error. The
   tarball here is written with `tarfile`, which lets each entry's mode be set
   explicitly — 0755 for the app host and native libraries, 0644 for the rest.

2. **The bundle layout.** macOS will not treat a directory as an application
   without Contents/Info.plist naming a CFBundleExecutable that exists in
   Contents/MacOS.

The icon is generated here too, rather than committed as a binary blob, so the
mark stays in sync with the one the app draws in its own header.
"""

from __future__ import annotations

import argparse
import plistlib
import struct
import tarfile
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw

BUNDLE_ID = "uk.co.alfienash.voidgrab"
APP_NAME = "VoidGrab"

CANVAS = (6, 10, 21, 255)       # #060A15 - the app's own background
ACCENT = (0, 216, 239, 255)     # #00D8EF - neon cyan
FLARE = (180, 125, 255, 255)    # #B47DFF - violet

# icns type -> pixel size. PNG-backed types, supported by macOS 10.7 and later.
ICNS_TYPES = [
    (b"ic11", 32),
    (b"ic12", 64),
    (b"ic07", 128),
    (b"ic13", 256),
    (b"ic08", 256),
    (b"ic14", 512),
    (b"ic09", 512),
    (b"ic10", 1024),
]

# Extensions that must stay executable inside the bundle.
EXECUTABLE_SUFFIXES = {".dylib", ".so", ".a"}
EXECUTABLE_NAMES = {APP_NAME, "createdump", "singlefilehost"}


def draw_icon(size: int) -> Image.Image:
    """The VoidGrab mark: a neon diamond on the void, matching the app header."""
    # Supersample, then downscale — the diamond's diagonals alias badly otherwise.
    scale = 4
    px = size * scale
    image = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    radius = int(px * 0.22)
    draw.rounded_rectangle([0, 0, px - 1, px - 1], radius=radius, fill=CANVAS)

    # A soft corona under the diamond, built from concentric translucent rings
    # rather than a blur pass, which keeps this dependency-light.
    centre = px / 2
    glow_max = px * 0.40
    for step in range(14, 0, -1):
        extent = glow_max * (step / 14)
        alpha = int(16 * (1 - step / 14) ** 1.5) + 2
        draw.ellipse(
            [centre - extent, centre - extent, centre + extent, centre + extent],
            fill=(ACCENT[0], ACCENT[1], ACCENT[2], alpha),
        )

    # The diamond itself, filled with a cyan-to-violet ramp drawn as a gradient
    # image and masked to the rotated square.
    half = px * 0.27
    points = [
        (centre, centre - half),
        (centre + half, centre),
        (centre, centre + half),
        (centre - half, centre),
    ]

    gradient = Image.new("RGBA", (px, px))
    grad_draw = ImageDraw.Draw(gradient)
    for x in range(px):
        t = x / max(px - 1, 1)
        grad_draw.line(
            [(x, 0), (x, px)],
            fill=(
                round(ACCENT[0] + (FLARE[0] - ACCENT[0]) * t),
                round(ACCENT[1] + (FLARE[1] - ACCENT[1]) * t),
                round(ACCENT[2] + (FLARE[2] - ACCENT[2]) * t),
                255,
            ),
        )

    mask = Image.new("L", (px, px), 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    image.paste(gradient, (0, 0), mask)

    return image.resize((size, size), Image.LANCZOS)


def build_icns() -> bytes:
    """
    Pack the rendered sizes into an .icns container.

    The format is a 'icns' magic plus a total length, then one chunk per size:
    a four-character type, a big-endian length that *includes* its own 8-byte
    header, and the PNG payload.
    """
    chunks = bytearray()

    for icns_type, size in ICNS_TYPES:
        buffer = BytesIO()
        draw_icon(size).save(buffer, format="PNG")
        payload = buffer.getvalue()
        chunks += icns_type + struct.pack(">I", len(payload) + 8) + payload

    return b"icns" + struct.pack(">I", len(chunks) + 8) + bytes(chunks)


def build_plist(version: str) -> bytes:
    return plistlib.dumps(
        {
            "CFBundleName": APP_NAME,
            "CFBundleDisplayName": APP_NAME,
            "CFBundleIdentifier": BUNDLE_ID,
            "CFBundleExecutable": APP_NAME,
            "CFBundleIconFile": "VoidGrab.icns",
            "CFBundlePackageType": "APPL",
            "CFBundleVersion": version,
            "CFBundleShortVersionString": version,
            "CFBundleInfoDictionaryVersion": "6.0",
            "LSMinimumSystemVersion": "12.0",
            "NSHighResolutionCapable": True,
            # Not an agent: this app owns a window and belongs in the Dock.
            "LSUIElement": False,
            "NSHumanReadableCopyright": "Uses yt-dlp and ffmpeg, downloaded at runtime.",
        }
    )


def mode_for(relative: Path) -> int:
    if relative.name in EXECUTABLE_NAMES or relative.suffix in EXECUTABLE_SUFFIXES:
        return 0o755
    return 0o644


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish_dir", type=Path, help="a dotnet publish output directory")
    parser.add_argument("--version", default="1.0.0")
    parser.add_argument("--out", type=Path, default=None, help="where to write the tarball")
    args = parser.parse_args()

    publish = args.publish_dir.resolve()
    if not publish.is_dir():
        raise SystemExit(f"Not a directory: {publish}")

    host = publish / APP_NAME
    if not host.is_file():
        raise SystemExit(f"No app host at {host} - was this published for an osx- runtime?")

    app_root = f"{APP_NAME}.app"
    contents = f"{app_root}/Contents"
    macos_dir = f"{contents}/MacOS"
    resources = f"{contents}/Resources"

    out_path = args.out or publish.parent / f"{APP_NAME}-macos-{publish.name.removeprefix('osx-')}.tar.gz"
    out_path.parent.mkdir(parents=True, exist_ok=True)

    icns = build_icns()
    plist = build_plist(args.version)

    def add_bytes(tar: tarfile.TarFile, name: str, payload: bytes, mode: int) -> None:
        info = tarfile.TarInfo(name)
        info.size = len(payload)
        info.mode = mode
        tar.addfile(info, BytesIO(payload))

    def add_dir(tar: tarfile.TarFile, name: str) -> None:
        info = tarfile.TarInfo(name)
        info.type = tarfile.DIRTYPE
        info.mode = 0o755
        tar.addfile(info)

    file_count = 0
    executables = []

    with tarfile.open(out_path, "w:gz") as tar:
        for directory in (app_root, contents, macos_dir, resources):
            add_dir(tar, directory)

        add_bytes(tar, f"{contents}/Info.plist", plist, 0o644)
        add_bytes(tar, f"{resources}/VoidGrab.icns", icns, 0o644)

        for source in sorted(publish.rglob("*")):
            if source.is_dir():
                continue

            relative = source.relative_to(publish)
            mode = mode_for(relative)
            if mode == 0o755:
                executables.append(str(relative))

            info = tarfile.TarInfo(f"{macos_dir}/{relative.as_posix()}")
            info.size = source.stat().st_size
            info.mode = mode
            with source.open("rb") as handle:
                tar.addfile(info, handle)
            file_count += 1

    size_mb = out_path.stat().st_size / (1024 * 1024)
    print(f"Built {out_path}  ({size_mb:.0f} MB, {file_count} files)")
    print(f"  bundle       : {app_root}")
    print(f"  identifier   : {BUNDLE_ID}")
    print(f"  icon         : {len(icns) / 1024:.0f} KB, {len(ICNS_TYPES)} sizes")
    print(f"  marked +x    : {len(executables)} ({', '.join(executables[:4])}"
          f"{'…' if len(executables) > 4 else ''})")


if __name__ == "__main__":
    main()
