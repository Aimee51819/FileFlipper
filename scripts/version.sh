#!/usr/bin/env bash
# One place to read, check and change FileFlipper's version.
#
#   ./scripts/version.sh show                 # print the Mac version and build number
#   ./scripts/version.sh check [vX.Y.Z]       # fail if the files disagree (or don't match the tag)
#   ./scripts/version.sh bump patch|minor|major|X.Y.Z
#                                             # set the version everywhere and raise the build number by 1
#   ./scripts/version.sh sync                 # copy project.yml's version into the other files (no build bump)
#
# The version lives in project.yml (source of truth), the generated Xcode project, the Windows
# .csproj, the Inno Setup script and the no-Xcode fallback in build-app.sh. The App Store needs a
# higher build number (CURRENT_PROJECT_VERSION) for every upload, so `bump` always raises it.
set -euo pipefail
cd "$(dirname "$0")/.."

PBXPROJ=FileFlipper.xcodeproj/project.pbxproj
CSPROJ=windows/FileFlipper/FileFlipper.csproj
ISS=windows/installer/FileFlipper.iss
BUILD_SCRIPT=scripts/build-app.sh

yml_value() { sed -n "s/^ *$1: *\"\{0,1\}\([^\"]*\)\"\{0,1\} *$/\1/p" project.yml | head -1; }
pbx_values() { sed -n "s/^.*$1 = \([^;]*\);.*$/\1/p" "$PBXPROJ" | sort -u; }

version=$(yml_value MARKETING_VERSION)
build=$(yml_value CURRENT_PROJECT_VERSION)

check() {
  local tag="${1:-}" problems=0
  report() { echo "::error::$1"; problems=$((problems + 1)); }

  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || report "project.yml MARKETING_VERSION '$version' is not X.Y.Z"
  [[ "$build" =~ ^[0-9]+$ ]] || report "project.yml CURRENT_PROJECT_VERSION '$build' is not a number"
  [ "$(pbx_values MARKETING_VERSION)" = "$version" ] ||
    report "$PBXPROJ MARKETING_VERSION is '$(pbx_values MARKETING_VERSION | tr '\n' ' ')', project.yml says $version"
  [ "$(pbx_values CURRENT_PROJECT_VERSION)" = "$build" ] ||
    report "$PBXPROJ CURRENT_PROJECT_VERSION is '$(pbx_values CURRENT_PROJECT_VERSION | tr '\n' ' ')', project.yml says $build"
  local cs; cs=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ")
  [ "$cs" = "$version" ] || report "$CSPROJ Version is '$cs', project.yml says $version"
  local iss; iss=$(sed -n 's/^ *#define AppVersion "\(.*\)"/\1/p' "$ISS")
  [ "$iss" = "$version" ] || report "$ISS AppVersion is '$iss', project.yml says $version"
  local fb; fb=$(sed -n 's:.*s/$(MARKETING_VERSION)/\([^/]*\)/.*:\1:p' "$BUILD_SCRIPT")
  [ "$fb" = "$version" ] || report "$BUILD_SCRIPT fallback version is '$fb', project.yml says $version"
  if [ -n "$tag" ] && [ "${tag#v}" != "$version" ]; then
    report "Tag $tag does not match project.yml version $version. Run ./scripts/version.sh bump ${tag#v} first."
  fi

  if [ "$problems" -gt 0 ]; then
    echo "Version check failed ($problems problem(s)). Fix with: ./scripts/version.sh sync" >&2
    exit 1
  fi
  echo "Version $version (build $build) is consistent${tag:+ and matches $tag}."
}

bump() {
  local next="${1:?usage: version.sh bump patch|minor|major|X.Y.Z}" major minor patch
  IFS=. read -r major minor patch <<<"$version"
  case "$next" in
    patch) next="$major.$minor.$((patch + 1))" ;;
    minor) next="$major.$((minor + 1)).0" ;;
    major) next="$((major + 1)).0.0" ;;
    *) [[ "$next" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Not a version: $next" >&2; exit 1; } ;;
  esac
  write "$next" $((build + 1))
}

write() {
  local next="$1" next_build="$2"
  # Edit in place with portable sed (works on macOS and Linux runners). The Xcode project is edited
  # directly rather than regenerated, so local signing settings in it are left alone.
  sedi() { local file="$1"; shift; sed "$@" "$file" > "$file.tmp" && mv "$file.tmp" "$file"; }
  sedi project.yml -e "s/^\( *MARKETING_VERSION: *\).*/\1$next/" -e "s/^\( *CURRENT_PROJECT_VERSION: *\).*/\1$next_build/"
  sedi "$PBXPROJ" -e "s/MARKETING_VERSION = [^;]*;/MARKETING_VERSION = $next;/" \
                  -e "s/CURRENT_PROJECT_VERSION = [^;]*;/CURRENT_PROJECT_VERSION = $next_build;/"
  sedi "$CSPROJ" -e "s:<Version>.*</Version>:<Version>$next</Version>:"
  sedi "$ISS" -e "s/^\( *#define AppVersion \)\".*\"/\1\"$next\"/" -e "s|/DAppVersion=[0-9.]*|/DAppVersion=$next|"
  sedi "$BUILD_SCRIPT" -e "s:s/\$(MARKETING_VERSION)/[^/]*/:s/\$(MARKETING_VERSION)/$next/:" \
                       -e "s:s/\$(CURRENT_PROJECT_VERSION)/[^/]*/:s/\$(CURRENT_PROJECT_VERSION)/$next_build/:"
  chmod +x "$BUILD_SCRIPT"
  version=$next build=$next_build
  check
}

case "${1:-show}" in
  show) echo "$version (build $build)" ;;
  check) check "${2:-}" ;;
  bump) bump "${2:-}" ;;
  sync) write "$version" "$build" ;;
  *) echo "usage: $0 show | check [vX.Y.Z] | bump patch|minor|major|X.Y.Z | sync" >&2; exit 1 ;;
esac
