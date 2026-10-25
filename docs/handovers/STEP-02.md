# HANDOFF: STEP-02: TapCue adapter

## Status i identyfikatory

- Status: **DONE** dla Androida (realny odczyt ekranu i bezpieczna akcja na emulatorze: PASS). iOS: **NOT RUN** (job gotowy, przyczyna i kryterium odblokowania niżej). Merge wykonuje Robert.
- Branch: `feat/step-02-tapcue-adapter`
- PR URL i status: <https://github.com/rtnk/MapCue/pull/4> (open, czeka na review i merge)
- Commit SHA: kod adaptera `defec15` (ostatnia zmiana w `src/`); ten handoff, nagrane fixtures i ich testy są w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `5bcc809457d059bc267bb9c6a6a8bd6dadcc4e4c` (merge PR #3, CI na `main`: success, run 40235235383)
- Data i środowisko: 2026-10-25. Lokalnie: kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.113 (pakiet Ubuntu), Java 21 z obrazu, TapCue 0.1.0 z archiwum Release (SHA-256 zgodne), bez KVM, bez `adb`, bez macOS; `dl.google.com` i blob storage artefaktów GitHub zablokowane przez proxy (403). Urządzenie: emulator Androida API 34 w GitHub Actions (`ubuntu-24.04`, KVM).

## Zakres faktycznie wykonany

- **Adapter `TapCueDeviceAutomation : IDeviceAutomation, IAsyncDisposable`** (`src/MapCue.Adapters.TapCue`), wyłącznie przez procesy `tapcue` (bez klas TapCue, bez `maui-dev-mcp`):
  - **MCP STDIO** (`Mcp/`): `JsonRpcConnection` (JSON-RPC 2.0, jedna wiadomość na linię, limit 32 M znaków na linię, korelacja po `id`, odpowiedź na `ping`, `-32601` na inne żądania serwera, ignorowanie śmieci na stdout), `McpToolClient` (`initialize` z protokołem `2025-06-18`, `notifications/initialized`, `tools/list` z weryfikacją narzędzi `list_devices`, `inspect_screen(device_id)`, `run(device_id, yaml, env)`), `TapCueMcpSession` (proces `tapcue mcp`, handshake w `startupTimeout`, twardy timeout na wywołanie, zabicie drzewa procesów).
  - **CLI** (`Cli/TapCueCliRunner`): `--version`, `check-syntax`, `--device <id> hierarchy --no-reinstall-driver`, `--device <id> test --no-reinstall-driver --format junit --output … --test-output-dir …`. Argumenty jako wektor, bez powłoki.
  - **Procesy** (`Processes/`): `ProcessEnvironment` (środowisko budowane od zera z allowlisty `PATH, HOME, USER, LOGNAME, TMPDIR, LANG, LC_ALL, JAVA_HOME, ANDROID_HOME, ANDROID_SDK_ROOT, ANDROID_USER_HOME, ANDROID_ADB_SERVER_PORT, XDG_STATE_HOME, DEVELOPER_DIR` + `tapcue.passEnvironment`; `MAESTRO_*` nigdy, `JAVA_TOOL_OPTIONS` tylko jawnie), `ChildProcess` (kill całego drzewa, ograniczony ogon stderr, ponowienie `exec` przy ETXTBSY), `ProcessRunner` (twardy timeout, limit wyjścia), `ExecutableResolver` (ścieżka z konfiguracji albo `PATH`, bez ścieżek na sztywno).
  - **Generator YAML** (`Flows/`): `FlowYamlGenerator` z szablonów dla `Tap`, `LongPress`, `Back`, `HideKeyboard`, `LaunchApp`, `WaitForAnimationToEnd`, `Scroll` (w dół: `scroll`, inne kierunki: `swipe`), `Swipe`, `InputText` (wartość tylko przez `env` jako `${MAPCUE_IN_1}`, nigdy w pliku). `Point`, `OpenLink`, `SetPermissions` i akcje bez selektora są odrzucane. `MaestroText` escapuje regex (Maestro dopasowuje `text`/`id` jako regex; `${` staje się `\$\{`) i pisze skalary YAML w podwójnych cudzysłowach. `FlowGuard` odrzuca zakazane komendy z §2.3 (`runScript`, `evalScript`, `assertTrue`, `runFlow`, `openLink`, `setAirplaneMode`, `toggleAirplaneMode`, `clearKeychain`, `addMedia`, `travel`, `setLocation`) także w zagnieżdżonych blokach, hooki `onFlowStart`/`onFlowComplete`, `jsEngine`, `url`, nieznane klucze nagłówka i każde `${…}` poza `${NAZWA_ENV}`. Ten sam guard i allowlista `appId` obowiązują dla `RunFlowAsync`.
  - **Wyniki**: `OutcomeUnknown` (z `Dispatched = true`), gdy akcja mogła dotrzeć do TapCue i straciliśmy wynik (timeout, zerwane STDIO, anulowanie). Akcje **nigdy** nie są ponawiane. Nierozpoznany błąd po wysłaniu daje `OutcomeUnknown` dla ryzyka ≥ `StateChanging` i `Failed` dla `Safe`/`Navigational`. Odczyty (`list_devices`, `inspect_screen`) są ponawiane po restarcie sesji najwyżej `budgets.maxRetries` razy. Timeouty: `actionTimeout`, `observeTimeout`; pierwsze wywołanie na urządzeniu w nowej sesji dostaje dodatkowo `startupTimeout` (instalacja drivera); `hierarchy` dostaje `max(observeTimeout, startupTimeout)`.
  - **Klasyfikacja błędów** (`Parsing/TapCueErrorClassifier`): `DeviceNotConnected`, `DeviceNotSupported`, `InvalidRequest`, `ToolNotFound`, `FlowSyntax`, `ElementNotFound`, `AssertionFailed`, `AppLaunchFailed`, `AppCrashed`, `DriverTimeout`, `DriverLost`, inaczej `Unknown`; razem z informacją, czy błąd dowodzi, że nic nie dotarło do urządzenia.
  - **Parsery** (`Parsing/TapCueParsers`): `--version`, `list_devices`, kompaktowy `inspect_screen` (elementy w pre-order z głębokością), pełny `TreeNode` z `hierarchy`, wynik `run` z usuwaniem wartości `env_vars` (G-06).
  - **Redakcja**: wartości wejść są rejestrowane w `IRedactor` przed wysłaniem; `Detail`, `RawPayload`, błędy CLI i ogon stderr przechodzą przez redaktor. `RawObservation.Elements` zostają nieredagowane (selektory muszą pasować do ekranu); trwały zapis należy do store'ów STEP-03.
  - **Preflight wersji**: `TapCueVersionPolicy` (`tapcue.supportedVersions`, `allowUntestedVersion` → status `Untested`). Sesja MCP nigdy nie startuje z nieobsługiwaną wersją. Wersja tylko z `tapcue --version` (G-07).
  - **R-03**: `hierarchy` działa obok sesji MCP (`tapcue.fullObservation: shared`, domyślnie; `exclusive` zamyka sesję na czas CLI). `RunFlowAsync` **zawsze** zamyka sesję MCP przed `tapcue test`, a `Device server died` (`DriverLost`) restartuje sesję.
  - Serializacja wszystkich wywołań urządzenia w jednym adapterze (G-09 w obrębie procesu; dzierżawa w SQLite to STEP-06).
- **`mapcue doctor`** (prawdziwa komenda zamiast zaślepki): `[--config|-c <plik>] [--device <id>]`. Sprawdza config (albo domyślne ustawienia, gdy brak `mapcue.yaml`), plik wykonywalny, wersję, środowisko (ostrzeżenie o `MAESTRO_*` w powłoce), handshake MCP i narzędzia, podłączone urządzenia z allowlisty, opcjonalnie odczyt ekranu (bez akcji). Wyjście: `PASS|WARN|FAIL|SKIP <nazwa> <szczegóły>` (zredagowane). Kod `4` (`PrerequisitesFailed`), gdy coś ma FAIL.
- **Konfiguracja**: nowa opcjonalna sekcja `tapcue:` (`executable`, `supportedVersions`, `allowUntestedVersion`, `passEnvironment`, `startupTimeout` 5 s–15 min, `fullObservation`, `checkGeneratedSyntax`), kody `CFG060` (wartość) i `CFG061` (`MAESTRO_*` w `passEnvironment`). Przykład w `docs/examples/mapcue.example.yaml`.
- **Smoke**: `tests/MapCue.Adapters.TapCue.Smoke` (runner konsolowy, raport `report.json`/`report.md`, zredagowany transkrypt MCP), `fetch-tapcue.sh` (archiwum z SHA-256 przypiętym w repo), `run-device-smoke.sh`, workflow `.github/workflows/tapcue-smoke.yml`: `tapcue-cli` (bez urządzenia, PR), `android` (emulator API 34 z KVM, PR), `ios` (`macos-26`, tylko `workflow_dispatch` z `ios: true`). Akcje przypięte do SHA, `permissions: contents: read`.
- **Odstępstwa i rozszerzenia API** (świadome, potrzebne do zakresu): patrz „Pliki / API” niżej. `take_screenshot` nie jest używany (screenshoty to P1, §10.3). `ForegroundAppId` zostaje `null`, bo TapCue 0.1.0 go nie raportuje.

## Pliki / API / migracje

- Nowe w `src/MapCue.Adapters.TapCue/`: `TapCueDeviceAutomation.cs`, `TapCueAdapterOptions.cs`, `TapCueDoctor.cs`, `TapCueException.cs`, `TapCueVersionPolicy.cs`, `Mcp/{JsonRpcConnection,McpToolClient,TapCueMcpSession}.cs`, `Cli/TapCueCliRunner.cs`, `Flows/{FlowYamlGenerator,FlowGuard,MaestroText}.cs`, `Parsing/{TapCueParsers,TapCueErrorClassifier}.cs`, `Processes/{ChildProcess,ProcessRunner,ProcessEnvironment,ExecutableResolver}.cs`. Pakiety: `Microsoft.Extensions.Logging.Abstractions`, `YamlDotNet` (już w CPM).
- Nowe w `src/MapCue.Cli/`: `DoctorCommand.cs`. Zmienione: `MapCueCli.cs`, `CliHost.cs` (wstrzykiwane środowisko), `ExitCodes.cs` (`4`).
- Zmienione w `src/MapCue.Application/`: `Ports/IDeviceAutomation.cs`, `Configuration/{MapCueSettings,MapCueConfigDocument,MapCueConfigValidator,ConfigError}.cs`. W `src/MapCue.Domain/ActionType.cs`: `ActionType.LaunchApp`, `ScrollDirection`.
- Testy: `tests/MapCue.Adapters.TapCue.ContractTests` (fixtures + 96 testów), nowe `tests/MapCue.Adapters.TapCue.IntegrationTests` (35), `tests/MapCue.Adapters.TapCue.FakeServer` (fałszywy `tapcue`, proces), `tests/MapCue.Adapters.TapCue.Smoke`, `tests/MapCue.Testing/FakeTapCue.cs`, `tests/MapCue.Cli.Tests/DoctorCommandTests.cs`, `tests/MapCue.Application.Tests/TapCueConfigTests.cs`. Usunięte: `SkeletonTests.cs`.
- Fixtures (`tests/MapCue.Adapters.TapCue.ContractTests/Fixtures/`, pochodzenie w `README.md`): `mcp-no-device.jsonl` (nagrany lokalnie z prawdziwego TapCue, CI sprawdza dryf), `mcp-android-settings.jsonl` i `hierarchy-android-launcher.txt` (nagrane na emulatorze w CI), dwa pliki `*.synthetic.*` (oznaczone, do testów punktowych), `record_mcp_fixture.py`.
- Dokumenty: `README.md`, `docs/ROADMAP.md` (statusy, R-03, macierz platform), `docs/tapcue-compatibility.md` (§2.2, §7, nowy §9), `docs/adr/ADR-0002-tapcue-integration.md` (status i wynik PoC), `docs/examples/mapcue.example.yaml`.
- **Zmiany łamiące publiczne API (brak konsumentów poza testami):**
  - `ValidatedDeviceAction` ma nowy, wymagany drugi parametr `AppId` i opcjonalny `Direction`;
  - `DeviceInfo.IsConnected`, nowe pola `RawElement` (bounds, hint, value, flagi, `Depth`) i `RawObservation` (`Fidelity`, `RawPayload`);
  - `ActionExecution` dostaje `ErrorKind` i `Dispatched`, `FlowRunResult` dostaje `ErrorKind` i `TestOutputDirectory`;
  - nowe `DeviceErrorKind`, `ActionType.LaunchApp`, `ScrollDirection`, `MapCueSettings.TapCue`;
  - kod wyjścia `4`; `doctor` przestaje zwracać `3`.
- Migracje: brak. Snapshot `--help`: bez zmian (opis `doctor` ten sam).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #3 zmergowany, `main` aktualny, CI zielone | `git fetch origin main` (`5bcc809`), GitHub API PR #3 `merged: true`, run 40235235383 `success` | PASS | — |
| Build (Debug, Release) | `dotnet build MapCue.sln [-c Release]` / lokalnie SDK 10.0.113 | PASS (0 ostrzeżeń) | — |
| Testy (11 projektów, 292 testy) | `dotnet test MapCue.sln` / lokalnie | PASS | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` | PASS | — |
| Test architektury | `MapCue.Architecture.Tests` (17) | PASS | — |
| Kontraktowe na fixtures (bez urządzenia) | `dotnet test tests/MapCue.Adapters.TapCue.ContractTests` | PASS (96) | `Fixtures/tapcue-0.1.0/` |
| Integracyjne z fałszywym serwerem (proces): timeout akcji → `OutcomeUnknown` bez retry, kill drzewa procesów (wnuk), zerwane STDIO, zawieszony handshake, zawieszone CLI `hierarchy`, anulowanie, `DriverLost`, czyste env, redakcja `env_vars`, allowlisty, wersja, brak narzędzia | `dotnet test tests/MapCue.Adapters.TapCue.IntegrationTests` | PASS (35) | — |
| G-06/R-13: brak `MAESTRO_*`/sekretów w env procesu i w wynikach | `EnvironmentAndRedactionTests`, `ProcessEnvironmentTests`, `DoctorCommandTests` | PASS | — |
| CI GitHub Actions `CI` (build/test/format/CLI smoke) | `.github/workflows/ci.yml`, ubuntu-24.04, commit `defec15` | PASS | run 40239216319 |
| TapCue bez urządzenia w CI: szablony przez prawdziwy `check-syntax`, `mapcue doctor` (exit 4, MCP PASS), dryf fixture | `tapcue-smoke.yml` job `tapcue-cli`, commit `defec15` | PASS | run 40239216271 |
| Szablony przez prawdziwy `tapcue check-syntax` lokalnie | `dotnet tests/…Smoke.dll --tapcue <bin/tapcue> --out <dir> --templates-only` | PASS (12/12) | — |
| `mapcue doctor` z prawdziwym TapCue bez urządzenia | `PATH=<bin>:$PATH dotnet run --project src/MapCue.Cli -- doctor` / lokalnie | PASS (oczekiwane: exit 4, `FAIL devices`) | — |
| **Smoke Android**: `list_devices` → `launchApp` Settings → `inspect_screen` → `back` → `inspect_screen` → `hierarchy` → `test --test-output-dir`, plus `mapcue doctor --device` | `tapcue-smoke.yml` job `android`, emulator API 34 `google_apis` x86_64, ubuntu-24.04 + KVM, `run-device-smoke.sh` | **PASS** (wszystkie kroki; odczyt ekranu 73 elementy Settings, `back` → launcher 58 elementów) | runy 40238138686 (`a571e15`) i 40239216271 (`defec15`), artefakt `android-smoke-reports` |
| Pomiar czasów (PoC ADR-0002) | jw. | PASS | `docs/tapcue-compatibility.md` §9.1 |
| R-03 (MCP vs CLI na jednym urządzeniu) | jw. | `hierarchy`: brak kolizji; `test`: kolizja wykryta (run 40238138686), po poprawce sesja jest restartowana i odczyt przechodzi (run 40239216271) | §9.2 |
| **Smoke iOS** | `tapcue-smoke.yml` job `ios` (`workflow_dispatch`, `ios: true`, `macos-26`) | **NOT RUN** (przyczyna niżej) | — |
| `take_screenshot` | — | NOT RUN (adapter go nie używa) | — |

## Ograniczenia, ryzyka, otwarte pytania

- **iOS (R-01): NOT RUN.** Dwie przyczyny:
  1. Job `workflow_dispatch` da się uruchomić dopiero, gdy workflow jest na domyślnym branchu, czyli po merge.
  2. Minuty `macos-26` są płatne, a decyzja 3 z ROADMAP (dostęp do runnera macOS) jest otwarta.

  **Kryterium odblokowania:** po merge Robert (albo agent za jego zgodą) uruchamia „TapCue smoke” z `ios: true`. PASS oznacza w raporcie `inspect_screen` i akcję `scroll` na `com.apple.Preferences`.

  **Ryzyko:** czyste środowisko nie przepuszcza `MAESTRO_DRIVER_STARTUP_TIMEOUT`, którego CI TapCue używa na iOS (240 s). Jeśli driver XCTest nie zdąży wystartować, potrzebna jest decyzja o wąskim wyjątku od reguły „bez `MAESTRO_*`”. Ta zmienna nie jest sekretem, ale TapCue odsyła ją w `env_vars`.
- **Kolizja `test` z MCP (R-03)** jest obsłużona zamknięciem sesji przed replayem. Kosztuje to restart sesji z reinstalacją drivera (~4–7 s) po każdym `RunFlowAsync`. STEP-05/06 powinny grupować replaye.
- `hierarchy` przy otwartej sesji działała w obu runach. To dwa przebiegi, nie dowód. Tryb `exclusive` jest gotowy na wypadek zmiany.
- Klasyfikacja błędów opiera się na tekście (G-08). Nowa wersja TapCue wymaga nowych fixtures (job `tapcue-cli` wykrywa dryf nagrania bez urządzenia).
- `inspect_screen` zawiera elementy System UI (pasek stanu) i nie raportuje aplikacji na pierwszym planie. Filtrowanie i weryfikacja aplikacji to STEP-03/05.
- `check-syntax` kosztuje 1,2–2,3 s (JVM), więc wynik jest cache'owany po kształcie szablonu na instancję adaptera.
- Smoke Android uruchamia się na każdym pushu do PR, bo filtr `paths` w `pull_request` patrzy na cały diff PR. Push w trakcie anuluje poprzedni run (`concurrency`).
- Artefaktów CI nie da się pobrać z sesji agenta (blob storage: 403), dlatego `run-device-smoke.sh` drukuje nagrania do logu między znacznikami `=== BEGIN/END`.
- Akcje v4 (`checkout`, `setup-dotnet`) wciąż deklarują Node 20 (ostrzeżenie runnera, bez wpływu). Nowe akcje: `setup-java` v6.1.0 `b843ebf…`, `android-emulator-runner` v2.37.0 `e89f39f…` (ta sama co w CI TapCue), `upload-artifact` v7.0.2 `cf430e0…`.
- Pytania do Roberta:
  - zgoda na uruchomienie smoke iOS po merge (koszt macOS);
  - czy dopuścić wyjątek dla `MAESTRO_DRIVER_STARTUP_TIMEOUT`, jeśli iOS go wymaga.

## Stan roadmapy

- STEP-00, STEP-01: zmergowane. STEP-02: PR #4 otwarty. Tabela statusów, rejestr ryzyk (R-03) i macierz platform w `docs/ROADMAP.md` są zaktualizowane.
- Macierz platform: Android emulator (host Linux) ma PASS dla snapshotu, akcji i replay (bez eksportu). iOS Simulator: NOT RUN.
- Następne: STEP-03 (zależy od 01) i STEP-02B (decyzja 1, domyślnie tak) mogą ruszyć po merge tego PR. STEP-04 zależy od 03.

## Warunki startu STEP-03

- PR #4 (`feat/step-02-tapcue-adapter`) **zmergowany** do `main`, CI zielone na `main`, lokalny `main` zaktualizowany.
- STEP-03 nie potrzebuje urządzenia ani TapCue. Do kalibracji fingerprintu (R-04) może użyć nagranych snapshotów z `tests/MapCue.Adapters.TapCue.ContractTests/Fixtures/tapcue-0.1.0/` (`inspect_screen` Settings i launchera, pełny `hierarchy`).
- Kontrakty z tego etapu, na których STEP-03 się opiera: `RawObservation` (`Elements` w pre-order z `Depth`, `RawPayload` zredagowany, `SnapshotHash` = hash payloadu, nie tożsamość ekranu), `ValidatedDeviceAction` z `AppId`.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-03: Graph & persistence.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-03, rejestr ryzyk R-04/R-11 i wspólne DONE), docs/architecture.md (§6, §7, §12), ADR-0003, ADR-0006, docs/handovers/STEP-02.md oraz kontrakt `RawObservation`/`RawElement` w `src/MapCue.Application/Ports/IDeviceAutomation.cs` i nagrane snapshoty w `tests/MapCue.Adapters.TapCue.ContractTests/Fixtures/tapcue-0.1.0/` (opis w `README.md` tego katalogu).
> Zweryfikuj, że PR-02 (<https://github.com/rtnk/MapCue/pull/4>) jest zmergowany, a `main` aktualny i z zielonym CI. Jeśli nie jest, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-03-graph-persistence` i `docs/handovers/STEP-03.md` w tym samym PR. Nie rozpoczynaj STEP-04/02B i nie merguj PR.
> Zakres (ROADMAP STEP-03): typy domenowe (`LogicalScreen`, `ScreenState`, `ScreenVariant`, `ScreenEdge`, `UiSnapshot`, `UiElement`, `ActionCandidate`, `ActionObservation`, `Evidence`, `FrontierTask`, `TaskAttempt`, `Checkpoint`, `ReachabilityRecipe`, `BlockedReason`); normalizacja snapshotu z `RawObservation` (w tym odfiltrowanie System UI jako jawna, testowana reguła) i `identityHash`/`layoutHash` z `fingerprintVersion`; resolver tożsamości; SQLite + migracje; `IStateStore` z transakcją commitu próby; content-addressed `IArtifactStore` z redakcją przed zapisem; outbox `events`; import snapshotów z plików (`mapcue graph import`, narzędzie testowe; nowa komenda wymaga aktualizacji snapshotu help).
> Testy: property-based stabilności fingerprintu (permutacja, cyfry, długość listy) także na nagranych snapshotach Androida; cykl A→B→A; dwa profile bez mieszania wariantów i dowodów; idempotentny import; migracje od zera i przyrostowo; atomowość (wyjątek w środku transakcji nie zostawia częściowego stanu).
> DoD: powtarzany import stanów daje stabilny graf; cykle i profile testowane bez urządzenia; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury nadal przechodzi; handoff STEP-03 zapisany. Wyniki raportuj jako PASS, FAIL albo NOT RUN, z komendami.
