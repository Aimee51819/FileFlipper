#!/usr/bin/env bash
# CI only: re-signs build/FileFlipper.app with a Developer ID certificate, notarizes it with Apple and
# staples the ticket, so people who download it from GitHub don't see the "unidentified developer" warning.
#
# Reads everything from environment variables (GitHub secrets); nothing is written outside $RUNNER_TEMP,
# and the temporary keychain is deleted by scripts/ci/cleanup-signing.sh.
#   MACOS_DEVELOPER_ID_P12           base64 of the exported "Developer ID Application" certificate + key (.p12)
#   MACOS_DEVELOPER_ID_P12_PASSWORD  its export password
#   APP_STORE_CONNECT_API_KEY_P8     base64 of the App Store Connect API key (.p8), used by notarytool
#   APP_STORE_CONNECT_KEY_ID, APP_STORE_CONNECT_ISSUER_ID
set -euo pipefail
cd "$(dirname "$0")/../.."
: "${RUNNER_TEMP:?run this in GitHub Actions}"
APP=build/FileFlipper.app
KEYCHAIN="$RUNNER_TEMP/signing.keychain-db"
KEYCHAIN_PASSWORD=$(uuidgen)

echo "::add-mask::$KEYCHAIN_PASSWORD"
printf '%s' "$MACOS_DEVELOPER_ID_P12" | base64 --decode > "$RUNNER_TEMP/cert.p12"
printf '%s' "$APP_STORE_CONNECT_API_KEY_P8" | base64 --decode > "$RUNNER_TEMP/AuthKey.p8"

security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 3600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$RUNNER_TEMP/cert.p12" -k "$KEYCHAIN" -P "$MACOS_DEVELOPER_ID_P12_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
existing=()
while IFS= read -r line; do existing+=("$(echo "$line" | tr -d '" ')"); done < <(security list-keychains -d user)
security list-keychains -d user -s "$KEYCHAIN" "${existing[@]}"
rm -f "$RUNNER_TEMP/cert.p12"

identity=$(security find-identity -v -p codesigning "$KEYCHAIN" | sed -n 's/.*"\(Developer ID Application: .*\)"/\1/p' | head -1)
[ -n "$identity" ] || { echo "::error::No 'Developer ID Application' identity in MACOS_DEVELOPER_ID_P12"; exit 1; }
echo "Signing with: $identity"

codesign --force --options runtime --timestamp --keychain "$KEYCHAIN" \
  --entitlements Resources/FileFlipper.entitlements --sign "$identity" "$APP"

ditto -c -k --keepParent "$APP" "$RUNNER_TEMP/notarize.zip"
xcrun notarytool submit "$RUNNER_TEMP/notarize.zip" --wait --timeout 30m \
  --key "$RUNNER_TEMP/AuthKey.p8" --key-id "$APP_STORE_CONNECT_KEY_ID" --issuer "$APP_STORE_CONNECT_ISSUER_ID"
xcrun stapler staple "$APP"
