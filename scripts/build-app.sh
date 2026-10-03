#!/usr/bin/env bash
# Builds FileFlipper.app into ./build for running on this Mac (ad-hoc signed, sandboxed like the App Store build).
#
#   ./scripts/build-app.sh             # build
#   ./scripts/build-app.sh --install   # also copy to /Applications
#
# Needs Xcode. Without Xcode it falls back to the Swift command-line tools
# (that build is not sandboxed, which is fine for personal use).
set -euo pipefail
cd "$(dirname "$0")/.."

INSTALL=0
for arg in "$@"; do
  case "$arg" in
    --install) INSTALL=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 1 ;;
  esac
done

APP="build/FileFlipper.app"
rm -rf "$APP"
mkdir -p build

if xcodebuild -version >/dev/null 2>&1; then
  xcodebuild -project FileFlipper.xcodeproj -scheme FileFlipper -configuration Release \
    -derivedDataPath build/DerivedData \
    CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM= \
    build > build/xcodebuild.log 2>&1 || {
      grep -E "error:" build/xcodebuild.log || tail -20 build/xcodebuild.log
      echo "Build failed. Full log: build/xcodebuild.log"
      exit 1
    }
  BUILT="build/DerivedData/Build/Products/Release/FileFlipper.app"
  cp -R "$BUILT" "$APP"
else
  echo "Xcode not found - building with the Swift command-line tools instead."
  swift build -c release
  BIN_DIR="$(swift build -c release --show-bin-path)"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp "$BIN_DIR/FileFlipper" "$APP/Contents/MacOS/FileFlipper"
  sed -e 's/$(EXECUTABLE_NAME)/FileFlipper/; s/$(PRODUCT_NAME)/FileFlipper/' \
      -e 's/$(PRODUCT_BUNDLE_IDENTIFIER)/com.aimeesun.fileflipper/' \
      -e 's/$(MARKETING_VERSION)/1.5.0/; s/$(CURRENT_PROJECT_VERSION)/8/' \
      -e 's/$(MACOSX_DEPLOYMENT_TARGET)/14.0/' \
      Resources/Info.plist > "$APP/Contents/Info.plist"
  # Icon for the command-line build
  ICONSET="build/AppIcon.iconset"
  rm -rf "$ICONSET" && mkdir -p "$ICONSET"
  cp Resources/Assets.xcassets/AppIcon.appiconset/*.png "$ICONSET/"
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
  /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$APP/Contents/Info.plist"
  codesign --force --sign - "$APP"
fi

echo "Built $APP"

if [ "$INSTALL" = 1 ]; then
  rm -rf "/Applications/FileFlipper.app"
  cp -R "$APP" /Applications/
  echo "Installed to /Applications/FileFlipper.app"
fi
