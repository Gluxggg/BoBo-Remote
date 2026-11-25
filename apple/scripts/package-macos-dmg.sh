#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
MAC_PKG="$ROOT_DIR/apple/BoBoRemoteMac"
OUT_DIR="$ROOT_DIR/dist-apple"
APP_BUNDLE="$OUT_DIR/BoBoRemote.app"
DMG_PATH="$OUT_DIR/BoBoRemote-macOS.dmg"

mkdir -p "$OUT_DIR"
rm -rf "$APP_BUNDLE" "$DMG_PATH"

echo "=== Building BoBoRemoteMac (Release) ==="
cd "$MAC_PKG"
swift build -c release

BIN_PATH="$(swift build -c release --show-bin-path)/BoBoRemoteMac"
if [[ ! -f "$BIN_PATH" ]]; then
  echo "ERROR: built binary not found at $BIN_PATH"
  exit 1
fi

echo "=== Assembling BoBoRemote.app ==="
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"
cp "$BIN_PATH" "$APP_BUNDLE/Contents/MacOS/BoBoRemoteMac"
cp "$MAC_PKG/Resources/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
chmod +x "$APP_BUNDLE/Contents/MacOS/BoBoRemoteMac"

# Ad-hoc sign so Gatekeeper / ScreenCaptureKit / Accessibility permissions persist across launches
codesign --force --deep --sign - "$APP_BUNDLE"

echo "=== Creating BoBoRemote-macOS.dmg ==="
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT
cp -R "$APP_BUNDLE" "$STAGE_DIR/BoBoRemote.app"
ln -s /Applications "$STAGE_DIR/Applications"
hdiutil create -volname "BoBoRemote" -srcfolder "$STAGE_DIR" -ov -format UDZO "$DMG_PATH"

ls -lh "$DMG_PATH"
