#!/usr/bin/env python3
"""Generates Offstream's .ico assets.

The icons are checked in, because the build must not depend on Python. This script is
checked in too, so they are reproducible rather than opaque binaries nobody can amend:
change a colour here, re-run, commit the result.

    python3 tools/generate-icons.py

The mark is a record ring: a heavy annulus with a filled centre. It is the one shape that
survives 16x16 - the size that actually matters, because that is the tray - and it says
"recording" without a legend. State is carried by colour alone, so the two tray icons are
the same shape at the same weight and only the fill changes; anything that also changed the
silhouette would read as the icon being replaced rather than the state having moved.

Pure standard library on purpose: PNG is zlib plus four chunks, and ICO is a directory of
PNGs, so a pillow dependency would buy nothing but an install step.
"""

import struct
import zlib
from pathlib import Path

# Supersampling factor. Every shape is rasterised at N times the target size and box-filtered
# down, which is what gives the ring a clean edge at 16px without hand-tuned hinting.
SS = 8

# The palette. Amber for the app itself, because a red app icon in the taskbar reads as an
# error state when nothing is wrong; red is reserved for the one moment it means something.
AMBER = (0xE8, 0x91, 0x3A)
SLATE = (0x8A, 0x96, 0xA6)
RED = (0xE0, 0x3A, 0x3A)

# Windows 11 only (CLAUDE.md), so PNG-compressed entries are safe at every size; the BMP
# fallback older shells wanted is dead weight here.
SIZES = (16, 24, 32, 48, 64, 128, 256)


def render(size: int, colour: tuple[int, int, int]) -> bytes:
    """Rasterises the record ring at `size` px, returning RGBA bytes."""
    n = size * SS
    centre = (n - 1) / 2.0

    # Proportions are fractions of the icon box, so every size is the same drawing rather
    # than seven drawings that happen to look similar.
    outer = n * 0.44
    inner = n * 0.30
    core = n * 0.17

    r, g, b = colour
    row_cache: dict[int, bytes] = {}
    rows = []

    for y in range(size):
        # The mark is symmetric about its horizontal axis, so the bottom half is the top
        # half mirrored - halves the work at 256px, where it is actually noticeable.
        mirror = size - 1 - y
        if mirror in row_cache:
            rows.append(row_cache[mirror])
            continue

        row = bytearray()
        for x in range(size):
            covered = 0
            for sy in range(SS):
                fy = y * SS + sy - centre
                fy2 = fy * fy
                for sx in range(SS):
                    fx = x * SS + sx - centre
                    d2 = fx * fx + fy2
                    # Inside the annulus, or inside the core dot.
                    if (inner * inner <= d2 <= outer * outer) or d2 <= core * core:
                        covered += 1

            alpha = (covered * 255) // (SS * SS)
            row += bytes((r, g, b, alpha))

        packed = bytes(row)
        row_cache[y] = packed
        rows.append(packed)

    return b"".join(rows)


def png(size: int, pixels: bytes) -> bytes:
    """Wraps raw RGBA rows in a PNG container."""
    stride = size * 4
    # Filter type 0 (None) in front of every scanline. Real filters would compress better,
    # but a 256px icon is already a few KB and the simplicity is worth more here.
    raw = b"".join(b"\x00" + pixels[y * stride:(y + 1) * stride] for y in range(size))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def ico(images: list[tuple[int, bytes]]) -> bytes:
    """Packs PNGs into an ICO directory."""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)

    entries = bytearray()
    for size, data in images:
        # 256 is stored as 0 in the directory - the field is one byte.
        entries += struct.pack(
            "<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset
        )
        offset += len(data)

    return bytes(header + entries + b"".join(data for _, data in images))


def build(path: Path, colour: tuple[int, int, int], sizes: tuple[int, ...]) -> None:
    path.write_bytes(ico([(s, png(s, render(s, colour))) for s in sizes]))
    print(f"{path}  ({path.stat().st_size:,} bytes)")


def main() -> None:
    assets = Path(__file__).resolve().parent.parent / "src" / "Offstream.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)

    # The app icon needs every size: Explorer, alt-tab and the installer all pick different
    # ones. The tray only ever asks for the small end, so the state icons stop at 48.
    build(assets / "offstream.ico", AMBER, SIZES)
    build(assets / "tray-idle.ico", SLATE, (16, 20, 24, 32, 48))
    build(assets / "tray-recording.ico", RED, (16, 20, 24, 32, 48))


if __name__ == "__main__":
    main()
