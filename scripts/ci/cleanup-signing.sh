#!/usr/bin/env bash
# CI only: removes the temporary keychain and key files created by sign-mac.sh / the App Store workflow.
set -uo pipefail
: "${RUNNER_TEMP:?run this in GitHub Actions}"
security delete-keychain "$RUNNER_TEMP/signing.keychain-db" 2>/dev/null || true
rm -f "$RUNNER_TEMP"/*.p12 "$RUNNER_TEMP"/*.p8 "$RUNNER_TEMP"/notarize.zip
rm -rf "$HOME/.appstoreconnect/private_keys"
echo "Signing material removed."
