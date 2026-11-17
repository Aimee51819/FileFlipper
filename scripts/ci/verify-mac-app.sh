#!/usr/bin/env bash
# Smoke-tests a built FileFlipper.app: version, bundle ID, signature, sandbox, architectures.
#   scripts/ci/verify-mac-app.sh build/FileFlipper.app [--developer-id]
set -euo pipefail
APP="${1:?usage: verify-mac-app.sh path/to/FileFlipper.app [--developer-id]}"
DEVELOPER_ID="${2:-}"
cd "$(dirname "$0")/../.."

fail() { echo "::error::$1"; exit 1; }
plist() { /usr/libexec/PlistBuddy -c "Print :$1" "$APP/Contents/Info.plist"; }

expected_version=$(./scripts/version.sh show | cut -d' ' -f1)
expected_build=$(./scripts/version.sh show | sed 's/.*build \([0-9]*\).*/\1/')

[ -x "$APP/Contents/MacOS/FileFlipper" ] || fail "Missing executable in $APP"
[ "$(plist CFBundleIdentifier)" = "com.aimeesun.fileflipper" ] || fail "Bundle ID is $(plist CFBundleIdentifier)"
[ "$(plist CFBundleShortVersionString)" = "$expected_version" ] ||
  fail "App version is $(plist CFBundleShortVersionString), project.yml says $expected_version"
[ "$(plist CFBundleVersion)" = "$expected_build" ] || fail "Build number is $(plist CFBundleVersion), project.yml says $expected_build"
[ "$(plist LSUIElement)" = "true" ] || fail "LSUIElement must be true (menu bar app)"
[ -f "$APP/Contents/Resources/zh-Hans.lproj/Localizable.strings" ] || fail "Chinese localization missing from the bundle"
[ -f "$APP/Contents/Resources/PrivacyInfo.xcprivacy" ] || fail "Privacy manifest missing from the bundle"

codesign --verify --deep --strict --verbose=2 "$APP" || fail "Code signature is invalid"
entitlements=$(codesign -d --entitlements :- "$APP" 2>/dev/null)
grep -q "com.apple.security.app-sandbox" <<<"$entitlements" || fail "The app is not signed with the sandbox entitlement"

archs=$(lipo -archs "$APP/Contents/MacOS/FileFlipper")
for arch in arm64 x86_64; do
  grep -qw "$arch" <<<"$archs" || fail "Executable is missing $arch (has: $archs)"
done

if [ "$DEVELOPER_ID" = "--developer-id" ]; then
  codesign -dv "$APP" 2>&1 | grep -q "Authority=Developer ID Application" || fail "Not signed with a Developer ID certificate"
  codesign -dv "$APP" 2>&1 | grep -q "runtime" || fail "Hardened runtime is off"
  spctl --assess --type execute --verbose=2 "$APP" || fail "Gatekeeper rejects the app"
  xcrun stapler validate "$APP" || fail "No notarization ticket stapled"
fi

echo "OK: $APP is FileFlipper $expected_version ($expected_build), $archs, sandboxed, signature valid."
