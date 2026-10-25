# Spike STEP-00: TapCue smoke test

To nie jest kod produkcyjny. Skrypty zapisują, co TapCue 0.1.0 naprawdę wystawia. STEP-02 ma na tej podstawie napisać testy kontraktowe adaptera.

| Plik | Rola |
|---|---|
| `run-smoke.sh` | Pobiera archiwum Release `v0.1.0`, sprawdza SHA-256 i uruchamia kontrole CLI oraz MCP. Z `DEVICE_ID` dochodzą kontrole na urządzeniu. |
| `mcp_smoke.py` | Klient MCP po STDIO (JSON-RPC, jedna wiadomość na linię): `initialize`, `tools/list` i wywołania narzędzi. |
| `single-action.yaml` | Wzór krótkiego flow z jedną akcją, takiego, jakie generuje adapter (sprawdzany przez `check-syntax`). |
| `settings-replay.yaml` | Próba replay przez `tapcue test --test-output-dir` (`manifest.json`, `commands.json`). |

## Uruchomienie

```sh
# bez urządzenia (Linux albo macOS, Java 17+)
docs/spikes/tapcue-smoke/run-smoke.sh /tmp/tapcue-smoke

# Android: uruchomiony emulator, adb na PATH
DEVICE_ID=emulator-5554 APP_ID=com.android.settings docs/spikes/tapcue-smoke/run-smoke.sh /tmp/tapcue-smoke

# iOS Simulator: macOS arm64, Xcode, uruchomiony symulator (pierwszy przebieg buduje driver XCTest przez kilka minut)
DEVICE_ID=<udid> APP_ID=com.apple.Preferences docs/spikes/tapcue-smoke/run-smoke.sh /tmp/tapcue-smoke
```

## Wyniki z 2026-10-25 (sesja STEP-00)

Środowisko: kontener Linux x86_64, Java (OpenJDK z obrazu), bez `adb`, bez KVM, bez Xcode. Archiwum: `tapcue-0.1.0-macos-arm64.zip`, SHA-256 `e8dd01b061f5fe9d381cf510c65161c7181108833e0c05c757796750ddab6ec1` (zgodne z `SHA256SUMS`).

| Kontrola | Wynik | Uwagi |
|---|---|---|
| `tapcue --version` | PASS | `tapcue 0.1.0`. Archiwum „macos-arm64” to dystrybucja JVM: CLI uruchamia się też na Linuksie. README TapCue nazywa Linuksa platformą nieprzetestowaną. |
| `tapcue list-devices` | PASS | `No devices found`, exit 0 |
| `check-syntax single-action.yaml` | PASS | `OK`, exit 0 |
| `check-syntax` błędnego flow | PASS | `Invalid Command: tapOnn …`, exit 1 |
| MCP `initialize` | PASS | `serverInfo={"name":"tapcue","version":"1.0.0"}` (wersja na sztywno, nie wersja CLI), protokół `2025-06-18`, `capabilities.tools.listChanged=true` |
| MCP `tools/list` | PASS | `list_devices`, `take_screenshot(device_id)`, `run(device_id, yaml\|files\|dir, include_tags, exclude_tags, env)`, `inspect_screen(device_id)`, `cheat_sheet` |
| MCP `list_devices` | PASS | `{"devices":[]}` |
| MCP `inspect_screen` bez urządzenia | PASS (błąd zgodny z oczekiwaniem) | `isError=true`, tekst `Device with id … is not connected`. Błąd jest wynikiem narzędzia (`isError`), a nie błędem JSON-RPC. |
| MCP `run` z `yaml` i `files` naraz | PASS (odrzucone) | `isError=true`: `` `yaml`, `files`, and `dir` are mutually exclusive `` |
| `tapcue hierarchy` bez urządzenia | FAIL (zawiesza się) | Brak wyniku po 60 s, przerwane przez `timeout` (exit 124). Adapter musi mieć własny twardy timeout i zabijać proces. |
| Build TapCue ze źródeł | BLOCKED | Polityka sieci tej sesji odrzuca `dl.google.com` (403), więc nie da się pobrać pluginu Android Gradle. Zamiast tego użyto archiwum Release. |
| Kontrole na urządzeniu (Android, iOS) | NOT RUN | W sesji nie ma emulatora, symulatora ani urządzenia (brak KVM, brak macOS). |
