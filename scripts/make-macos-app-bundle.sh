#!/usr/bin/env bash
# Wraps a `dotnet publish -r osx-x64/osx-arm64 --self-contained true` output folder into a real
# RunPad.app bundle (Contents/MacOS + Contents/Resources + Info.plist), so it's double-clickable and
# shows up properly in Finder/Dock instead of being a flat folder of files. Run from the repo root.
#
# Usage: make-macos-app-bundle.sh <publish-dir> <output-dir>
set -euo pipefail

PUBLISH_DIR="$1"
OUTPUT_DIR="$2"
APP="$OUTPUT_DIR/RunPad.app"

VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' src/main/RunPad/RunPad.csproj)

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR"/* "$APP/Contents/MacOS/"
cp src/main/RunPad/Assets/RunPad.icns "$APP/Contents/Resources/RunPad.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>RunPad</string>
    <key>CFBundleDisplayName</key>
    <string>RunPad</string>
    <key>CFBundleIdentifier</key>
    <string>com.coddwrench.runpad</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>RunPad</string>
    <key>CFBundleIconFile</key>
    <string>RunPad.icns</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/RunPad"

echo "Built $APP (version $VERSION)"
