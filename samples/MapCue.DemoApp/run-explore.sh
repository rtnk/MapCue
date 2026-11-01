#!/usr/bin/env bash
# Installs the demo APK on a booted emulator, runs `mapcue explore` with mapcue.demo.yaml for the profiles basic and investor (one run
# per profile, one workspace; rule-based reasoner, setup-<profile>.yaml) and compares the discovered graph of each context with
# expected-graph.md (STEP-05 demo, STEP-07 profiles).
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
explore=0
summary=""
for profile in basic investor; do
  start=$(date +%s)
  "$MAPCUE" explore --config samples/MapCue.DemoApp/mapcue.demo.yaml --profile "$profile" --device "$DEVICE" --seed "demo-$profile" --app-build "apk-$APK_SHA" \
    > "$OUT/explore-$profile.txt" 2> "$OUT/explore-$profile.log.jsonl"
  code=$?
  seconds=$(( $(date +%s) - start ))
  cat "$OUT/explore-$profile.txt"
  echo "explore $profile exit code: $code (${seconds} s)"
  summary="$summary- explore $profile: exit code $code, ${seconds} s"$'\n'
  [ "$code" -eq 0 ] || explore=$code
done
"$MAPCUE" status --config samples/MapCue.DemoApp/mapcue.demo.yaml --profiles | tee "$OUT/status.txt"

python3 -I "$HERE/compare-expected.py" .mapcue-demo "$OUT/compare.md"
compare=$?
cat "$OUT/compare.md"
{ printf '%s\n' "$summary"; cat "$OUT/compare.md"; echo; echo '## mapcue status --profiles'; echo; echo '```'; cat "$OUT/status.txt"; echo '```'; } > "$OUT/report.md"
cp -r .mapcue-demo "$OUT/workspace"
tail -n 30 "$OUT/explore-basic.log.jsonl" "$OUT/explore-investor.log.jsonl"
if [ "$explore" -ne 0 ]; then
  # The setup flows run through `tapcue test`; the end of the newest TapCue logs goes to the job log (artifacts may be unreachable).
  find .mapcue-demo/tmp/tapcue -name maestro.log -printf '%T@ %p\n' 2>/dev/null | sort -n | tail -n 2 | cut -d' ' -f2- |
    while read -r log; do echo "--- $log"; tail -n 60 "$log"; done
fi
[ "$explore" -eq 0 ] && [ "$compare" -eq 0 ]
