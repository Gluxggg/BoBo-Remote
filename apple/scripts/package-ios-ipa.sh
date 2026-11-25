#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
IOS_DIR="$ROOT_DIR/apple/BoBoRemoteiOS"
OUT_DIR="$ROOT_DIR/dist-apple"
BUILD_DIR="$IOS_DIR/build"
IPA_PATH="$OUT_DIR/BoBoRemote-iOS-Unsigned.ipa"
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
if ! command -v xcodegen >/dev/null 2>&1; then
  echo 'XcodeGen is required. Install it with brew install xcodegen.' >&2
  exit 1
fi

mkdir -p "$OUT_DIR"
rm -rf "$BUILD_DIR"

cd "$IOS_DIR"
echo '=== Generating Xcode project via XcodeGen ==='
xcodegen generate

echo '=== Building BoBoRemoteiOS (Release, iphoneos, unsigned) ==='
BUILD_ARGS=(
  -project BoBoRemoteiOS.xcodeproj
  -scheme BoBoRemoteiOS
  -configuration Release
  -sdk iphoneos
  -derivedDataPath "$BUILD_DIR"
  CODE_SIGNING_ALLOWED=NO
  CODE_SIGNING_REQUIRED=NO
  CODE_SIGN_IDENTITY=
)
if [[ -n "$RELEASE_VERSION" ]]; then
  BUILD_ARGS+=("MARKETING_VERSION=$RELEASE_VERSION" "CURRENT_PROJECT_VERSION=$RELEASE_BUILD")
fi
xcodebuild "${BUILD_ARGS[@]}" build

APP_PATH="$BUILD_DIR/Build/Products/Release-iphoneos/BoBoRemoteiOS.app"
EXTENSION_PATH='PlugIns/BoBoBroadcastExtension.appex'
if [[ ! -d "$APP_PATH/$EXTENSION_PATH" ]]; then
  echo "Built iOS app or ReplayKit extension not found at $APP_PATH" >&2
  exit 1
fi

echo '=== Packaging BoBoRemote-iOS-Unsigned.ipa ==='
STAGE_DIR="$(mktemp -d "$OUT_DIR/.ios-package.XXXXXX")"
trap 'rm -rf "$STAGE_DIR"' EXIT
mkdir "$STAGE_DIR/Payload"
STAGED_APP="$STAGE_DIR/Payload/BoBoRemoteiOS.app"
cp -R "$APP_PATH" "$STAGED_APP"
for bundle in "$STAGED_APP" "$STAGED_APP/$EXTENSION_PATH"; do
  plist="$bundle/Info.plist"
  if [[ -n "$RELEASE_VERSION" ]]; then
    plutil -replace CFBundleShortVersionString -string "$RELEASE_VERSION" "$plist"
    plutil -replace CFBundleVersion -string "$RELEASE_BUILD" "$plist"
  fi
  plutil -lint "$plist"
done
STAGED_IPA="$STAGE_DIR/BoBoRemote-iOS-Unsigned.ipa"
(cd "$STAGE_DIR" && zip -qry "$STAGED_IPA" Payload)
mv -f "$STAGED_IPA" "$IPA_PATH"
ls -lh "$IPA_PATH"
