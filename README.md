# MapCue

MapCue (nazwa robocza, TOOL-009 „MobileGraph Explorer”) to planowane narzędzie CLI w .NET 10. Ma deterministycznie eksplorować aplikacje mobilne (Android/iOS) przez [TapCue](https://github.com/rtnk/TapCue). Buduje trwały graf ekranów, stanów i wariantów profili, kontrakty ekranów z macierzą dostępności, a zweryfikowane ścieżki eksportuje jako flow YAML TapCue.

**Stan:** STEP-08 (kontrakty ekranów i accessibility). Jest szkielet solucji .NET 10, porty, walidacja konfiguracji, redakcja logów, adapter `IDeviceAutomation` przez publiczne MCP/CLI TapCue 0.1.0 i `mapcue doctor`. Od STEP-03 jest model grafu ekranów (normalizacja snapshotu, fingerprint, resolver tożsamości), SQLite z migracjami, content-addressed artefakty z redakcją i narzędzie testowe `mapcue graph import`. Od STEP-04 jest kontrakt z agentem (schematy w `schemas/`), walidator odpowiedzi (schemat → stale → semantyka → ryzyko → typowana akcja), regułowy klasyfikator ryzyka, `PolicyGate`, budżety i reasonery `DeterministicFakeReasoner`, `RuleBasedReasoner` (bez LLM) oraz `CliAgentReasoner` (`claude -p`, domyślnie w kontenerze). Od STEP-05 działa eksploracja: `mapcue explore` (preflight → pętla `AnalyzeState`/`ExploreAction` → zakończenie, frontier w SQLite, artefakty prób) i `mapcue status`. Od STEP-06 frontier ma lease i claim zadań, dzierżawę urządzenia, dziennik faz próby, rekoncyliację po przerwanym runie, kolejność według lokalności, ryzyka, nowości i głębokości, budżety per run i `representedBy` dla wierszy list. Od STEP-07 dochodzą profile i warianty:
- profile w YAML mają `scenario`, `locale` i `switchElements`;
- run domyślnie odmawia zmiany profilu;
- rozpoznawane są warianty profilu na wspólnych ekranach;
- ocena kandydatów i recipes są per kontekst;
- porównanie profili (macierz pokrycia, klasy równoważności, hipotezy guardów) pokazuje `mapcue status --profiles`.

Od STEP-08 są kontrakty ekranów (`mapcue.screen-contract/1`) z macierzą accessibility TalkBack/VoiceOver/Klawiatura i poziomami dowodu, `mapcue contract build` i `mapcue a11y record`.

Pozostałe komendy (`blocked`, `resume`, `report`, `export`, `diff`) to zaślepki. Od STEP-02B jest aplikacja kontrolna MAUI `samples/MapCue.DemoApp` (cel eksploracji, poza `MapCue.sln`).

## Szybki start (bez urządzenia, TapCue i LLM)

Wymagany .NET SDK 10 (wersja w `global.json`).

```bash
dotnet build MapCue.sln
dotnet test MapCue.sln
dotnet format MapCue.sln --verify-no-changes
dotnet run --project src/MapCue.Cli -- --help
dotnet run --project src/MapCue.Cli -- status --workspace /tmp/mapcue-empty                  # „no runs”, niczego nie tworzy
```

Kody wyjścia `mapcue`: `0` sukces, `1` błędna linia poleceń, `2` błędna konfiguracja, `3` komenda jeszcze niezaimplementowana, `4` `doctor` znalazł niespełniony warunek, `5` błędny plik wejściowy (np. plan `graph import`, nieznany `--run`), `6` workspace zajęty przez inny proces MapCue albo schemat bazy nowszy lub zmieniony, `7` preflight `explore` nieudany (urządzenie, setup flow, aplikacja na pierwszym planie, sekret), `8` run `explore` zakończony błędem albo przerwany. Przykład konfiguracji: [`docs/examples/mapcue.example.yaml`](docs/examples/mapcue.example.yaml). Logi to JSON lines na stderr, zawsze po redakcji; poziom ustawia `MAPCUE_LOG_LEVEL` (domyślnie `Warning`).

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

## Eksploracja (STEP-05): `mapcue explore` i `mapcue status`

```bash
PATH=<katalog bin TapCue>:$PATH dotnet run --project src/MapCue.Cli -- explore --config mapcue.yaml [--profile basic] [--device emulator-5554] [--seed demo] [--app-build 1.2.3]
dotnet run --project src/MapCue.Cli -- status --config mapcue.yaml      # albo --workspace <dir> [--run <runId>]
```

- **Preflight:** miejsce na dysku, urządzenie (połączone, z allowlisty, platforma profilu albo `--device`), plik `profiles[].setupFlow`, start aplikacji (`setupFlow` przez `tapcue test`, inaczej `launchApp`) i aplikacja na pierwszym planie.
- **Pętla:** `AnalyzeState` (reasoner na zapisanym snapshocie, bez urządzenia; domyślnie `agent.reasoner: rule-based`) planuje zaakceptowane akcje jako zadania `ExploreAction`. Zadanie: dojście do stanu źródłowego (znane krawędzie, inaczej reset), świeży pre-snapshot i `PreExecutionGuard`, jedna akcja, stabilizacja UI (2× ten sam odcisk bez `loading` albo `uiSettleTimeout`), commit próby w jednej transakcji.
- **Zakończenie:** `Exhausted`, `BudgetExhausted`, `AllRemainingBlocked`, `PreflightFailed`, `Failed`, `StoppedByOperator`.
- **Workspace:** `mapcue.db`, `blobs/`, `runs/<runId>/run.json`, `runs/<runId>/tasks/<taskId>/attempts/<n>/attempt.json`.
- `back` na ekranie głównym wychodzi z aplikacji: krawędź `executed` bez stanu docelowego, ekran launchera jako dowód próby, reset.
- `--seed` ustala identyfikatory runu i zdarzeń: dwa runy z tym samym ziarnem na tej samej aplikacji dają ten sam graf.
- **Wznowienie (STEP-06):** kolejny `mapcue explore` w tym samym workspace kontynuuje frontier. Run przerwany (zabity proces, budżet) zostawia zadania, które nowy run rekoncyliuje: przed `ActionDispatched` wracają do kolejki, po nim dostają `OutcomeUnknown` i nie są ponawiane. Run nie zaczyna zadania, na które nie starczy `maxActions`; `maxStates`, `maxScreens` i budżet reasonera liczą się per run.

## Profile (STEP-07)

Jeden run eksploruje jeden profil, czyli jeden kontekst. Profile porównuje się w jednym workspace kolejnymi runami:

```bash
dotnet run --project src/MapCue.Cli -- explore --config mapcue.yaml --profile basic --seed demo-basic
dotnet run --project src/MapCue.Cli -- explore --config mapcue.yaml --profile investor --seed demo-investor
dotnet run --project src/MapCue.Cli -- status --config mapcue.yaml --profiles   # context / coverage / equivalent / guard
```

- **Profil** (`profiles[]`): `environment: test`, `setupFlow`, `inputs` (tylko `secret://env/NAME` albo `generated:<kind>`), `capabilities`, `scenario`, `locale`, `switchElements` (wzorce `resource-id` elementów, które wybierają ten profil).
- **Zmiana profilu w runie** jest domyślnie odmówiona (`policy.profileSwitch: deny`): `switchElements` innych profili to `accountChange`, więc ich akcje nie są wykonywane. `allow` wyłącza tę ochronę.
- **Wariant profilu:** ekran z kilkoma elementami więcej lub mniej w innym profilu (np. zakładka Investments) to wariant wspólnego stanu (`identity.profileVariantMaxDelta`, domyślnie 2), a nie nowy stan. Krawędzie, zadania, dowody, oceny kandydatów i recipes są zawsze per kontekst.
- **Guard:** element nazwany jak capability (`tab.investments` ↔ `investments`) jest `inferred` po jednym profilu, a `observed` dopiero wtedy, gdy ten sam ekran widziano bez niego w profilu bez tej capability.

Demo na emulatorze: [`samples/MapCue.DemoApp/mapcue.demo.yaml`](samples/MapCue.DemoApp/mapcue.demo.yaml), `run-explore.sh` (profile `basic` i `investor`) i porównanie każdego kontekstu z wyrocznią `compare-expected.py` (job `android-explore` w `.github/workflows/demo-app.yml`).

## Kontrakty ekranów i accessibility (STEP-08)

```bash
dotnet run --project src/MapCue.Cli -- explore --config mapcue.yaml --profile basic    # z sekcją contracts: przechwytuje każdy stan
dotnet run --project src/MapCue.Cli -- contract build --config mapcue.yaml             # screens/<screenKey>/<grp_…>/screen-contract.v<k>.json
dotnet run --project src/MapCue.Cli -- a11y record --config mapcue.yaml --screen <screenKey> --profile basic \
  --technology talkback --result pass --evidence transkrypt.txt --operator qa-1 --device "Pixel 8, Android 15, TalkBack 15.0"
```

- **Przechwytywanie** (`contracts.analyzeScreens`, `contracts.keyboardFocus`, `contracts.maxTabPresses`; domyślnie wyłączone): po eksploracji kontekstu każdy stan dostaje pełną hierarchię (`AnalyzeScreen`, CLI `hierarchy`), a na Androidzie próbę fokusu klawiatury (`AuditAccessibility`: `pressKey Tab` i flaga `focused`, PoC). Każde naciśnięcie to akcja w `budgets.maxActions`. Eksplorowany graf się od tego nie zmienia.
- **Kontrakt** (schemat [`schemas/screen-contract.v1.schema.json`](schemas/screen-contract.v1.schema.json)):
  - jeden kontrakt na ekran i grupę (app, platforma, build, locale);
  - stany, konteksty, elementy z obecnością per kontekst i `visibleWhen` (hipoteza guardu), warianty z `identityTokens`;
  - macierz a11y, znaleziska, rekordy operatora, kolejność fokusu przewidywana (`predicted`/`unverified`) i obserwowana (`automated`).
- **Wersjonowanie:** nowa wersja tylko przy zmianie `contentHash`.
- **Poziomy dowodu** (architektura §14):
  - z drzewa nigdy nie ma PASS (`inspected`: `present`/`issues`);
  - TalkBack/VoiceOver `automated` = `unsupported` (G-05);
  - klawiatura na iOS = `unsupported` (G-04);
  - `assistiveVerified` tylko z protokołu manualnego: [`docs/a11y-manual-protocol.md`](docs/a11y-manual-protocol.md).

## TapCue i `mapcue doctor`

MapCue używa TapCue tylko jako procesu (`tapcue mcp` i komendy CLI), z czystym środowiskiem (bez `MAESTRO_*`). Wymagane: TapCue 0.1.0 (archiwum Release), Java 17+, dla Androida `adb` i uruchomiony emulator. Ścieżka do `tapcue` pochodzi z `tapcue.executable` w konfiguracji albo z `PATH`.

```bash
tests/MapCue.Adapters.TapCue.Smoke/fetch-tapcue.sh /tmp/tapcue          # pobiera i weryfikuje SHA-256, wypisuje ścieżkę bin/tapcue
PATH=/tmp/tapcue/unpacked/tapcue-0.1.0-macos-arm64/bin:$PATH dotnet run --project src/MapCue.Cli -- doctor [--config mapcue.yaml] [--device emulator-5554]
```

`doctor` sprawdza plik wykonywalny, wersję (`tapcue.supportedVersions`), środowisko, handshake MCP z wymaganymi narzędziami, podłączone urządzenia z allowlisty i opcjonalnie odczyt ekranu (bez żadnej akcji). Smoke na urządzeniu: `.github/workflows/tapcue-smoke.yml` i `tests/MapCue.Adapters.TapCue.Smoke/run-device-smoke.sh`.

## Aplikacja demo (STEP-02B)

[`samples/MapCue.DemoApp`](samples/MapCue.DemoApp) to aplikacja .NET MAUI (`net10.0-android`, `net10.0-ios`) bez backendu i bez prawdziwych danych: wybór profilu testowego (`basic`/`investor`, scenariusz pustych rachunków), ładowanie, Home z zakładkami, rachunki, szczegóły, Investments (tylko `investor`), Ustawienia, modal, akcje ryzykowne i celowy nieznany popup. Nie jest w `MapCue.sln`, więc `dotnet build MapCue.sln` nie wymaga workloadów MAUI. Oczekiwana mapa: [`expected-graph.md`](samples/MapCue.DemoApp/expected-graph.md). Flow TapCue: `samples/MapCue.DemoApp/.tapcue/`.

```bash
python3 -I samples/MapCue.DemoApp/check-consistency.py                     # AutomationId ↔ flow ↔ expected-graph.md
dotnet workload install maui-android
dotnet publish samples/MapCue.DemoApp/MapCue.DemoApp.csproj -f net10.0-android -c Release -o out   # out/com.mapcue.demoapp-Signed.apk
samples/MapCue.DemoApp/run-flows.sh <tapcue> out/com.mapcue.demoapp-Signed.apk reports/demo [emulator-5554]
```

CI: `.github/workflows/demo-app.yml` (APK, flow na emulatorze; build iOS ręcznie przez `workflow_dispatch` z `ios=true`).

## Dokumentacja

- Plan i statusy etapów: [`docs/ROADMAP.md`](docs/ROADMAP.md)
- Architektura: [`docs/architecture.md`](docs/architecture.md)
- Interfejs TapCue i luki: [`docs/tapcue-compatibility.md`](docs/tapcue-compatibility.md)
- Decyzje: [`docs/adr/`](docs/adr/)
- Handoffy etapów: [`docs/handovers/`](docs/handovers/)
- Zasady pracy agentów: [`AGENTS.md`](AGENTS.md)
