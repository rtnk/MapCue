#!/usr/bin/env bash
# STEP-00 spike: reproducible TapCue 0.1.0 smoke test. Not production code.
#
#   docs/spikes/tapcue-smoke/run-smoke.sh <work-dir>                       # no device: version, list-devices, check-syntax, MCP handshake
#   DEVICE_ID=emulator-5554 APP_ID=com.android.settings docs/spikes/tapcue-smoke/run-smoke.sh <work-dir>
#   DEVICE_ID=<sim-udid> APP_ID=com.apple.Preferences docs/spikes/tapcue-smoke/run-smoke.sh <work-dir>   # macOS + Xcode
#
# Needs: curl, unzip, sha256sum or shasum, Java 17+, python3. With DEVICE_ID also adb (Android) or Xcode (iOS).
# HOME is redirected to <work-dir>/home so ~/.tapcue of the operator is not touched.
set -euo pipefail

VERSION="${TAPCUE_VERSION:-0.1.0}"
ARCHIVE="tapcue-${VERSION}-macos-arm64.zip"
BASE="https://github.com/rtnk/TapCue/releases/download/v${VERSION}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${1:?usage: run-smoke.sh <work-dir>}"

mkdir -p "$WORK/home" "$WORK/out"
# macOS has no `timeout` by default (coreutils provides `gtimeout`).
to() { local s="$1"; shift; if command -v timeout >/dev/null; then timeout "$s" "$@"; elif command -v gtimeout >/dev/null; then gtimeout "$s" "$@"; else "$@"; fi; }
cd "$WORK"
[ -f "$ARCHIVE" ] || curl -fsSL -o "$ARCHIVE" "$BASE/$ARCHIVE"
curl -fsSL -o SHA256SUMS "$BASE/SHA256SUMS"
if command -v sha256sum >/dev/null; then sha256sum -c SHA256SUMS; else shasum -a 256 -c SHA256SUMS; fi
rm -rf unpacked && unzip -q "$ARCHIVE" -d unpacked
TAPCUE="$WORK/unpacked/tapcue-${VERSION}-macos-arm64/bin/tapcue"
export HOME="$WORK/home"

echo "== tapcue --version";        "$TAPCUE" --version
echo "== tapcue list-devices";     to 120 "$TAPCUE" list-devices || echo "exit=$?"
echo "== check-syntax (generated single-action flow)"
"$TAPCUE" check-syntax "$HERE/single-action.yaml"
echo "== check-syntax (invalid flow must fail)"
printf 'appId: x\n---\n- tapOnn: "x"\n' > out/bad.yaml
if "$TAPCUE" check-syntax out/bad.yaml; then echo "UNEXPECTED: invalid flow accepted"; exit 1; else echo "rejected as expected"; fi

echo "== MCP over STDIO"
python3 -I "$HERE/mcp_smoke.py" --tapcue "$TAPCUE" ${DEVICE_ID:+--device "$DEVICE_ID"} ${APP_ID:+--app-id "$APP_ID"} --out "$WORK/out"

if [ -n "${DEVICE_ID:-}" ]; then
  echo "== CLI full-fidelity hierarchy (TreeNode JSON), timed"
  time to 180 "$TAPCUE" --device "$DEVICE_ID" hierarchy --no-reinstall-driver > out/hierarchy.json
  echo "== CLI test with artifacts (manifest.json, commands.json)"
  to 300 "$TAPCUE" --device "$DEVICE_ID" test --no-reinstall-driver --format junit --output out/report.xml \
    --test-output-dir out/test-output -e APP_ID="${APP_ID:-com.android.settings}" "$HERE/settings-replay.yaml" || echo "exit=$?"
  ls -R out/test-output | head -50
fi
