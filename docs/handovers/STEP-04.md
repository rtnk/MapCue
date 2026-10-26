# HANDOFF: STEP-04: Agent interpretation

## Status i identyfikatory

- Status: **DONE** (zakres ROADMAP STEP-04 zrealizowany i przetestowany; smoke w kontenerze NOT RUN, opis niżej). Merge wykonuje Robert.
- Branch: `claude/step-04-agent-interpretation-i7rote` (prompt etapu wskazywał `feat/step-04-agent-interpretation`; środowisko sesji wyznacza ten branch)
- PR URL i status: <https://github.com/rtnk/MapCue/pull/6> (open, czeka na review i merge)
- Commit SHA: kod `c11fd0c`; ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `badb864824eb9b276194ff36f73733352d115be0` (merge PR #5; CI na `main`: success, run 40283143760)
- Data i środowisko: 2026-10-26. Kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.113 (pakiet Ubuntu `dotnet-sdk-10.0`, doinstalowany w sesji), Claude Code CLI 2.1.310, klient Docker bez demona, bez urządzenia i TapCue (niepotrzebne w tym etapie).

## Zakres faktycznie wykonany

- **Schematy** `schemas/agent-exploration-request.v1.schema.json` i `schemas/agent-exploration-response.v1.schema.json`:
  - JSON Schema 2020-12, każdy obiekt `additionalProperties: false`, wszystkie pola wymagane;
  - teksty bez znaków sterujących, z limitem długości;
  - `elementRef` w formacie `e<n>`, `inputText` tylko z `valueRef` (`profile.inputs.*`) albo `generator` (`email|name|text|number`).
- **Kontrakty** (`MapCue.Contracts`):
  - pełne DTO v1 i `AgentJson` (camelCase, nieznane pola odrzucane);
  - schematy osadzone (`ContractSchemas`);
  - `Schemas.JsonSchema`: walidator podzbioru 2020-12 bez zależności. Nieznane słowo kluczowe schematu powoduje błąd przy ładowaniu (fail closed). Zduplikowane klucze JSON są błędem. Nietypowe nazwy pól nie są cytowane w błędach.
- **Żądanie** (`Application/Agent/AgentRequestBuilder`, `GraphHintsBuilder`):
  - z `RecordedSnapshot` brane są tylko elementy interaktywne lub z etykietą/`resource-id` (maks. 400);
  - etykiety zamaskowane (`TextMasker`), bez oryginalnego tekstu, wartości wejść i screenshotu;
  - `snapshotHash = UiSnapshot.NormalizedSha256` (decyzja otwarta w STEP-03: `elementRef` są ważne tylko w tym dokumencie, `identityHash` ignoruje kolejność);
  - `requestId` deterministyczny; `graphHints` to top-5 stanów wg Jaccarda (≥ 0,3) i znane `actionKey` stanu;
  - polityka w żądaniu jest informacyjna, egzekwuje ją MapCue.
- **Walidator** (`AgentResponseValidator`), w kolejności:
  1. rozmiar ≤ 256 KiB, JSON i schemat;
  2. stale (`requestId`, `snapshotHash`);
  3. treść zakazana (`ContentGuard`: YAML i komendy Maestro, powłoka, ścieżki, URL) odrzuca całą odpowiedź;
  4. `sameAsStateId` spoza hintów i fakty z nieznanymi refami są pomijane (z błędem w zapisie);
  5. per kandydat: limit, unikalne `candidateId` i akcja, typ w allowliście, element istnieje, jest interaktywny i włączony, `inputText` tylko do pola tekstowego i tylko z wejściem tego profilu albo generatorem, kierunek tylko dla scroll/swipe;
  6. ryzyko `max(agent, RiskClassifier)` (nieznana klasa agenta = `unknown`);
  7. `PolicyGate`;
  8. `ValidatedDeviceAction` (`actionId` deterministyczny, `appId` z polityki, selektor `resource-id`, potem tekst, każdy tylko jeśli unikalny na ekranie).

  `PreExecutionGuard` porównuje `identityHash` i `elementKey` na świeżym snapshocie i zwraca `ElementStale` (do użycia w STEP-05).
- **Ryzyko i polityka**:
  - `RiskClassifier`: bazowe ryzyko z typu akcji (back/scroll/swipe = `safe`, tap = `navigational`, inputText = `stateChanging`, longPress i inne = `unknown`), `toggle` = `stateChanging`;
  - słowa kluczowe EN/PL/DE/FR/ES/IT/PT z foldowaniem diakrytyków bez ICU (`TextFold`), dopasowanie słów lub prefiksów (`usuń*`), także słowa z `resource-id`;
  - reguły operatora (`policy.riskRules`: `resourceId`, `resourceIdPattern`, `labelPattern`) tylko podnoszą ryzyko, a timeout regexu liczy się jako trafienie;
  - `PolicyGate`: aplikacja z allowlisty, typ akcji z allowlisty (poza nią zawsze `Rejected`). `safe`/`navigational` są dozwolone, `stateChanging`/`unknown` tylko przy `disposable`. Klasy wysokiego ryzyka przy `disposable` dają `RequiresApproval`, a przy `disposable` ze zgodą kategorii są dozwolone. Przy `shared-readonly` wszystko powyżej `navigational` daje `SkippedByPolicy`;
  - `AgentPolicy.For(profile)`: domyślne typy akcji (bez `longPress`), brak zgód.
- **Orkiestracja** (`AgentInterpreter`, `AgentBudget`):
  - hinty z grafu, potem budowa żądania;
  - zapis żądania przez `PutTextAsync`; wysyłany jest dokładnie zapisany (zredagowany) tekst. Jeśli redakcja zepsuje schemat, nic nie jest wysyłane (`Rejected`);
  - kontrola budżetu (z estymatą tokenów) i wywołanie; wyjątek adaptera = `Unavailable`;
  - zapis odpowiedzi i walidacja zapisanego tekstu;
  - jedna transakcja: `AgentCall` + `ActionCandidate` (`CandidateSource.Agent`, `Proposed`/`SkippedByPolicy`) + zdarzenie `agent.call`;
  - `SchemaInvalid` daje najwyżej 1 naprawę (`repair.previousErrors`). `Unavailable`/`TimedOut` dają `Blocked(ReasonerUnavailable)`, budżet daje `Blocked(ReasonerBudgetExhausted)`.
- **Reasonery** (`MapCue.Adapters.Reasoning`):
  - `DeterministicFakeReasoner` (przeniesiony z `MapCue.Testing`): skrypt surowych odpowiedzi po `snapshotHash`, w kolejności;
  - `RuleBasedReasoner`: taps poza listami, maks. 2 wiersze list, scroll, `inputText` do pól nazwanych jak wejście profilu, back; ryzyko z tego samego klasyfikatora; teksty nie cytują etykiet;
  - `CliAgentReasoner` (`claude -p --output-format json --json-schema … --tools "" --strict-mcp-config --disable-slash-commands --no-session-persistence [--bare] [--model]`):
    - kontener: `run --rm -i --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges --pids-limit 256 --memory 1g --cpus 1`, bez wolumenów, klucz po nazwie, po timeoucie `kill` kontenera;
    - `local`: czyste env, pusty katalog roboczy;
    - brak obrazu, silnika albo niezerowy kod wyjścia dają `Unavailable` bez fallbacku;
    - `AgentProcessLauncher`: bez powłoki, czyste env, twardy timeout z zabiciem drzewa, limit wyjścia 1 MiB.
- **Konfiguracja**:
  - `agent.provider` (tylko `claude`), `agent.executable`, `agent.containerEngine`, `agent.containerImage` (bez domyślnej), `agent.containerNetwork`, `agent.model`, `agent.timeout` (5 s–30 min, domyślnie 180 s), `agent.apiKey` (tylko `secret://env/NAME`);
  - `policy.riskRules`;
  - `agent.sendScreenshots: true` jest teraz błędem `CFG032`. Nowe kody `CFG032` i `CFG070`.

## Pliki / API / migracje

- Nowe:
  - `schemas/*.schema.json`;
  - `src/MapCue.Contracts/{Agent/AgentJson.cs,Schemas/JsonSchema.cs,Schemas/ContractSchemas.cs}`;
  - `src/MapCue.Domain/Graph/AgentCall.cs`;
  - `src/MapCue.Application/Agent/{AgentPolicy,PolicyGate,RiskClassifier,RiskKeywords,ContentGuard,AgentRequestBuilder,AgentResponseValidator,AgentBudget,AgentInterpreter}.cs`;
  - `src/MapCue.Adapters.Reasoning/{DeterministicFakeReasoner,RuleBasedReasoner}.cs`, `Cli/{AgentProcess,AgentCommandBuilder,ClaudeEnvelope,CliAgentReasoner}.cs`;
  - `src/MapCue.Infrastructure.Sqlite/Migrations/0003_agent_calls.sql`;
  - `tests/MapCue.Adapters.Reasoning.Tests/` (nowy projekt w solucji);
  - `tests/Fixtures/agent-responses/` (22 złote odpowiedzi + README);
  - `tests/MapCue.Application.Tests/Agent/*`, `tests/MapCue.Contracts.Tests/{AgentSchemaTests,AgentContractSamples}.cs`, `tests/MapCue.Infrastructure.Tests/Graph/AgentCallStoreTests.cs`.
- Zmienione:
  - `Application/Ports/{IExplorationReasoner,IStateStore}.cs`, `Application/Configuration/*`;
  - `Contracts/Agent/AgentExploration{Request,Response}.cs`, `MapCue.Contracts.csproj` (osadzenie schematów);
  - `Domain/Graph/GraphEnums.cs`, `Infrastructure.Sqlite/{SqliteRepositories,SqliteStateStore}.cs`;
  - `Directory.Packages.props` (`JsonSchema.Net` 9.5.0, tylko testy);
  - `docs/examples/mapcue.example.yaml`, `README.md`, `docs/ROADMAP.md` (statusy, R-05, R-06, decyzja 2), `docs/architecture.md` (§9.5), `docs/adr/ADR-0004…` (implementacja), testy E2E/SQLite.
  - Usunięty: `tests/MapCue.Testing/DeterministicFakeReasoner.cs`.
- **Zmiany łamiące (bez konsumentów poza testami):**
  - `IExplorationReasoner.Name`; `ReasonerResult(Status, RawResponse, Usage, Detail)` zamiast DTO;
  - pełne DTO v1;
  - `IUnitOfWork.AgentCalls`; `BlockedReasonCode.ReasonerUnavailable`, `ReasonerBudgetExhausted`;
  - nowe klucze konfiguracji; `sendScreenshots: true` odrzucane.
- Migracja: `0003_agent_calls` (tabela `agent_calls`, `UNIQUE(request_id, call_number)`).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #5 zmergowany, `main` aktualny, CI zielone | GitHub API PR #5 `merged: true` (2026-10-26 07:00), `main` = `badb864` = HEAD startowy, run 40283143760 `success` | PASS | — |
| Build Release | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.113 | PASS (0 ostrzeżeń) | — |
| Testy (10 projektów: 599 pass, 1 skip) | `dotnet test MapCue.sln -c Release --no-build` / lokalnie | PASS | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` | PASS | — |
| Test architektury | `MapCue.Architecture.Tests` (17) | PASS | — |
| Złote JSON: poprawne (nawigacja, ryzyko i wejścia) i błędne (dodatkowe pole w kandydacie i na górze, brak pola, nieistniejący i nieinteraktywny `elementRef`, `elementRef` w złym formacie, inny `snapshotHash`, inny `requestId`, `inputText` z `value` literalnym i z `valueRef` literalnym, wejście spoza profilu, YAML jedno- i wielolinijkowy, komenda, URL i ścieżka, `openLink` spoza schematu, `longPress` spoza allowlisty, zduplikowane klucze, markdown z ogrodzeniem, ucięty JSON, klasa `destructive`) | `GoldenResponseTests` (`tests/Fixtures/agent-responses`) | PASS | fixtures |
| Akcja spoza allowlisty zawsze odrzucona (każdy typ ze schematu × 9 klas ryzyka × każdy element × izolacja × wszystkie zgody; aplikacja spoza allowlisty; typy spoza schematu) | `AllowlistTests`, `PolicyGateTests` | PASS | — |
| Naprawa formatu najwyżej 1×, z listą błędów; stale i treść zakazana bez naprawy | `AgentInterpreterTests` | PASS | — |
| Izolacja: brak fallbacku na host (fałszywy `docker` exit 125 + `claude` na `PATH` zostawiający znacznik; znacznik nie powstaje; `Blocked(ReasonerUnavailable)`), brak obrazu, `local` bez opt-in, kody 125/127, timeout i `kill` kontenera, brak sekretu w argumentach i brak dziedziczenia env | `CliAgentReasonerTests` | PASS | — |
| Walidator schematu = JsonSchema.Net na 21 złotych plikach i 22 mutacjach; schemat osadzony = plik; każdy obiekt zamknięty | `AgentSchemaTests` | PASS | — |
| Redakcja: e-mail z ekranu nie trafia do żądania ani do żadnego bloba; kwota salda zamaskowana | `AgentInterpreterTests.Request_values_are_redacted…`, `Request_is_schema_valid_minimal…` | PASS | — |
| `AgentCall` w SQLite (round-trip, idempotencja, sumy użycia), migracja 3 | `AgentCallStoreTests`, `SqliteStoreTests` | PASS | — |
| Smoke `CliAgentReasoner` z prawdziwym CLI, runtime `local` (jawny opt-in) | `MAPCUE_AGENT_SMOKE=local dotnet test tests/MapCue.Adapters.Reasoning.Tests -c Release --filter "FullyQualifiedName~CliAgentSmokeTests"` / Claude Code 2.1.310 w tej sesji | **PASS** (1 wywołanie `Accepted`, in 4824 / out 1941 tokenów, 0,0424 USD, 8 kandydatów: 6 `Accepted` safe/navigational, 2 `SkippedByPolicy` stateChanging) | wyjście testu |
| Smoke `CliAgentReasoner` w kontenerze | `MAPCUE_AGENT_SMOKE=container MAPCUE_AGENT_IMAGE=<obraz z claude> ANTHROPIC_API_KEY=… dotnet test … --filter …CliAgentSmokeTests` | **NOT RUN**: w środowisku jest klient Docker bez demona (`/var/run/docker.sock` nie istnieje) i nie ma obrazu z agentem CLI | — |
| Smoke CLI: `--help`, `graph import` 2× = ten sam graf, przykład konfiguracji przechodzi walidację (`explore` → kod 3) | jak w `ci.yml` | PASS | — |
| CI GitHub Actions na PR #6 | `.github/workflows/ci.yml` | wynik w PR (Checks) | PR #6 → Checks |

## Ograniczenia, ryzyka, otwarte pytania

- **Smoke w kontenerze: NOT RUN.** Kryterium: host z demonem Docker/Podman, obraz z `claude` (pinowany digestem) i `ANTHROPIC_API_KEY`. Flagi `--read-only`/`HOME=/tmp` mogą wymagać dopasowania obrazu (CLI pisze stan pod `HOME`).
- Podczas smoke wykryto, że `claude --json-schema` nie akceptuje `$schema` 2020-12, więc CLI dostaje kopię schematu bez `$schema`/`$id`. Przy zmianie wersji CLI trzeba powtórzyć smoke.
- `local` bez `agent.apiKey` działa bez `--bare`, więc CLI może wczytać ustawienia i hooki użytkownika z `~/.claude` (narzędzia i MCP są wyłączone, katalog roboczy pusty). To świadomy wybór operatora (`allowLocalExecution`).
- Klasyfikator słów kluczowych to heurystyka (R-06). Fałszywy alarm pomija akcję, a brak trafienia nie obniża ryzyka agenta. `ContentGuard` odrzuca całą odpowiedź także przy fałszywym trafieniu (np. URL w nazwie ekranu). Kalibracja na demo app (02B) w STEP-05.
- Selektor tekstowy używa zredagowanego tekstu oryginalnego. Tekst dynamiczny (np. saldo w wierszu listy) może się zmienić przed wykonaniem; wtedy `PreExecutionGuard` i `ElementNotFound` w STEP-05.
- Budżet jest w pamięci (`AgentBudget`). Wznowienie: `IAgentCallRepository.GetUsageTotalsAsync` (sumy dla workspace, bez podziału na run, bo run jeszcze nie istnieje). Wywołania niewykonane z powodu konfiguracji i błędy startu są liczone konserwatywnie jako wywołania.
- Zgody na kategorie ryzyka (`ApprovedRiskClasses`) i `longPress` nie mają jeszcze źródła w konfiguracji (STEP-07/09: profile, `resume --approve`).
- `proposedScreenName` i hipotezy są tylko w artefakcie odpowiedzi. Nie zmieniają `LogicalScreen.DisplayName` (ADR-0006: propozycje agenta to hipotezy). Wykorzystanie w STEP-05/07.
- Codex (`codex exec`) nie jest zaimplementowany (`agent.provider` przyjmuje tylko `claude`).
- Pytania do Roberta: (1) obraz kontenera agenta i runner z Dockerem dla smoke kontenerowego; (2) czy `local` bez klucza API ma wymuszać `--bare` (wtedy działa tylko z `ANTHROPIC_API_KEY`). Nadal otwarte decyzje z ROADMAP (02B, runner macOS).

## Stan roadmapy

- STEP-00…03: zmergowane. STEP-04: PR #6 otwarty. Tabela statusów, R-05, R-06 i decyzja 2 w `docs/ROADMAP.md` są zaktualizowane.
- Następne: STEP-05 (zależy od 02, 02B, 03, 04) po merge tego PR i po STEP-02B (decyzja 1), który można robić niezależnie.

## Warunki startu STEP-05

- PR #6 **zmergowany** do `main`, CI zielone na `main`, lokalny `main` zaktualizowany. STEP-02B zmergowany albo jawna zgoda Roberta na start bez niego (ROADMAP: 05 zależy od 02B).
- Kontrakty z tego etapu, na których STEP-05 się opiera:
  - `AgentInterpreter.InterpretAsync(AgentInterpretationInput)` dla zadania `AnalyzeState`; status `ReasonerUnavailable` i `BudgetExhausted` mapuje na `Blocked` z `BlockedReason`;
  - `AgentInterpretationResult.AcceptedActions` (`CandidateValidation.Action` = `ValidatedDeviceAction`, `Spec` = `ActionSpec` dla `GraphCommitter`);
  - `PreExecutionGuard.Check(...)` tuż przed `IDeviceAutomation.ExecuteAsync`;
  - `RuleBasedReasoner` jako reasoner MVP bez LLM, `AgentPolicy.For(profile)`, `RiskClassifier(settings.Policy.RiskRules)`, `PolicyGate(settings.Allowlist)`;
  - `AgentBudget(settings.Agent, totals)`; `CliAgentReasoner` z `AgentSettings`;
  - `inputText` z `InputValueRef` (`profile.inputs.*` albo `generated:<kind>`) rozwiązuje silnik przez `ISecretResolver`/generator.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-05: Vertical slice crawler.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-05, rejestr ryzyk R-03/R-04 i wspólne DONE), docs/architecture.md (§5, §7, §9.5, §10), ADR-0002, ADR-0004, ADR-0006, docs/handovers/STEP-04.md (i STEP-02B, jeśli istnieje) oraz kod: `src/MapCue.Application/Agent/` (`AgentInterpreter`, `AgentResponseValidator`, `PreExecutionGuard`, `PolicyGate`), `src/MapCue.Application/Graph/` (`SnapshotRecorder`, `GraphCommitter`), `src/MapCue.Adapters.Reasoning/`, `src/MapCue.Adapters.TapCue/TapCueDeviceAutomation.cs` i `tests/MapCue.Testing/FakeDevice.cs`.
> Zweryfikuj, że PR-04 (<https://github.com/rtnk/MapCue/pull/6>) jest zmergowany, a `main` aktualny i z zielonym CI, oraz że STEP-02B jest zmergowany albo Robert jawnie zgodził się na start bez niego. Jeśli nie, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-05-vertical-slice` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-05.md` w tym samym PR. Nie rozpoczynaj STEP-06 i nie merguj PR.
> Zakres (ROADMAP STEP-05): `ExplorationEngine` (preflight → pętla → zakończenie), pętla `AnalyzeState` (przez `AgentInterpreter`) + `ExploreAction` (`PreExecutionGuard` → `IDeviceAutomation.ExecuteAsync` → obserwacja → `GraphCommitter.CommitAttemptAsync`), stabilizacja UI, prosty frontier w SQLite (bez lease i odzysku), `mapcue explore --config`, `mapcue status`, artefakty prób.
> Testy: E2E na `FakeDevice` (≥ 5 stanów, cykl, modal), deterministyczność (dwa runy z tym samym ziarnem = ten sam graf), demo na emulatorze z TapCue: ≥ 3 stany odkryte z Fake albo RuleBased reasonerem (real LLM opcjonalnie). Wyniki urządzeniowe raportuj jako PASS, FAIL albo NOT RUN, z komendami.
> DoD: demo odkrywa ≥ 3 stany z realistycznym przebiegiem TapCue; każda krawędź ma dowody before/after; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury przechodzi; handoff STEP-05 zapisany.
