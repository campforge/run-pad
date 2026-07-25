#!/usr/bin/env bash
# Wraps a `dotnet publish -r linux-x64 --self-contained true` output folder into a real .deb package
# — built by hand (ar + tar, matching the real .deb binary format exactly: a "debian-binary" version
# marker plus control.tar.gz/data.tar.gz, combined into one `ar` archive) rather than shelling out to
# dpkg-deb, so the same script also works on non-Debian build machines that don't have dpkg-deb
# installed (verified directly — this repo's own dev/CI sandbox is Fedora-based and doesn't have it).
# Run from the repo root.
#
# Usage: make-linux-deb.sh <publish-dir> <version> <output-dir>
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
OUTPUT_DIR="$3"
STAGING="$OUTPUT_DIR/deb-staging"

rm -rf "$STAGING"
mkdir -p "$STAGING/usr/lib/dotpad" "$STAGING/usr/bin" \
    "$STAGING/usr/share/applications" "$STAGING/usr/share/icons/hicolor/256x256/apps" \
    "$STAGING/DEBIAN"

cp -R "$PUBLISH_DIR"/* "$STAGING/usr/lib/dotpad/"
chmod +x "$STAGING/usr/lib/dotpad/DotPad"
ln -s /usr/lib/dotpad/DotPad "$STAGING/usr/bin/dotpad"
cp src/main/DotPad/Assets/dotpad.desktop "$STAGING/usr/share/applications/dotpad.desktop"
cp src/main/DotPad/Assets/dotpad.png "$STAGING/usr/share/icons/hicolor/256x256/apps/dotpad.png"

INSTALLED_SIZE_KB=$(du -sk "$STAGING/usr" | cut -f1)
sed -e "s/__VERSION__/$VERSION/" src/main/DotPad/Assets/dotpad.control > "$STAGING/DEBIAN/control"
echo "Installed-Size: $INSTALLED_SIZE_KB" >> "$STAGING/DEBIAN/control"

# The real .deb binary format: an `ar` archive of exactly debian-binary, control.tar.gz, data.tar.gz,
# in that order. --owner=root --group=root records root ownership in the tar headers without actually
# needing to run as root (the same trick dpkg-deb's own --root-owner-group flag uses).
echo "2.0" > "$STAGING/debian-binary"
tar -czf "$STAGING/control.tar.gz" --owner=root --group=root --numeric-owner -C "$STAGING/DEBIAN" .
tar -czf "$STAGING/data.tar.gz" --owner=root --group=root --numeric-owner \
    -C "$STAGING" usr

DEB_PATH="$(cd "$OUTPUT_DIR" && pwd)/dotpad_${VERSION}_amd64.deb"
rm -f "$DEB_PATH"
( cd "$STAGING" && ar rc "$DEB_PATH" debian-binary control.tar.gz data.tar.gz )

echo "Built $DEB_PATH"
