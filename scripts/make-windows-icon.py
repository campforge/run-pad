#!/usr/bin/env python3
"""Builds src/main/Invoklet/Assets/icon.ico from icon.svg (needs rsvg-convert).

icon.svg keeps the margin of the macOS icon grid (about 10% on each side). Windows draws every frame of an
icon edge to edge in the taskbar, the title bar and the Start menu, so the same artwork looks small there:
the .ico is rendered from the surface alone, with a hair of margin so the antialiased edge is not cut.

The small frames (16 to 48 pixels: the title bar, the taskbar, Alt+Tab) are cut closer, into the corners of the surface, so
the hammer and the grid take more of the few pixels there are; the corners of the plate are only slightly rounded there.
"""
import pathlib
import re
import struct
import subprocess
import sys
import tempfile

SIZES = [16, 24, 32, 48, 64, 128, 256]
VIEW_BOX = "98 98 828 828"
SMALL_VIEW_BOX = "140 140 744 744"
SMALL_UP_TO = 48

assets = pathlib.Path(__file__).resolve().parent.parent / "src/main/Invoklet/Assets"
original = (assets / "icon.svg").read_text(encoding="utf-8")
if 'viewBox="0 0 1024 1024"' not in original:
    sys.exit("icon.svg no longer has the viewBox this script crops")

with tempfile.TemporaryDirectory() as tmp:
    sources = {}
    for name, view_box in (("large", VIEW_BOX), ("small", SMALL_VIEW_BOX)):
        sources[name] = pathlib.Path(tmp) / f"{name}.svg"
        sources[name].write_text(original.replace('viewBox="0 0 1024 1024"', f'viewBox="{view_box}"', 1), encoding="utf-8")
    frames = []
    for size in SIZES:
        png = pathlib.Path(tmp) / f"{size}.png"
        source = sources["small" if size <= SMALL_UP_TO else "large"]
        subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), str(source), "-o", str(png)], check=True)
        frames.append(png.read_bytes())

offset = 6 + 16 * len(SIZES)
entries, body = b"", b""
for size, frame in zip(SIZES, frames):
    entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(frame), offset + len(body))
    body += frame
(assets / "icon.ico").write_bytes(struct.pack("<HHH", 0, 1, len(SIZES)) + entries + body)
