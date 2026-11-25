#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
MAC_PKG="$ROOT_DIR/apple/BoBoRemoteMac"
OUT_DIR="$ROOT_DIR/dist-apple"
APP_BUNDLE="$OUT_DIR/BoBoRemote.app"
DMG_PATH="$OUT_DIR/BoBoRemote-macOS.dmg"
RELEASE_VERSION="${BOBO_RELEASE_VERSION:-}"

if [[ -n "$RELEASE_VERSION" && ! "$RELEASE_VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo 'BOBO_RELEASE_VERSION must have the form MAJOR.MINOR.PATCH.' >&2
  exit 1
fi
if [[ -n "$RELEASE_VERSION" ]]; then
  IFS=. read -r major minor patch <<< "$RELEASE_VERSION"
  RELEASE_BUILD=$((major * 10000 + minor * 100 + patch - 59580))
  if (( minor > 99 || patch > 99 || RELEASE_BUILD < 1 )); then
    echo 'The version cannot be mapped to a valid bundle build number.' >&2
    exit 1
  fi
fi

mkdir -p "$OUT_DIR"
WORK_DIR="$(mktemp -d "$OUT_DIR/.macos-package.XXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT
STAGE_DIR="$WORK_DIR/stage"
STAGED_APP="$STAGE_DIR/BoBoRemote.app"
PLIST="$STAGED_APP/Contents/Info.plist"

cd "$MAC_PKG"
echo '=== Building BoBoRemoteMac (Release, arm64 and x86_64) ==='
BUILD_ARGS=(-c release --arch arm64 --arch x86_64)
swift build "${BUILD_ARGS[@]}"
BIN_PATH="$(swift build "${BUILD_ARGS[@]}" --show-bin-path)/BoBoRemoteMac"
if [[ ! -f "$BIN_PATH" ]]; then
  echo "Built binary not found at $BIN_PATH" >&2
  exit 1
fi
lipo "$BIN_PATH" -verify_arch arm64 x86_64

echo '=== Assembling BoBoRemote.app ==='
mkdir -p "$STAGED_APP/Contents/MacOS" "$STAGED_APP/Contents/Resources"
cp "$BIN_PATH" "$STAGED_APP/Contents/MacOS/BoBoRemoteMac"
cp "$MAC_PKG/Resources/Info.plist" "$PLIST"
chmod +x "$STAGED_APP/Contents/MacOS/BoBoRemoteMac"
if [[ -n "$RELEASE_VERSION" ]]; then
  plutil -replace CFBundleShortVersionString -string "$RELEASE_VERSION" "$PLIST"
  plutil -replace CFBundleVersion -string "$RELEASE_BUILD" "$PLIST"
fi

LOGO="$ROOT_DIR/assets/bobo-logo.png"
ICONSET="$WORK_DIR/BoBoRemote.iconset"
mkdir "$ICONSET"
for size in 16 32 128 256 512; do
  retina_size=$((size * 2))
  sips -z "$size" "$size" "$LOGO" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z "$retina_size" "$retina_size" "$LOGO" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns -o "$STAGED_APP/Contents/Resources/BoBoRemote.icns" "$ICONSET"
plutil -insert CFBundleIconFile -string 'BoBoRemote.icns' "$PLIST"
plutil -lint "$PLIST"

codesign --force --sign - "$STAGED_APP"
codesign --verify --strict "$STAGED_APP"

echo '=== Creating BoBoRemote-macOS.dmg ==='
ln -s /Applications "$STAGE_DIR/Applications"
STAGED_DMG="$WORK_DIR/BoBoRemote-macOS.dmg"
hdiutil create -volname 'BoBoRemote' -srcfolder "$STAGE_DIR" -format UDZO "$STAGED_DMG"
rm -rf "$APP_BUNDLE"
mv "$STAGED_APP" "$APP_BUNDLE"
mv -f "$STAGED_DMG" "$DMG_PATH"
ls -lh "$DMG_PATH"
