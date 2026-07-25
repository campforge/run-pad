#!/usr/bin/env bash
# Wraps a `dotnet publish -r linux-x64 --self-contained true` output folder into a portable
# DotPad-x86_64.AppImage — single executable file, no install/root needed, no package-manager
# repository to maintain. Run from the repo root; needs appimagetool on PATH or at $APPIMAGETOOL.
#
# Usage: make-linux-appimage.sh <publish-dir> <output-dir>
set -euo pipefail

PUBLISH_DIR="$1"
OUTPUT_DIR="$2"
APPDIR="$OUTPUT_DIR/DotPad.AppDir"
APPIMAGETOOL="${APPIMAGETOOL:-appimagetool}"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"

cp -R "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/DotPad"

cp src/main/DotPad/Assets/dotpad.desktop "$APPDIR/DotPad.desktop"
cp src/main/DotPad/Assets/dotpad.png "$APPDIR/dotpad.png"

cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "$HERE/usr/bin/DotPad" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

mkdir -p "$OUTPUT_DIR"
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUTPUT_DIR/DotPad-x86_64.AppImage"

echo "Built $OUTPUT_DIR/DotPad-x86_64.AppImage"
