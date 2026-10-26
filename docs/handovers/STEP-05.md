# HANDOFF: STEP-05: Vertical slice crawler

## Status i identyfikatory

- Status: **DONE** (zakres ROADMAP STEP-05 zrealizowany; demo na emulatorze PASS w CI; lokalnie bez urządzenia, R-09). Merge wykonuje Robert.
- Branch: `feat/step-05-vertical-slice` (nazwa z promptu etapu; środowisko sesji nie wyznaczyło innego brancha)
- PR URL i status (open / merged / not created): <https://github.com/rtnk/MapCue/pull/8> (open, czeka na review i merge)
- Commit SHA: kod i dokumentacja do `9c515e3`; ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR. Demo na emulatorze wykonano na commicie `9bd464c` (silnik przed poprawkami z przeglądu `25414f3`); CI PR uruchamia je ponownie na head.
- Base branch/SHA: `main` @ `b99b1956453d9180c60fa45f0be5775913abb25a` (merge PR #7; CI run 40358579524 success, Demo app run 40358579569 success)
- Data i środowisko: 2026-10-26. Lokalnie: kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.113 (pakiet `dotnet-sdk-10.0`, doinstalowany w sesji), bez urządzenia, Android SDK i TapCue (R-09). Emulator: GitHub Actions `ubuntu-24.04` z KVM, Android API 34 `google_apis` x86_64, TapCue 0.1.0 (archiwum z przypiętym SHA-256), Zulu JDK 17, APK demo z artefaktu `demo-apk` tego samego runu.

## Zakres faktycznie wykonany

- **Gate:**
  - PR #7 zmergowany, `main` aktualny, CI i Demo app zielone;
  - ROADMAP: status 02B zmieniony na „zmergowany (PR #7)”;
  - handoff STEP-02B: status PR zmieniony na merged.
- **`ExplorationEngine`** (`src/MapCue.Application/Exploration/`, architektura §5.5):
  - **run:** `Created → Preflight → Running → Finished(reason)`, tabela `runs`;
  - **preflight:** miejsce na dysku, urządzenie (połączone, z allowlisty, platforma profilu albo `--device`), plik `setupFlow`, reset (`setupFlow` przez `RunFlowAsync`, inaczej `launchApp`), aplikacja na pierwszym planie, commit stanu startowego;
  - **pętla, `AnalyzeState`:** `AgentInterpreter` na zapisanym snapshocie (bez urządzenia). Zaakceptowane akcje trafiają do zadań `ExploreAction` z zapisaną `ValidatedDeviceAction` (`task_actions`);
  - **pętla, `ExploreAction`:** nawigacja do stanu źródłowego (BFS po krawędziach `observed`, inaczej reset i jedna ponowna próba), świeży pre-snapshot, `PreExecutionGuard` (`ElementStale` z dowodem), zapis fazy `ActionDispatched`, `ExecuteAsync`, stabilizacja (`UiStabilizer`: 2× ten sam `identityHash` bez `loading` albo `uiSettleTimeout`) i `GraphCommitter.CommitAttemptAsync`;
  - **wybór zadań:** analizy najpierw, potem lokalność (najbliższy stan po znanych krawędziach), `back` z niższym priorytetem; `maxDepth` daje `Cancelled`;
  - **wyjście z `appId`** (`back` na ekranach głównych; `AppPresenceDetector` po pakietach `resource-id`): krawędź `executed` bez celu, ekran launchera jako dowód próby (nie stan), reset przed kolejnym zadaniem;
  - **wyniki akcji:** `OutcomeUnknown` bez ponowienia (status zadania `OutcomeUnknown`, reset). `Failed`/`Rejected` dają `Failed`. Awaria obserwacji po akcji też kończy się commitem próby, a seria awarii urządzenia kończy run jako `Failed`;
  - **budżety:** `maxActions`, `maxDuration`, `maxStates`, `maxScreens`, `maxDepth` oraz budżet reasonera z sumą workspace;
  - **zakończenie:** `Exhausted` / `AllRemainingBlocked` / `BudgetExhausted` / `PreflightFailed` / `Failed` / `StoppedByOperator`, checkpoint, `run.json`;
  - **artefakty prób:** `runs/<runId>/tasks/<taskId>/attempts/<n>/attempt.json` (linki SHA-256 do zredagowanych blobów: before/after, wynik narzędzia `ToolOutput`, ekran spoza aplikacji).
- **Prosty frontier w SQLite:** migracja `0004_runs_task_actions` (`runs`, `task_actions`), `GraphCommitter.ScheduleActionsAsync`, dowody próby (`AttemptCommit.AttemptEvidence`, klucz z `attemptId`). Bez lease, claim i odzysku (STEP-06).
- **CLI:**
  - `mapcue explore --config [--profile] [--device] [--seed] [--app-build]`: sekrety profilu rozwiązywane przed runem, generatory jako stałe syntetyczne;
  - `mapcue status [--config|--workspace] [--run]`: tylko odczyt, bez blokady, nie tworzy workspace;
  - nowe kody wyjścia: `7` (preflight), `8` (błąd albo przerwanie runu).
- **Konfiguracja:**
  - `agent.reasoner: rule-based|cli` (domyślnie `rule-based`);
  - `profiles[].setupFlow` (ścieżka względem pliku konfiguracji);
  - `identity.screenHints` (regex, timeout 100 ms);
  - nowy kod błędu `CFG080`.
- **Kalibracja normalizacji (R-04, ADR-0006 „Implementacja w STEP-05”):**
  - `FingerprintVersion = 2`: kontenery frameworka (`android:id/content` oraz MAUI `action_bar_root`, `navigation_layout`, `navigationlayout_content`, `nav_host`) nie są kotwicami tożsamości;
  - `screenHints` w `ScreenIdentityResolver`;
  - testy na `recordings/`: MAUI `AutomationId` = `resource-id` z siłą `ResourceId`; Accounts nie jest już stanem Home (v1: Jaccard 0,69, v2: 0,43); Home `investor` jest wariantem Home `basic` (6/7); `AlertDialog` ma `modal`, a strona modalna MAUI nie; launcher daje `Left`.
- **Testy:**
  - `FakeDevice`: nawigacja push/replace/pop/root, `back` z ekranu głównego do launchera, `launchApp`, setup flow, ekran ładowania, popup ignorujący `back`, usterki;
  - `FakeDemoApp`: model aplikacji demo plus bezpieczny `AlertDialog` About do testu modala.
- **Demo:**
  - `samples/MapCue.DemoApp/mapcue.demo.yaml`, `run-explore.sh`, `compare-expected.py` (wyrocznia);
  - job `android-explore` w `demo-app.yml`, uruchamiany także przy zmianach `src/**`.

## Pliki / API / migracje

- **Nowe:**
  - `src/MapCue.Application/Exploration/{ExplorationEngine,ExplorationOptions,UiStabilizer,AppPresence}.cs`;
  - `src/MapCue.Application/Time/SeededIdGenerator.cs`, `src/MapCue.Domain/Graph/ExplorationRun.cs`;
  - `src/MapCue.Infrastructure.Sqlite/Migrations/0004_runs_task_actions.sql`;
  - `src/MapCue.Cli/{ExploreCommand,StatusCommand}.cs`;
  - `samples/MapCue.DemoApp/{mapcue.demo.yaml,run-explore.sh,compare-expected.py}`;
  - `tests/MapCue.Testing/FakeDemoApp.cs`;
  - testy: `tests/MapCue.EndToEnd.Tests/{ExplorationEngineTests,ExplorationHarness}.cs`, `tests/MapCue.Cli.Tests/ExploreCommandTests.cs`, `tests/MapCue.Application.Tests/{ExploreConfigTests.cs,Exploration/UiStabilizerTests.cs}`, `tests/MapCue.Infrastructure.Tests/Graph/RunStoreTests.cs`, `tests/MapCue.Adapters.TapCue.ContractTests/DemoAppRecordingCalibrationTests.cs`.
- **Zmienione:**
  - `SnapshotNormalizer` (v2), `SystemUiRules.cs` (`FrameworkContainerRules`), `ScreenIdentityResolver` (`ScreenHints`), `GraphCommitter` (`AttemptEvidence`, `ScheduleActionsAsync`);
  - `IStateStore.cs` (`IUnitOfWork.Runs`, `IRunRepository`, `TaskAction`, `IFrontierRepository.{Insert,Get}TaskAction…`), repozytoria SQLite;
  - konfiguracja (`MapCueSettings`, dokument, walidator, `ConfigError`);
  - `CliHost` (`DeviceFactory`), `ExitCodes`, `MapCueCli`;
  - `FakeDevice`;
  - `.github/workflows/{ci.yml,demo-app.yml}`, `.gitignore` (`.mapcue-demo/`);
  - dokumenty: README, ROADMAP, architektura §5.5 i §7, ADR-0006, przykład konfiguracji, handoff 02B.
- **Zmiany łamiące** (bez zewnętrznych konsumentów):
  - `FingerprintVersion` 1 → 2: stany zapisane w v1 nie są porównywane z v2, a resolver porównuje tylko tę samą wersję. Workspace z STEP-03/04 wymaga ponownej eksploracji albo importu;
  - `IUnitOfWork.Runs` i nowe metody `IFrontierRepository`;
  - `mapcue status` nie zwraca już kodu `3`;
  - `mapcue explore` wykonuje eksplorację zamiast kodu `3`.
- **Migracja:** `0004` (przyrostowo od 3, testowana).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #7 zmergowany, `main` aktualny, CI i Demo app zielone | GitHub API: PR #7 `merged` 16:22; `main` = `b99b195`; runy 40358579524 (CI) i 40358579569 (Demo app) `success` | PASS | — |
| Build Release (0 ostrzeżeń) | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.113 | PASS | — |
| Testy (11 projektów: 669 pass, 1 skip: smoke CLI agenta bez zmiennej) | `dotnet test MapCue.sln -c Release --no-build` / lokalnie | PASS | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` / lokalnie | PASS | — |
| Test architektury (17) | `MapCue.Architecture.Tests` | PASS | — |
| E2E na `FakeDevice`: ≥ 5 stanów (Home, Accounts, Details, What's new, Settings, Verify, Profile + modal About), ≥ 7 ekranów, cykle Home ↔ Accounts, Details → Home, Settings → Profile → Home, modal (`StateKind.Modal`), polityka (delete, sendcode, signout, investor, przełączniki niewykonane, kandydaci `SkippedByPolicy`), wyjście z aplikacji i reset | `ExplorationEngineTests.Explores_the_demo_app_with_cycle_modal_and_policy` | PASS | — |
| Każda krawędź ma dowody before/after (snapshot przed, wynik narzędzia, snapshot po albo ekran spoza aplikacji; bloby istnieją; `attempt.json` na próbę) | `Every_edge_has_before_and_after_evidence` | PASS | — |
| Determinizm: dwa runy z tym samym ziarnem dają identyczny graf kanoniczny, zdarzenia (id, typ, payload), run id, akcje | `Two_runs_with_the_same_seed_give_the_same_graph`, `ExploreCommandTests.Same_seed_gives_the_same_run_id` | PASS | — |
| Budżety (`maxActions`, `maxDepth`), `OutcomeUnknown` bez ponowienia, `Failed`, awaria obserwacji po akcji, `ElementStale` → `AllRemainingBlocked`, preflight (brak setup flow, launcher zamiast aplikacji, nieznane urządzenie), anulowanie, drugi run bez nowych prób, start bez setup flow i ekran ładowania | `ExplorationEngineTests` (20) | PASS | — |
| CLI `explore`/`status` na `FakeDevice` (kody 0/1/5/7, brak sekretu bez echa wartości, `status` nie tworzy workspace) | `ExploreCommandTests` (7) | PASS | — |
| Kalibracja na nagraniach demo (R-04) | `DemoAppRecordingCalibrationTests` (5), `ScreenIdentityResolverTests` (hinty), przepięte hashe v2 (`RecordedSnapshotFingerprintTests`) | PASS | — |
| `compare-expected.py` na workspace z `FakeDevice` (konfiguracja demo przez CLI) | skrypt w scratchpadzie / lokalnie | PASS: 7 stanów, 26 krawędzi, 25/25 oczekiwanych przejść, kontrole twarde PASS | — |
| `check-consistency.py` | `python3 -I samples/MapCue.DemoApp/check-consistency.py` | PASS (34 id, 5 flow) | — |
| **Demo na emulatorze z TapCue 0.1.0** (`mapcue explore` + `compare-expected.py`) | `.github/workflows/demo-app.yml` job `android-explore` (`workflow_dispatch` na branchu), run 40363853291, job 121717745333, commit `9bd464c` | **PASS**: `Exhausted` w 268 s; 7 stanów i 7 ekranów (Home, Accounts, Details, What's new, Settings, Verify, Profile), 26 krawędzi; 25/25 przejść `expected-graph.md` (basic, domyślna polityka); kontrole twarde `compare-expected.py` PASS (≥ 3 stany, 0 akcji ryzykownych, dowody before/after); `mapcue status`: 26 prób, 9 kroków nawigacji, 5 resetów, 4 wyjścia z aplikacji, 0 `OutcomeUnknown` | artefakt `demo-android-explore` (raport, `explore.txt`, `status.txt`, workspace) |
| Demo na emulatorze lokalnie | — | NOT RUN: brak Android SDK i emulatora w sesji (`dl.google.com` blokowane, R-09) | — |
| CI na PR | `ci.yml`, `demo-app.yml` | wynik w PR #8 (Checks) | PR → Checks |

Demo na emulatorze (szczegóły w `docs/tapcue-compatibility.md` §11, raport w podsumowaniu joba i w artefakcie `demo-android-explore`):

- Akcje pominięte przez politykę: `details.delete` (accountChange), `profile.investor` (accountChange, reguła operatora), `scenario.empty` i `settings.notifications` (stateChanging przy `shared-readonly`), `verify.sendcode` (communication), `verify.signout` (accountChange).
- Identyfikatory stanów Home, What's new, Settings i Profile są takie same na emulatorze i w modelu `FakeDemoApp`, więc fake odwzorowuje tożsamość tych ekranów 1:1. Accounts, Details i Verify różnią się strukturą od modelu, ale były stabilne.
- 6 sesji MCP, bo każdy reset przez `tapcue test` zamyka sesję (R-03). Nie było `DriverLost`.

## Ograniczenia, ryzyka, otwarte pytania

- **Bez lease, odzysku i trwałych recipe** (STEP-06/09):
  - przerwanie procesu zostawia zadanie `Pending` i ewentualnie próbę w fazie `ActionDispatched` bez commitu;
  - nawigacja i reset żyją tylko w runie; kroki nawigacji nie są zapisywane jako próby (to replay, nie weryfikacja `VerifyEdge`).
- **Wykrywanie wyjścia z aplikacji** to heurystyka po pakietach `resource-id`, bo TapCue 0.1.0 nie podaje aplikacji na pierwszym planie:
  - okno bez identyfikatorów aplikacji i z obcym pakietem daje `Left`;
  - dialog z samymi `android:id/*` daje `Unknown` i jest traktowany jak aplikacja;
  - ekran bez żadnych `resource-id` też daje `Unknown`.
- **Margines wariantu Home `investor`** (Jaccard 0,857 przy `τ_state` = 0,85) jest mały; do sprawdzenia w STEP-07.
- **Kontekst `basic` a wybór profilu:** kontekst może trafić do ekranu wyboru profilu (`settings.switchprofile`), skąd `profile.investor` zmieniłby profil. Konfiguracja demo blokuje go regułą operatora (`accountChange`). Ogólne rozwiązanie (zakaz zmiany profilu w kontekście) to STEP-07.
- **Strona modalna MAUI** (What's new, Verify) nie dostaje `stateKind: modal`; to osobny ekran przez `screenHints`. Brak nagrania strony modalnej; surowe snapshoty z joba `android-explore` są w artefakcie.
- **`maxStates`/`maxScreens`** liczą cały graf workspace, nie run.
- **Budżet reasonera** to suma workspace (jak w STEP-04).
- **`tapcue test` (setup flow)** zapisuje własne, niezredagowane wyjście w `<workspace>/tmp/tapcue/test-output` (zachowanie adaptera z STEP-02); baza i `runs/` do niego nie odsyłają. Setup demo nie ma sekretów.
- **Verify (blokada):** run odkrywa stan i jego pętlę `back`. `Blocked(UnknownOverlay)` i `resume.md` są w STEP-09.
- **Wydajność:** `GraphQueries.SummarizeAsync` i graf nawigacji liczone są na każde zadanie (O(n) zapytań); wystarcza dla MVP, do optymalizacji w STEP-06.
- **Pytania do Roberta:**
  1. czy `mapcue explore` ma domyślnie odmawiać zmiany profilu (STEP-07), czy zostawić to regułom operatora;
  2. czy `mapcue status` ma pokazywać pozostały frontier z priorytetami (teraz tylko liczniki).

## Stan roadmapy

- STEP-00…04 i 02B: zmergowane. STEP-05: PR #8 otwarty.
- W `docs/ROADMAP.md` zaktualizowane: tabela statusów (02B, 05), R-03, R-04 i macierz platform.
- Następny: STEP-06 (Frontier scheduler), po merge tego PR.

## Warunki startu STEP-06

- PR STEP-05 **zmergowany** do `main`, CI (`CI`, `Demo app`) zielone na `main`, lokalny `main` zaktualizowany.
- Kontrakty z tego etapu, na których STEP-06 się opiera:
  - `ExplorationEngine` i `RunSession` (`NextTaskAsync`, `ExploreFromSourceAsync`, `NavigateToAsync`, `ResetAsync`);
  - tabele `runs` i `task_actions`, `TaskAttempt` w fazie `ActionDispatched` zapisywany przed akcją;
  - `TaskKeys` i `GraphCommitter.ScheduleActionsAsync` (`ON CONFLICT DO NOTHING`);
  - `SeededIdGenerator`, identyfikator próby `H(taskId, n)`;
  - testy E2E na `FakeDevice` (`ExplorationHarness`, `FakeDemoApp`) do rozbudowy o kill procesu w każdej fazie.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-06: Frontier scheduler.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-06, rejestr ryzyk R-02, R-03, R-11 i wspólne DONE), docs/architecture.md (§5.1, §5.3, §5.4, §5.5, §7.4, §12), ADR-0003 i ADR-0006, docs/handovers/STEP-05.md oraz kod: `src/MapCue.Application/Exploration/` (`ExplorationEngine`, `UiStabilizer`), `src/MapCue.Application/Graph/GraphCommitter.cs`, `src/MapCue.Application/Ports/IStateStore.cs`, `src/MapCue.Infrastructure.Sqlite/` (migracje 0001–0004, `SqliteRepositories`), `tests/MapCue.EndToEnd.Tests/` i `tests/MapCue.Testing/{FakeDevice,FakeDemoApp}.cs`.
> Zweryfikuj, że PR STEP-05 (<https://github.com/rtnk/MapCue/pull/8>) jest zmergowany, a `main` aktualny i z zielonym CI (`CI` i `Demo app`). Jeśli nie, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-06-frontier-scheduler` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-06.md` w tym samym PR. Nie rozpoczynaj STEP-07 i nie merguj PR.
> Zakres (ROADMAP STEP-06): lease i claim zadań, dzierżawa urządzenia, priorytety (lokalność, nowość, głębokość, ryzyko), budżety runu, idempotentne próby (UNIQUE `attemptId`), fazy próby i rekoncyliacja po restarcie (architektura §5.3), deterministyczne kryteria zakończenia, `representedBy` dla wierszy list.
> Testy: kill procesu w każdej fazie próby (test integracyjny z procesem potomnym), brak podwójnego zatwierdzenia krawędzi, terminacja na grafie z cyklem i z nieskończoną listą, budżet zatrzymuje się w przewidywalnym punkcie. Wyniki urządzeniowe raportuj jako PASS, FAIL albo NOT RUN, z komendami.
> DoD: wznowienie po awarii bez utraty postępu, brak podwójnych commitów, terminacja na grafie z cyklem; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury przechodzi; ROADMAP i handoff STEP-06 zaktualizowane.
