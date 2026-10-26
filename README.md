# MapCue

MapCue (nazwa robocza, TOOL-009 „MobileGraph Explorer”) to planowane narzędzie CLI w .NET 10. Ma deterministycznie eksplorować aplikacje mobilne (Android/iOS) przez [TapCue](https://github.com/rtnk/TapCue). Buduje trwały graf ekranów, stanów i wariantów profili, kontrakty ekranów z macierzą dostępności, a zweryfikowane ścieżki eksportuje jako flow YAML TapCue.

**Stan:** STEP-04 (interpretacja przez agenta). Jest szkielet solucji .NET 10, porty, walidacja konfiguracji, redakcja logów, adapter `IDeviceAutomation` przez publiczne MCP/CLI TapCue 0.1.0, działające `mapcue doctor`, a od STEP-03 model grafu ekranów (normalizacja snapshotu, fingerprint, resolver tożsamości), SQLite z migracjami, content-addressed artefakty z redakcją i narzędzie testowe `mapcue graph import`. Od STEP-04 jest kontrakt z agentem (schematy w `schemas/`), walidator odpowiedzi (schemat → stale → semantyka → ryzyko → typowana akcja), regułowy klasyfikator ryzyka, `PolicyGate`, budżety i reasonery `DeterministicFakeReasoner`, `RuleBasedReasoner` (bez LLM) oraz `CliAgentReasoner` (`claude -p`, domyślnie w kontenerze). Pozostałe komendy to zaślepki. Eksploracja jeszcze nie działa (od STEP-05).

## Szybki start (bez urządzenia, TapCue i LLM)

Wymagany .NET SDK 10 (wersja w `global.json`).

```bash
dotnet build MapCue.sln
dotnet test MapCue.sln
dotnet format MapCue.sln --verify-no-changes
dotnet run --project src/MapCue.Cli -- --help
dotnet run --project src/MapCue.Cli -- explore --config docs/examples/mapcue.example.yaml   # waliduje konfigurację, potem „not implemented”
```

Kody wyjścia `mapcue`: `0` sukces, `1` błędna linia poleceń, `2` błędna konfiguracja, `3` komenda jeszcze niezaimplementowana, `4` `doctor` znalazł niespełniony warunek, `5` błędny plik wejściowy (np. plan `graph import`), `6` workspace zajęty przez inny proces MapCue albo schemat bazy nowszy lub zmieniony. Przykład konfiguracji: [`docs/examples/mapcue.example.yaml`](docs/examples/mapcue.example.yaml). Logi to JSON lines na stderr, zawsze po redakcji; poziom ustawia `MAPCUE_LOG_LEVEL` (domyślnie `Warning`).

## Graf bez urządzenia: `mapcue graph import`

Narzędzie testowe: importuje nagrane snapshoty i przejścia do workspace (`.mapcue/` albo `--workspace`) tak, jak zrobiłaby to próba eksploracji. Powtórny import tego samego planu niczego nie zmienia.

```bash
dotnet run --project src/MapCue.Cli -- graph import tests/Fixtures/graph-import/android-settings-cycle.json --workspace /tmp/mapcue-demo
```

Plan (`mapcue.graph-import/1`, JSON) opisuje kontekst (profil, platforma, aplikacja, build, locale, scenariusz), obserwacje (pliki `tapcue-inspect-screen`, `tapcue-hierarchy`, `tapcue-mcp-transcript` z `index`, `mapcue-elements`) i przejścia (`from`, `action`, `to`, `outcome`). Przykład na nagraniach z emulatora: [`tests/Fixtures/graph-import/android-settings-cycle.json`](tests/Fixtures/graph-import/android-settings-cycle.json). Workspace: `mapcue.db` (SQLite, WAL), `blobs/sha256/..` (zredagowane snapshoty), `lock`.

## Agent (STEP-04)

Reasoner tylko interpretuje zredagowany snapshot i proponuje akcje po `elementRef`; nie dostaje narzędzi TapCue (ADR-0004). Każda odpowiedź przechodzi walidację z [architektury §9.5](docs/architecture.md), a do wykonania może trafić tylko typowana akcja z allowlisty, z ryzykiem `max(agent, reguły)` dopuszczonym przez politykę profilu. Konfiguracja: sekcje `agent` i `policy.riskRules` w [przykładzie](docs/examples/mapcue.example.yaml). `CliAgentReasoner` działa domyślnie w kontenerze (`agent.containerImage` wybiera operator); `agent.runtime: local` wymaga `agent.allowLocalExecution: true` i nie ma fallbacku między trybami.

Smoke z prawdziwym CLI (pomijany bez zmiennej):

```bash
MAPCUE_AGENT_SMOKE=local dotnet test tests/MapCue.Adapters.Reasoning.Tests --filter CliAgentSmokeTests
MAPCUE_AGENT_SMOKE=container MAPCUE_AGENT_IMAGE=<obraz z claude> ANTHROPIC_API_KEY=… dotnet test tests/MapCue.Adapters.Reasoning.Tests --filter CliAgentSmokeTests
```

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
