#!/usr/bin/env bash
# Wraps a `dotnet publish -r osx-x64/osx-arm64 --self-contained true` output folder into a real
# RunPad.app bundle (Contents/MacOS + Contents/Resources + Info.plist), so it's double-clickable and
# shows up properly in Finder/Dock instead of being a flat folder of files. Run from the repo root.
#
# The finished bundle is ad-hoc signed (`codesign --sign -`, no Developer ID) as the very last step:
# the SDK apphost copied in as Contents/MacOS/RunPad only carries the linker's ad-hoc signature for a
# bare "apphost" binary (no Info.plist bound, no sealed resources), which macOS reports as a damaged
# app once the bundle is downloaded. Ad-hoc signing seals the bundle's contents; it is NOT a Developer
# ID signature or notarization, so Gatekeeper may still ask the user to allow the first launch.
# Nothing may modify the bundle after signing.
#
# Usage: make-macos-app-bundle.sh <publish-dir> <output-dir>
set -euo pipefail

PUBLISH_DIR="$1"
OUTPUT_DIR="$2"
APP="$OUTPUT_DIR/RunPad.app"

if ! command -v codesign > /dev/null; then
    echo "error: codesign not found — the bundle must be built on macOS with the Xcode command line tools" >&2
    exit 1
fi

CSPROJ=src/main/RunPad/RunPad.csproj
# Stock awk (not GNU grep -P) so this runs on macOS runners; takes the first <Version> element.
VERSION=$(awk -F'</?Version>' 'NF > 2 { print $2; exit }' "$CSPROJ")
if [[ -z "$VERSION" ]]; then
    echo "error: no <Version> found in $CSPROJ" >&2
    exit 1
fi

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

# codesign treats every file under Contents/MacOS as nested code, so strict verification rejects the
# publish output's managed assemblies and data files (System.*.dll, RunPad.deps.json, …) and its
# satellite folders (de/, zh-Hans/, … holding *.resources.dll) as "code object is not signed at all".
# Only Mach-O binaries stay in Contents/MacOS; every other top-level file and each culture folder is
# moved to Contents/Resources and replaced by a relative symlink, which is sealed as-is, so the .NET
# host and runtime still find <app dir>/X.dll and <app dir>/<culture>/X.resources.dll at their
# original paths. Existing symlinks are left as they are; any other kind of subfolder, or a name
# that already exists in Contents/Resources, fails the build instead of guessing.
relocate_to_resources()
{
    local NAME="$1"
    if [[ -e "$APP/Contents/Resources/$NAME" || -L "$APP/Contents/Resources/$NAME" ]]; then
        echo "error: cannot move Contents/MacOS/$NAME: Contents/Resources/$NAME already exists" >&2
        exit 1
    fi
    mv "$APP/Contents/MacOS/$NAME" "$APP/Contents/Resources/$NAME"
    ln -s "../Resources/$NAME" "$APP/Contents/MacOS/$NAME"
}

for ENTRY in "$APP/Contents/MacOS"/*; do
    NAME="$(basename "$ENTRY")"
    if [[ -L "$ENTRY" ]]; then
        continue
    elif [[ -d "$ENTRY" ]]; then
        if [[ -n "$(find "$ENTRY" -mindepth 1 ! \( -type f -name '*.resources.dll' \) -print -quit)" ]]; then
            echo "error: Contents/MacOS/$NAME is not a satellite resource folder (only *.resources.dll expected)" >&2
            exit 1
        fi
        relocate_to_resources "$NAME"
    elif [[ -f "$ENTRY" && "$(file -b "$ENTRY")" != *Mach-O* ]]; then
        relocate_to_resources "$NAME"
    fi
done
[[ -f "$APP/Contents/MacOS/RunPad" && ! -L "$APP/Contents/MacOS/RunPad" ]] \
    || { echo "error: Contents/MacOS/RunPad is not a Mach-O executable" >&2; exit 1; }

# Sign inside-out, no --deep: first every nested Mach-O (the runtime/native .dylibs, createdump, …)
# next to the main executable, then the bundle itself, which signs Contents/MacOS/RunPad with the
# Info.plist bound and seals Contents/Resources (managed .dlls, data, satellites, icon) and the
# symlinks as resources.
while IFS= read -r -d '' FILE; do
    [[ "$FILE" == "$APP/Contents/MacOS/RunPad" ]] && continue
    if [[ "$(file -b "$FILE")" == *Mach-O* ]]; then
        codesign --force --sign - "$FILE"
    fi
done < <(find "$APP/Contents" -type f -print0)
codesign --force --sign - "$APP"

# Fail the build rather than ship a bundle macOS would reject.
codesign --verify --deep --strict --verbose=2 "$APP"

echo "Built and ad-hoc signed $APP (version $VERSION)"
