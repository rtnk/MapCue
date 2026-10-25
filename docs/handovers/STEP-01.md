# HANDOFF: STEP-01: Solution foundation

## Status i identyfikatory

- Status: **DONE** (lokalnie i w CI: PASS).
- Branch: `feat/step-01-foundation`
- PR URL i status: <https://github.com/rtnk/MapCue/pull/3> (open, czeka na review i merge przez Roberta)
- Commit SHA: `805fdf6855dfcae4ac1310f66c9becd07cef4e15` (kod). Ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `473b8bb14e91f73a2d19f03597107194bbee4002` (merge PR #2)
- Data i środowisko: 2026-10-25. Kontener Linux x86_64 (Ubuntu 24.04, sesja Claude Code w chmurze). .NET SDK **10.0.113** z pakietu Ubuntu `dotnet-sdk-10.0` (source-built), bo `dot.net` i `builds.dotnet.microsoft.com` są zablokowane przez proxy (403). NuGet (`api.nuget.org`) dostępny. Brak urządzeń, TapCue i LLM (niepotrzebne w tym etapie).

## Zakres faktycznie wykonany

- **Solucja:** `MapCue.sln`, `global.json` (SDK `10.0.100`, `rollForward: latestPatch`, bez prerelease), `Directory.Build.props` (`net10.0`, nullable, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, deterministyczny build, `InvariantGlobalization`, wersja `0.1.0-alpha.1`), `Directory.Packages.props` (CPM + transitive pinning), `tests/Directory.Build.props` (xUnit dla projektów `*Tests`), `.editorconfig`, `.gitignore`.
- **Projekty `src/` (§3.1):** `Domain`, `Contracts`, `Application`, `Infrastructure.Sqlite`, `Infrastructure.Artifacts`, `Adapters.TapCue`, `Adapters.Reasoning`, `Reporting`, `Cli`. Szkielety bez kodu mają `AssemblyMetadata("MapCue.ImplementedIn", "STEP-NN")`. **Odstępstwo:** brak `MapCue.Adapters.DevFlow`. To opcjonalny STEP-11, a pusty projekt nic nie wnosi.
- **Porty (§4)** w `MapCue.Application.Ports`: `IDeviceAutomation` (+ DTO: `ToolVersion`, `DeviceInfo`, `ObservationRequest/Fidelity`, `RawObservation`, `RawElement`, `ValidatedDeviceAction`, `ActionExecution`, `FlowFile`, `FlowRunOptions/Result`, `SyntaxCheckResult`), `IExplorationReasoner` (+ `ReasonerResult/Status/Usage`), `IStateStore`/`IUnitOfWork` (puste repozytoria, STEP-03), `IArtifactStore` (+ `ArtifactRef/Kind`, `RedactionInfo`), `IRedactor` (+ `RedactionContext`, `RedactedContent`, `RegisterSensitiveValue`), `ISecretResolver`, `IClock`, `IIdGenerator`, `IHttpCaptureSource` (pusty, STEP-11).
- **Domain:** `Platform`, `DeviceId`, `ActionOutcome` (z `OutcomeUnknown`), `ActionType` + `ActionTypes.DefaultAllowed`, `RiskClass` + `RiskClasses.DefaultAllowed` (`safe`, `navigational`), `DataIsolation`, `SecretRef` (tylko `secret://env/NAME`, `NAME` = `[A-Z_][A-Z0-9_]*`), `SecretValue` (`ToString()` zwraca `***`).
- **Contracts:** `SchemaVersions` (`mapcue.config/1`, `mapcue.agent-exploration-request/1`, `…-response/1`), szkielety `AgentExplorationRequest/Response`. Mają tylko pola korelacji (pełny kontrakt i JSON Schema dochodzą w STEP-04).
- **Konfiguracja:**
  - `MapCueConfigDocument` (surowe DTO) → `MapCueConfigValidator` → niemutowalne `MapCueSettings` (Application). `MapCueConfigLoader` (Cli, YamlDotNet) ładuje ściśle: nieznane klucze, zduplikowane klucze i plik > 1 MB są błędem. Komunikaty parsera są ograniczone do lokalizacji i nazwy klucza.
  - Walidacja odrzuca brakującą albo podejrzaną konfigurację:
    - wymagane niepuste `allowlist.appIds` i `allowlist.environments`; dozwolone środowisko to tylko `test`;
    - profil musi mieć `appId` z allowlisty i `environment: test`;
    - `agent.runtime: local` wymaga `allowLocalExecution: true`;
    - wejścia profili tylko jako `secret://env/NAME` albo `generated:<kind>`. Literał daje `CFG050` i komunikat bez wartości.
  - Domyślne budżety są dokładnie z §10.2, z twardymi zakresami (np. `maxDepth` 1–64, `actionTimeout` 1 s–10 min). Kody błędów `CFG001…CFG051`. Przykład: `docs/examples/mapcue.example.yaml`.
- **Redakcja i logi:**
  - `PatternRedactor` (Infrastructure.Artifacts) redaguje najpierw zarejestrowane wartości (od najdłuższej), potem wzorce: Bearer, JWT, `password=`/`apiKey:`/…, e-mail, IBAN, PAN (Luhn), telefon z `+`.
  - `EnvironmentSecretResolver` rejestruje wartość w redaktorze przed zwróceniem. Brak zmiennej daje wyjątek z samą referencją.
  - `JsonLinesLoggerProvider` (Microsoft.Extensions.Logging) pisze jedną linię JSON na wpis. Każdy string (message, template, propsy, wyjątek) przechodzi przez redaktor, a znacznik czasu pochodzi z `IClock`.
- **CLI:** `mapcue` (assembly `mapcue`, System.CommandLine 2.0.13):
  - jawny root `mapcue`, help o stałej szerokości 120, `--version` z wersją MapCue (a nie hosta);
  - komendy `explore [--config|-c]`, `status`, `blocked`, `resume <taskId> [--approve]`, `report`, `export`, `diff --from --to`, `doctor`;
  - kody wyjścia: `0` OK, `1` błąd linii poleceń, `2` konfiguracja, `3` not implemented. `explore` najpierw waliduje konfigurację (domyślnie `mapcue.yaml`);
  - logi idą na stderr, poziom ustawia `MAPCUE_LOG_LEVEL` (domyślnie `Warning`). Composition root: `CliHost` (bez kontenera DI, wystarczy na tym etapie).
- **Testing:** `ManualClock`, `SequentialIdGenerator`, `FakeDevice` (ekrany, tap→przejście, back, odrzucenie innych akcji, deterministyczny `snapshotHash`), `DeterministicFakeReasoner` (odpowiedzi po `snapshotHash`; nieznany snapshot daje `Unavailable`).
- **CI:** `.github/workflows/ci.yml` (ubuntu-24.04):
  - kroki: checkout bez zapisanych credentiali, setup-dotnet z `global.json`, restore, build Release, test (trx), `dotnet format --verify-no-changes`, smoke CLI (`--help`, `--version`, `status` musi zwrócić 3);
  - akcje przypięte do SHA: `actions/checkout` v4.2.2 `11bd719…`, `actions/setup-dotnet` v4.3.1 `67a3573…`;
  - `permissions: contents: read`, `timeout-minutes: 20`, concurrency.

## Pliki / API / migracje

- Nowe: `MapCue.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitignore`, `.github/workflows/ci.yml`, `src/**` (9 projektów), `tests/**` (8 projektów testów + `MapCue.Testing`), `tests/Directory.Build.props`, `tests/MapCue.Cli.Tests/Snapshots/help.txt`, `docs/examples/mapcue.example.yaml`, `docs/handovers/STEP-01.md`.
- Zmienione: `README.md` (stan i szybki start), `docs/ROADMAP.md` (statusy 00 i 01).
- Publiczne API: porty i typy wymienione wyżej. Kontrakt konfiguracji `mapcue.config/1` jest nowy. Kody wyjścia CLI są nowym kontraktem. Migracje: brak.
- Pakiety: System.CommandLine 2.0.13, YamlDotNet 16.3.0, Microsoft.Extensions.Logging(.Abstractions) 10.0.13, xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.10.1.

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #2 zmergowany | `git fetch origin main`; `origin/main` = `473b8bb` (merge PR #2) | PASS | — |
| Build Debug | `dotnet build MapCue.sln` / Linux, SDK 10.0.113 | PASS (0 ostrzeżeń, TWAE) | — |
| Build Release | `dotnet build MapCue.sln -c Release` | PASS | — |
| Testy (8 projektów, 144 testy) | `dotnet test MapCue.sln` i `-c Release --no-build` | PASS (144/144) | — |
| Format | `dotnet format --verify-no-changes` | PASS (exit 0) | — |
| `--help` bez urządzenia, TapCue i LLM | `dotnet run --project src/MapCue.Cli -- --help` | PASS (exit 0) | `tests/MapCue.Cli.Tests/Snapshots/help.txt` |
| `--version` | `dotnet run --project src/MapCue.Cli -- --version` | PASS (`0.1.0-alpha.1+<sha>`, exit 0) | — |
| Komendy-zaślepki | `dotnet run --project src/MapCue.Cli -- status` | PASS (exit 3) | `CommandTests` |
| `explore` z poprawną konfiguracją | `… -- explore --config docs/examples/mapcue.example.yaml` | PASS (exit 3, not implemented) | — |
| `explore` z `environments: [prod]` | `… -- explore -c bad.yaml` | PASS (exit 2, `CFG012`) | — |
| Walidacja konfiguracji (brak `appIds`/`environments`, nie-`test`, budżety, literały, `secret://`) | `ConfigValidatorTests`, `ConfigLoaderTests`, `CommandTests` | PASS | — |
| Redakcja: sekret nie trafia do logu (template, interpolacja, wyjątek, wrapper) | `RedactingLoggerTests.Resolved_secret_never_reaches_the_log` | PASS | — |
| Reguły zależności projektów (csproj + skompilowane assembly, brak TapCue/maestro/maui-dev-mcp, brak nadpisań centralnych ustawień) | `MapCue.Architecture.Tests` | PASS | — |
| CI GitHub Actions | `.github/workflows/ci.yml` na PR #3, ubuntu-24.04, SDK 10.0.112 | PASS | <https://github.com/rtnk/MapCue/pull/3/checks> |
| Kontrole na urządzeniu | — | NOT RUN (nie dotyczy STEP-01) | — |

### Wynik CI

- Run: <https://github.com/rtnk/MapCue/actions/runs/40234302058> (job `build / test / format (ubuntu)`, commit `805fdf6`) — **PASS**: restore, build Release (0 ostrzeżeń, 0 błędów), test (144/144), format (`Formatted 0 of 116 files`), smoke CLI (`--help` exit 0, `--version` → `0.1.0-alpha.1+<sha>`, `status` exit 3).
- Efektywne SDK w CI: **10.0.112**. `setup-dotnet` zainstalował 10.0.100, ale `rollForward: latestPatch` wybrał najnowszą łatkę z pasma 10.0.1xx preinstalowaną na runnerze. Przypięte są więc major i feature band (10.0.1xx), a nie dokładna łatka. Dokładne przypięcie (`rollForward: disable`) wymagałoby tej samej łatki lokalnie i w CI. Decyzja zostaje dla Roberta (pytanie otwarte niżej).
- Ostrzeżenie runnera: akcje v4 deklarują Node 20 i są wymuszane na Node 24. Działa, ale przy kolejnej zmianie CI warto podbić je do v5 z nowymi SHA.

## Ograniczenia, ryzyka, otwarte pytania

- **SDK lokalny ≠ SDK w CI:** lokalnie 10.0.113 (Ubuntu source-built), w CI efektywnie 10.0.112 (`latestPatch`). Wynik obu jest identyczny (0 ostrzeżeń). **Pytanie do Roberta:** czy wymagane jest dokładne przypięcie łatki (`rollForward: disable` + konkretna wersja), czy wystarcza pasmo 10.0.1xx?
- SHA akcji pochodzą z mojej wiedzy o tagach (`actions/checkout` v4.2.2, `actions/setup-dotnet` v4.3.1). CI potwierdziło, że SHA istnieją i działają. Akcje v4 deklarują Node 20; gdy GitHub wycofa Node 20, trzeba je podbić (do v5, z nowymi SHA).
- `explore --config` waliduje konfigurację przed „not implemented”. To świadome, małe rozszerzenie zakresu (jedyny sposób, żeby walidacja była osiągalna z CLI). Brak innych opcji globalnych (`--workspace` itp.).
- Komunikat „Unknown key 'x'” pokazuje nazwę klucza (do 64 znaków `[A-Za-z0-9_-]`), nigdy wartość.
- Redaktor jest heurystyczny poza zarejestrowanymi wartościami. Fałszywe trafienia (np. `password: secret://…` w logu) są akceptowane, bo bezpieczeństwo jest ważniejsze od czytelności. Maskowanie screenshotów to P1 (§10.3).
- `IUnitOfWork`, `IArtifactStore`, kontrakt agenta: kształty orientacyjne. Docelowe sygnatury ustalają STEP-03 i STEP-04 (to może być zmiana łamiąca, ale nie ma jeszcze konsumentów).
- `Version7IdGenerator` używa losowych bitów UUIDv7 i jest tylko produkcyjny. Testy i E2E używają `SequentialIdGenerator`.
- Brak kontenera DI (Microsoft.Extensions.DependencyInjection). Można go dodać, gdy pojawią się adaptery (STEP-02+).

## Stan roadmapy

- STEP-00: zmergowany (PR #2). STEP-01: PR #3 otwarty. Tabela w `docs/ROADMAP.md` jest zaktualizowana.
- Pozostaje: STEP-02…12 (+02B). Decyzje Roberta z ROADMAP bez zmian; STEP-02 zależy od decyzji 3 (runner macOS) tylko w części iOS smoke.

## Warunki startu STEP-02

- PR #3 (`feat/step-01-foundation`) **zmergowany** do `main`, CI zielone na `main`, lokalny `main` zaktualizowany (`git fetch origin main && git checkout main && git pull`).
- .NET SDK 10 (lokalnie albo przez CI). Archiwum TapCue 0.1.0 z SHA (`docs/spikes/tapcue-smoke/`). Dla smoke Android: emulator (KVM) lub runner z emulatorem; dla iOS: runner `macos-26` (`workflow_dispatch`) albo NOT RUN z kryterium R-01.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-02: TapCue adapter.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-02, rejestr ryzyk R-01/R-02/R-03/R-08/R-12/R-13 i wspólne DONE), docs/architecture.md (§4, §5.1, §10.1, §10.3, §11), docs/tapcue-compatibility.md (całość, zwłaszcza §2.3 i §6), ADR-0002, docs/spikes/tapcue-smoke/ oraz docs/handovers/STEP-01.md.
> Zweryfikuj, że PR-01 (<https://github.com/rtnk/MapCue/pull/3>) jest zmergowany, a `main` aktualny i z zielonym CI. Jeśli nie jest, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-02-tapcue-adapter` i `docs/handovers/STEP-02.md` w tym samym PR. Nie rozpoczynaj STEP-03/02B i nie merguj PR.
> Zakres: implementacja `IDeviceAutomation` w `src/MapCue.Adapters.TapCue` wyłącznie przez publiczne CLI/MCP TapCue (bez klas wewnętrznych, bez `maui-dev-mcp`):
> - klient MCP STDIO (JSON-RPC line-delimited, handshake, `tools/list` z weryfikacją wymaganych narzędzi);
> - `TapCueCliRunner` (`--version`, `check-syntax`, `hierarchy --no-reinstall-driver`, `test --test-output-dir`);
> - generator jednoakcyjnego YAML z `ValidatedDeviceAction` (szablony, escapowanie, zakazane komendy z §2.3);
> - twarde timeouty z budżetów konfiguracji z zabiciem drzewa procesów, restart sesji, `OutcomeUnknown` bez auto-retry dla akcji zmieniających stan;
> - proces TapCue z czystym środowiskiem (bez `MAESTRO_*`) i redakcja `env_vars` przez `IRedactor` (G-06/R-13, z testem);
> - klasyfikacja błędów, preflight wersji, prawdziwa komenda `mapcue doctor` (zamiast zaślepki, z aktualizacją snapshotu help, jeśli się zmieni).
> Testy: kontraktowe na nagranych fixtures JSONL w `tests/MapCue.Adapters.TapCue.ContractTests` (bez urządzenia, w CI) i integracyjne z fałszywym serwerem MCP jako proces testowy (timeout, zawieszenie, zerwane STDIO). Smoke Android (emulator: `list_devices` → `launchApp` Settings → `inspect_screen` → `back` → `inspect_screen` → `hierarchy` → `test --test-output-dir`) oraz smoke iOS jako osobny job `workflow_dispatch` na `macos-26`: PASS albo NOT RUN/BLOCKED z przyczyną i kryterium odblokowania. Zmierz czasy (PoC ADR-0002) i kolizję MCP z CLI (R-03). Akcje CI przypinaj do SHA, `permissions: contents: read`.
> DoD: realny odczyt ekranu i bezpieczna akcja na ≥ 1 środowisku (Android) z zapisanym dowodem albo jawne NOT RUN z przyczyną; brak ścieżek na sztywno; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury nadal przechodzi; handoff STEP-02 zapisany. Wyniki raportuj jako PASS, FAIL albo NOT RUN, z komendami.
