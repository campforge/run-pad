#!/usr/bin/env bash
# Wraps a `dotnet publish -r linux-x64 --self-contained true` output folder into a portable
# Invoklet-x86_64.AppImage — single executable file, no install/root needed, no package-manager
# repository to maintain. Run from the repo root; needs appimagetool on PATH or at $APPIMAGETOOL.
#
# Usage: make-linux-appimage.sh <publish-dir> <output-dir>
# APPIMAGE_RUNTIME (optional): a runtime file to pack the AppImage with, instead of the latest one that
# appimagetool downloads on its own.
set -euo pipefail

PUBLISH_DIR="$1"
OUTPUT_DIR="$2"
APPDIR="$OUTPUT_DIR/Invoklet.AppDir"
APPIMAGETOOL="${APPIMAGETOOL:-appimagetool}"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"

cp -R "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Invoklet"

# NuGet extracts packages with the executable bit for the owner only (mode 0744), and publish copies the
# modes as they are. An AppImage is run by other users, and newer appimagetool builds refuse to pack an
# AppDir whose files are not readable, and executable where the owner can execute them, for everyone. Only
# the published files are changed (directories get the search bit, files that are executable for someone
# the bit for all); what this script puts next to them gets its mode below.
chmod -R a+rX "$APPDIR/usr/bin"

cp src/main/Invoklet/Assets/invoklet.desktop "$APPDIR/Invoklet.desktop"
cp src/main/Invoklet/Assets/invoklet.png "$APPDIR/invoklet.png"

cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "$HERE/usr/bin/Invoklet" "$@"
APPRUN
chmod 0755 "$APPDIR/AppRun" "$APPDIR" "$APPDIR/usr" "$APPDIR/usr/bin"
chmod 0644 "$APPDIR/Invoklet.desktop" "$APPDIR/invoklet.png"

RUNTIME_ARGS=()
if [ -n "${APPIMAGE_RUNTIME:-}" ]; then RUNTIME_ARGS=(--runtime-file "$APPIMAGE_RUNTIME"); fi

mkdir -p "$OUTPUT_DIR"
ARCH=x86_64 "$APPIMAGETOOL" ${RUNTIME_ARGS[@]+"${RUNTIME_ARGS[@]}"} "$APPDIR" "$OUTPUT_DIR/Invoklet-x86_64.AppImage"

echo "Built $OUTPUT_DIR/Invoklet-x86_64.AppImage"
