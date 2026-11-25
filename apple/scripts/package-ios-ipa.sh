#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
IOS_DIR="$ROOT_DIR/apple/BoBoRemoteiOS"
OUT_DIR="$ROOT_DIR/dist-apple"
BUILD_DIR="$IOS_DIR/build"
IPA_PATH="$OUT_DIR/BoBoRemote-iOS-Unsigned.ipa"

mkdir -p "$OUT_DIR"
rm -rf "$BUILD_DIR" "$IPA_PATH"

cd "$IOS_DIR"
echo "=== Generating Xcode project via XcodeGen ==="
xcodegen generate

echo "=== Building BoBoRemoteiOS (Release, iphoneos, unsigned) ==="
xcodebuild \
  -project BoBoRemoteiOS.xcodeproj \
  -scheme BoBoRemoteiOS \
  -configuration Release \
  -sdk iphoneos \
  -derivedDataPath "$BUILD_DIR" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY="" \
  build

APP_PATH="$BUILD_DIR/Build/Products/Release-iphoneos/BoBoRemoteiOS.app"
if [[ ! -d "$APP_PATH" ]]; then
  echo "ERROR: built iOS app bundle not found at $APP_PATH"
  exit 1
fi

echo "=== Packaging BoBoRemote-iOS-Unsigned.ipa (AltStore / SideStore / Sideloadly / Xcode) ==="
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT
mkdir -p "$STAGE_DIR/Payload"
cp -R "$APP_PATH" "$STAGE_DIR/Payload/BoBoRemoteiOS.app"
(cd "$STAGE_DIR" && zip -qry "$IPA_PATH" Payload)

ls -lh "$IPA_PATH"
