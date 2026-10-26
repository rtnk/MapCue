#!/usr/bin/env bash
# Installs the demo APK on a booted emulator, runs `mapcue explore` with mapcue.demo.yaml (rule-based reasoner, setup-basic.yaml)
# and compares the discovered graph with expected-graph.md (STEP-05 demo).
#   samples/MapCue.DemoApp/run-explore.sh <mapcue> <tapcue> <apk> <out-dir> [device-id]
# <mapcue> is the published CLI (dotnet publish src/MapCue.Cli); TapCue is found on PATH (its bin directory is prepended).
set -uo pipefail
MAPCUE="${1:?usage: run-explore.sh <mapcue> <tapcue> <apk> <out-dir> [device-id]}"; TAPCUE="${2:?tapcue}"; APK="${3:?apk}"; OUT="${4:?out-dir}"; DEVICE="${5:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
if [ -z "$DEVICE" ]; then DEVICE="$(adb devices | awk 'NR>1 && $2=="device" {print $1; exit}')"; fi
[ -n "$DEVICE" ] || { echo "no booted device" >&2; exit 2; }
echo "device: $DEVICE"
adb -s "$DEVICE" install -r "$APK" || { echo "adb install failed" >&2; exit 2; }
APK_SHA="$(sha256sum "$APK" | cut -c1-12)"

export PATH="$(dirname "$TAPCUE"):$PATH"
export MAPCUE_LOG_LEVEL=Information
cd "$REPO"
rm -rf .mapcue-demo
start=$(date +%s)
"$MAPCUE" explore --config samples/MapCue.DemoApp/mapcue.demo.yaml --device "$DEVICE" --seed demo --app-build "apk-$APK_SHA" \
  > "$OUT/explore.txt" 2> "$OUT/explore.log.jsonl"
explore=$?
seconds=$(( $(date +%s) - start ))
cat "$OUT/explore.txt"
echo "explore exit code: $explore (${seconds} s)"
"$MAPCUE" status --config samples/MapCue.DemoApp/mapcue.demo.yaml | tee "$OUT/status.txt"

python3 -I "$HERE/compare-expected.py" .mapcue-demo "$OUT/compare.md"
compare=$?
cat "$OUT/compare.md"
{ echo "explore exit code: $explore, ${seconds} s"; echo; cat "$OUT/compare.md"; } > "$OUT/report.md"
cp -r .mapcue-demo "$OUT/workspace"
tail -n 60 "$OUT/explore.log.jsonl"
[ "$explore" -eq 0 ] && [ "$compare" -eq 0 ]
