# MapCue

MapCue (nazwa robocza, TOOL-009 „MobileGraph Explorer”) to planowane narzędzie CLI w .NET 10. Ma deterministycznie eksplorować aplikacje mobilne (Android/iOS) przez [TapCue](https://github.com/rtnk/TapCue). Buduje trwały graf ekranów, stanów i wariantów profili, kontrakty ekranów z macierzą dostępności, a zweryfikowane ścieżki eksportuje jako flow YAML TapCue.

**Stan:** STEP-02 (adapter TapCue). Jest szkielet solucji .NET 10, porty, walidacja konfiguracji, redakcja logów, adapter `IDeviceAutomation` przez publiczne MCP/CLI TapCue 0.1.0 i działające `mapcue doctor`. Pozostałe komendy to zaślepki. Eksploracja jeszcze nie działa (od STEP-05).

## Szybki start (bez urządzenia, TapCue i LLM)

Wymagany .NET SDK 10 (wersja w `global.json`).

```bash
dotnet build MapCue.sln
dotnet test MapCue.sln
dotnet format MapCue.sln --verify-no-changes
dotnet run --project src/MapCue.Cli -- --help
dotnet run --project src/MapCue.Cli -- explore --config docs/examples/mapcue.example.yaml   # waliduje konfigurację, potem „not implemented”
```

Kody wyjścia `mapcue`: `0` sukces, `1` błędna linia poleceń, `2` błędna konfiguracja, `3` komenda jeszcze niezaimplementowana, `4` `doctor` znalazł niespełniony warunek. Przykład konfiguracji: [`docs/examples/mapcue.example.yaml`](docs/examples/mapcue.example.yaml). Logi to JSON lines na stderr, zawsze po redakcji; poziom ustawia `MAPCUE_LOG_LEVEL` (domyślnie `Warning`).

## TapCue i `mapcue doctor`

MapCue używa TapCue tylko jako procesu (`tapcue mcp` i komendy CLI), z czystym środowiskiem (bez `MAESTRO_*`). Wymagane: TapCue 0.1.0 (archiwum Release), Java 17+, dla Androida `adb` i uruchomiony emulator. Ścieżka do `tapcue` pochodzi z `tapcue.executable` w konfiguracji albo z `PATH`.

```bash
tests/MapCue.Adapters.TapCue.Smoke/fetch-tapcue.sh /tmp/tapcue          # pobiera i weryfikuje SHA-256, wypisuje ścieżkę bin/tapcue
PATH=/tmp/tapcue/unpacked/tapcue-0.1.0-macos-arm64/bin:$PATH dotnet run --project src/MapCue.Cli -- doctor [--config mapcue.yaml] [--device emulator-5554]
```

`doctor` sprawdza plik wykonywalny, wersję (`tapcue.supportedVersions`), środowisko, handshake MCP z wymaganymi narzędziami, podłączone urządzenia z allowlisty i opcjonalnie odczyt ekranu (bez żadnej akcji). Smoke na urządzeniu: `.github/workflows/tapcue-smoke.yml` i `tests/MapCue.Adapters.TapCue.Smoke/run-device-smoke.sh`.

## Dokumentacja

- Plan i statusy etapów: [`docs/ROADMAP.md`](docs/ROADMAP.md)
- Architektura: [`docs/architecture.md`](docs/architecture.md)
- Interfejs TapCue i luki: [`docs/tapcue-compatibility.md`](docs/tapcue-compatibility.md)
- Decyzje: [`docs/adr/`](docs/adr/)
- Handoffy etapów: [`docs/handovers/`](docs/handovers/)
- Zasady pracy agentów: [`AGENTS.md`](AGENTS.md)
