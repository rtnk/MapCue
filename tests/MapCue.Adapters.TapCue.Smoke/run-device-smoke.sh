#!/usr/bin/env bash
# Device smoke of the TapCue adapter (STEP-02). Expects a booted emulator/simulator and a Release build.
#   tests/MapCue.Adapters.TapCue.Smoke/run-device-smoke.sh <tapcue> <out-dir> <app-id> <safe-action: back|scroll> [device-id]
set -uo pipefail
TAPCUE="$1"; OUT="$2"; APP_ID="$3"; SAFE="$4"; DEVICE="${5:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
mkdir -p "$OUT"
export PATH="$(dirname "$TAPCUE"):$PATH"
if [ -z "$DEVICE" ] && command -v adb >/dev/null; then DEVICE="$(adb devices | awk 'NR>1 && $2=="device" {print $1; exit}')"; fi
echo "device: ${DEVICE:-<none>}"
status=0

echo "== mapcue doctor"
dotnet "$ROOT/src/MapCue.Cli/bin/Release/net10.0/mapcue.dll" doctor ${DEVICE:+--device "$DEVICE"} | tee "$OUT/doctor.txt" || status=$?

echo "== adapter smoke"
dotnet "$ROOT/tests/MapCue.Adapters.TapCue.Smoke/bin/Release/net10.0/MapCue.Adapters.TapCue.Smoke.dll" \
  --tapcue "$TAPCUE" --out "$OUT/smoke" --app-id "$APP_ID" --safe-action "$SAFE" ${DEVICE:+--device "$DEVICE"} || status=$?

echo "== fixture recording (raw MCP, for contract tests)"
safe_cmd="back"; [ "$SAFE" = scroll ] && safe_cmd="scroll"
python3 -I "$ROOT/tests/MapCue.Adapters.TapCue.ContractTests/Fixtures/tapcue-0.1.0/record_mcp_fixture.py" \
  --tapcue "$TAPCUE" --device "$DEVICE" --app-id "$APP_ID" --safe-command "$safe_cmd" --out "$OUT/mcp-device-recording.jsonl" || true
"$TAPCUE" --device "$DEVICE" hierarchy --no-reinstall-driver > "$OUT/hierarchy-raw.txt" 2> "$OUT/hierarchy-raw.stderr" || true

cat "$OUT/smoke/report.md" 2>/dev/null || true
exit "$status"
