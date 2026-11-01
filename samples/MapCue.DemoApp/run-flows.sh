#!/usr/bin/env bash
# Installs the demo APK on a booted emulator and runs the TapCue flows from .tapcue/ (STEP-02B smoke).
#   samples/MapCue.DemoApp/run-flows.sh <tapcue> <apk> <out-dir> [device-id]
# Setup flows (setup-*.yaml) are the deliverable; tour flows (tour-*.yaml) check expected-graph.md against the app.
# After each profile setup the screen hierarchy is saved for fingerprint calibration in STEP-05.
set -uo pipefail
TAPCUE="${1:?usage: run-flows.sh <tapcue> <apk> <out-dir> [device-id]}"; APK="${2:?apk}"; OUT="${3:?out-dir}"; DEVICE="${4:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
if [ -z "$DEVICE" ]; then DEVICE="$(adb devices | awk 'NR>1 && $2=="device" {print $1; exit}')"; fi
[ -n "$DEVICE" ] || { echo "no booted device" >&2; exit 2; }
echo "device: $DEVICE"

adb -s "$DEVICE" install -r "$APK" || { echo "adb install failed" >&2; exit 2; }

report="$OUT/report.md"
{
  echo "| Flow | Result | Seconds |"
  echo "|---|---|---|"
} > "$report"
status=0
for name in setup-basic setup-investor setup-basic-empty tour-basic tour-investor; do
  flow="$HERE/.tapcue/$name.yaml"
  start=$(date +%s)
  if "$TAPCUE" --device "$DEVICE" test --format junit --output "$OUT/$name.xml" --test-output-dir "$OUT/$name" "$flow" > "$OUT/$name.log" 2>&1; then
    result=PASS
  else
    result=FAIL
    status=1
  fi
  echo "| \`$name\` | $result | $(( $(date +%s) - start )) |" >> "$report"
  echo "== $name: $result"
  tail -n 40 "$OUT/$name.log"
  if [ "$result" = FAIL ]; then
    # Artifact storage may be unreachable from the agent sandbox: the end of TapCue's own log goes to the job log too.
    find "$OUT/$name" -name maestro.log -print 2>/dev/null | sort | while read -r log; do echo "--- $log"; tail -n 60 "$log"; done
  fi
  case "$name" in
    setup-basic|setup-investor)
      "$TAPCUE" --device "$DEVICE" hierarchy --no-reinstall-driver > "$OUT/hierarchy-$name.txt" 2> "$OUT/hierarchy-$name.stderr" || true
      ;;
  esac
done

cat "$report"
# Artifact storage may be unreachable from the agent sandbox; the hierarchies are also printed between markers.
for name in setup-basic setup-investor; do
  echo "=== BEGIN hierarchy-$name.txt"; cat "$OUT/hierarchy-$name.txt" 2>/dev/null || true; echo "=== END hierarchy-$name.txt"
done
exit "$status"
