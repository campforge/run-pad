#!/usr/bin/env bash
# Packs an existing RunPad.app bundle (scripts/make-macos-app-bundle.sh) into a compressed, read-only
# disk image: the app, an Applications symlink to drag it onto, a short first-launch ReadMe, and the
# license agreement and GPL-3.0 text (copied from the bundle's Resources).
# Native hdiutil only, no Finder/GUI automation. The app must already carry a valid (ad-hoc) bundle
# signature from make-macos-app-bundle.sh — checked before anything is written, and again on the staged
# copy. Neither the app nor the image is Developer ID signed or notarized — the ReadMe says so and
# explains the Gatekeeper "Open Anyway" path; nothing here clears quarantine.
#
# Usage: make-macos-dmg.sh <path/to/RunPad.app> <output.dmg>
#
# The input bundle is validated before anything is written. Only <output.dmg> itself is replaced,
# after the new image is built and verified. Staging lives in a private hidden temp dir next to the
# output (removed on exit), so the final mv is a same-filesystem rename — atomic, never a copy over
# the old image.
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "usage: $0 <path/to/RunPad.app> <output.dmg>" >&2
    exit 2
fi

APP="${1%/}"
OUTPUT="$2"

fail()
{
    echo "error: $*" >&2
    exit 1
}

[[ -d "$APP" ]] || fail "'$APP' is not a directory"
[[ "$(basename "$APP")" == "RunPad.app" ]] || fail "'$APP' is not named RunPad.app"
[[ -f "$APP/Contents/MacOS/RunPad" && -x "$APP/Contents/MacOS/RunPad" ]] \
    || fail "'$APP' has no executable Contents/MacOS/RunPad"
[[ -f "$APP/Contents/Info.plist" ]] || fail "'$APP' has no Contents/Info.plist"
plutil -lint -s "$APP/Contents/Info.plist" || fail "'$APP/Contents/Info.plist' is not a valid property list"
[[ -s "$APP/Contents/Resources/RunPad.icns" ]] || fail "'$APP' has no Contents/Resources/RunPad.icns"
# Part of the publish output (RunPad.csproj); make-macos-app-bundle.sh moves them to Resources.
for DOC in EULA.txt LICENSE.txt; do
    [[ -s "$APP/Contents/Resources/$DOC" ]] || fail "'$APP' has no Contents/Resources/$DOC"
done
command -v codesign > /dev/null || fail "codesign not found — cannot validate the bundle signature"
codesign --verify --deep --strict --verbose=2 "$APP" \
    || fail "'$APP' has no valid bundle signature (build it with scripts/make-macos-app-bundle.sh)"

[[ "$OUTPUT" == *.dmg ]] || fail "output '$OUTPUT' must end in .dmg"
[[ ! -d "$OUTPUT" ]] || fail "output '$OUTPUT' is a directory"
OUTPUT_DIR="$(dirname "$OUTPUT")"
[[ -d "$OUTPUT_DIR" ]] || fail "output directory '$OUTPUT_DIR' does not exist"

WORK="$(mktemp -d "$OUTPUT_DIR/.runpad-dmg.XXXXXX")"
cleanup()
{
    [[ -n "${WORK:-}" && -d "$WORK" ]] && rm -rf "$WORK"
}
trap cleanup EXIT

STAGE="$WORK/RunPad"
mkdir "$STAGE"
# ditto keeps permissions, symlinks and extended attributes of the bundle as they are.
ditto "$APP" "$STAGE/RunPad.app"
codesign --verify --deep --strict "$STAGE/RunPad.app" || fail "staged copy of '$APP' fails signature verification"
ln -s /Applications "$STAGE/Applications"
cp "$APP/Contents/Resources/EULA.txt" "$STAGE/License Agreement.txt"
cp "$APP/Contents/Resources/LICENSE.txt" "$STAGE/GPL-3.0.txt"

cat > "$STAGE/Read Me First.txt" <<'README'
RunPad — installing from this disk image
========================================

1. Read "License Agreement.txt" in this window. RunPad is free software under the GNU GPL v3
   ("GPL-3.0.txt") and is provided as is, without warranty; by installing or using it you
   accept that agreement.

2. Pick the right download. RunPad-osx-arm64.dmg is for Apple Silicon Macs (M1 and later),
   RunPad-osx-x64.dmg is for Intel Macs. Apple menu > About This Mac shows which one you have.

3. Drag RunPad.app onto the Applications folder in this window.

4. Eject the "RunPad" disk image (Finder sidebar, or drag it to the Trash), then start RunPad
   from your Applications folder — not from the disk image.

First launch: this build is not notarized
-----------------------------------------
RunPad is an open-source project without an Apple Developer certificate: the app is only ad-hoc
signed (which lets macOS check it is intact) and is not notarized by Apple, so on the first launch
macOS may say it cannot verify the developer and refuse to open it. If you trust where you
downloaded it from:

  - Close the warning, open System Settings > Privacy & Security, scroll to the Security
    section and click "Open Anyway" next to the message about RunPad, then confirm.
  - macOS shows that button only for a while after a blocked launch attempt, and depending on
    your macOS version and security settings it may not be offered at all.

Only do this for a copy downloaded from the official releases page:
https://github.com/Campforge/run-pad/releases
README

TMP_DMG="$WORK/output.dmg"
hdiutil create -quiet -volname "RunPad" -srcfolder "$STAGE" -fs HFS+ -format UDZO \
    -imagekey zlib-level=9 "$TMP_DMG"
hdiutil verify -quiet "$TMP_DMG"

mv -f "$TMP_DMG" "$OUTPUT"
echo "Built $OUTPUT"
