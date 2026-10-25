#!/usr/bin/env bash
# Downloads the pinned TapCue release archive, verifies it against the SHA-256 pinned here (not only against the
# release's own SHA256SUMS) and unpacks it. Prints the path of bin/tapcue on stdout.
#   tests/MapCue.Adapters.TapCue.Smoke/fetch-tapcue.sh <dir>
set -euo pipefail
VERSION="0.1.0"
ARCHIVE="tapcue-${VERSION}-macos-arm64.zip"
SHA256="e8dd01b061f5fe9d381cf510c65161c7181108833e0c05c757796750ddab6ec1"
DIR="${1:?usage: fetch-tapcue.sh <dir>}"
mkdir -p "$DIR"
cd "$DIR"
[ -f "$ARCHIVE" ] || curl -fsSL --retry 3 -o "$ARCHIVE" "https://github.com/rtnk/TapCue/releases/download/v${VERSION}/${ARCHIVE}"
if command -v sha256sum >/dev/null; then actual="$(sha256sum "$ARCHIVE" | cut -d' ' -f1)"; else actual="$(shasum -a 256 "$ARCHIVE" | cut -d' ' -f1)"; fi
if [ "$actual" != "$SHA256" ]; then
  echo "SHA-256 mismatch for $ARCHIVE: $actual" >&2
  exit 1
fi
rm -rf unpacked && unzip -q "$ARCHIVE" -d unpacked
echo "$PWD/unpacked/tapcue-${VERSION}-macos-arm64/bin/tapcue"
