# HANDOFF: STEP-06: Frontier scheduler

## Status i identyfikatory

- Status: **DONE**. Zakres ROADMAP STEP-06 zrealizowany, kontrole lokalne PASS. Wynik CI i demo na emulatorze jest w Checks PR. Merge wykonuje Robert.
- Branch: `feat/step-06-frontier-scheduler` (nazwa z promptu etapu)
- PR URL i status (open / merged / not created): <https://github.com/rtnk/MapCue/pull/9> (merged 2026-10-29, merge `e07651b`)
- Commit SHA: kod `4b11af1`, dokumentacja `86ac67a`. Ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `044c5e40417837208469c660614fd48b5a11496f` (merge PR #8; CI run 40370435555 success, Demo app run 40370435341 success)
- Data i środowisko: 2026-10-29. Lokalnie kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.113 (`apt-get install dotnet-sdk-10.0` w sesji). Bez urządzenia, Android SDK i TapCue (R-09).

## Zakres faktycznie wykonany

- **Gate:**
  - PR #8 zmergowany, `main` aktualny, CI i Demo app zielone;
  - ROADMAP: 05 → „zmergowany (PR #8)”;
  - handoff STEP-05: status PR = merged.
- **Lease i claim:** `Pending → Leased` przez warunkowy `UPDATE … WHERE status = 'Pending'`. Właściciel = `runId`, czas = `ExplorationOptions.LeaseDuration` (domyślnie 10 min). Dla `ExploreAction` próba `Created` powstaje w tej samej transakcji. Każda faza odnawia lease.
- **Dzierżawa urządzenia:** tabela `device_leases`. Preflight odmawia, gdy inny run ma niewygasłą dzierżawę (`PreflightFailed`, „leased by run …”). Run odnawia dzierżawę w każdej iteracji i zwalnia ją przy zakończeniu.
- **Fazy próby** (zapis przed efektem): `Created → RestoreStarted → RestoreVerified → PreObserved → PolicyChecked → ActionDispatched → ActionReturned → PostObserved → Committed`.
  - `ChargeAction` przed `ActionDispatched`, więc budżet nigdy nie przerywa po wysłaniu akcji;
  - `Classified` nie jest osobną fazą, bo klasyfikacja jest w transakcji commitu;
  - commit ustawia końcowy status zadania i zwalnia lease atomowo (`AttemptCommit.TaskStatus`, `RunId`);
  - zakończenie bez commitu: próba `Abandoned` przed `ActionDispatched`, `OutcomeUnknown` po nim;
  - hook `ExplorationOptions.OnAttemptPhase`.
- **Rekoncyliacja** (`RecoverAsync`, na starcie runu, tylko z bazy):
  - z `WorkspaceLockHeld` (CLI): obce niezakończone runy dostają `Finished(Failed, "interrupted: …")`, a ich dzierżawy urządzeń są zwalniane;
  - bez blokady odzyskiwane są tylko wygasłe lease zadań;
  - ostatnia faza < `ActionDispatched`: zadanie wraca do `Pending`, a po więcej niż `maxRetries` przerwaniach dostaje `Failed`;
  - faza ≥ `ActionDispatched`: zadanie dostaje `OutcomeUnknown` (próba też), bez ponowienia;
  - zdarzenia `run.recovered` i `task.recovered`;
  - przy zakończeniu runu własne lease są zwalniane tą samą regułą.
- **`FrontierScheduler`:**
  - analizy najpierw;
  - potem: lokalność (BFS od bieżącego stanu, inaczej reset + ścieżka od stanu startowego), ryzyko (priorytet statyczny z `PriorityOf`: 0 / `back` −1 / `stateChanging`/`unknown` −2 / reszta −3), nowość (akcja nie zaobserwowana z innego stanu), głębokość, sekwencja, `taskId`;
  - `EstimatedCost`.
- **Budżety per run:**
  - przewidywalny stop `maxActions`: run nie zaczyna zadania, którego szacowany koszt przekracza resztę budżetu, a zadanie zostaje `Pending`;
  - `maxStates`/`maxScreens` liczą stany i ekrany nowe w runie (`RunCounters.NewScreens`);
  - budżet reasonera per run;
  - budżet wyczerpany w trakcie zadania oddaje je do `Pending`.
- **`representedBy`:** wiersze tego samego `listTemplate` ponad `maxListRowsPerTemplate` są zapisywane jako zadania `Cancelled` z `represented_by` = zadanie pierwszego wiersza. Analiza i planowanie to jedna transakcja (`ScheduleActionsAsync(…, completeTaskId)`).
- **Zakończenie:** `Exhausted` tylko bez zadań `Pending`, `Blocked` i bez zadań w lease innego runu. W pozostałych przypadkach `AllRemainingBlocked` z liczbami w `detail`.
- **Wydajność:** graf nawigacji z 2 zapytań (`ListTaskActionsAsync`) zamiast 2 zapytań na krawędź. Bez `GraphQueries.SummarizeAsync` w pętli.
- **CLI:** `explore` ustawia `WorkspaceLockHeld = true`, a `status` pokazuje `leased`, `new-screens`, `recovered` i `represented`.

## Pliki / API / migracje

- **Nowe:**
  - `src/MapCue.Application/Exploration/FrontierScheduler.cs`;
  - `src/MapCue.Infrastructure.Sqlite/Migrations/0005_leases_recovery.sql`;
  - `tests/MapCue.EndToEnd.CrashHost/` (exe procesu potomnego, w `MapCue.sln`);
  - testy: `tests/MapCue.EndToEnd.Tests/{RecoveryTests,FrontierSchedulerEndToEndTests,TestReasoners}.cs`, `tests/MapCue.Application.Tests/Exploration/FrontierSchedulerTests.cs`, `tests/MapCue.Infrastructure.Tests/Graph/LeaseStoreTests.cs`.
- **Zmienione:**
  - `ExplorationEngine`, `ExplorationOptions` (`LeaseDuration`, `WorkspaceLockHeld`, `OnAttemptPhase`, `AttemptPhaseEvent`), `GraphCommitter`;
  - `IStateStore.cs` (nowe metody), `SqliteRepositories`;
  - domena: `FrontierTask.RepresentedBy`, `TaskAttempt.RunId`, `RunCounters.{NewScreens,RecoveredTasks,RepresentedTasks,ChargedActions}`, `DeviceLease`;
  - `ExploreCommand`, `StatusCommand`;
  - `FakeDevice` (`FakeElement.Depth`, `Scrollable`), `ExplorationHarness` (`Open`, `NewRoot`, ustawienia per run);
  - `MapCue.sln`;
  - dokumenty: architektura §5.6 i §10.2, ADR-0003, ROADMAP, README, `docs/examples/mapcue.example.yaml`, handoff STEP-05.
- **Zmiany łamiące** (bez zewnętrznych konsumentów):
  - `maxStates`/`maxScreens` i budżet reasonera są per run;
  - `ScheduleActionsAsync` zwraca `ScheduleResult`;
  - usunięto `ExplorationEngine.BackPriority`;
  - próby zablokowane przed akcją mają wiersz `Abandoned`;
  - `mapcue status` ma nowe pola.
- **Migracja:** `0005` (przyrostowo od 4, testowana).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #8 zmergowany, `main` aktualny, CI i Demo app zielone | GitHub API: `main` = `044c5e4`; runy 40370435555 (CI), 40370435341 (Demo app) `success` | PASS | — |
| Baseline przed zmianami | `dotnet test MapCue.sln -c Release` na `044c5e4` / lokalnie | PASS (669 pass, 1 skip) | — |
| Build Release (0 ostrzeżeń) | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.113 | PASS | — |
| Testy (11 projektów testowych) | `dotnet test MapCue.sln -c Release --no-build` / lokalnie | PASS (702 pass, 1 skip: smoke CLI agenta bez zmiennej) | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` | PASS | — |
| Test architektury (17) | `MapCue.Architecture.Tests` | PASS | — |
| Kill procesu w każdej fazie próby (9 faz, `SIGKILL` procesu potomnego z blokadą workspace) i wznowienie: blokada zwolniona przez system, run przerwany → `Failed`, 0 lease, wszystkie próby zakończone, obserwacje = commity, `observationCount` = 1 na krawędź; przed `ActionDispatched` graf (stany, krawędzie) = graf runu bez awarii; po niej `OutcomeUnknown` bez ponownego wykonania; po `Committed` brak powtórzenia | `RecoveryTests.A_run_killed_in_any_attempt_phase_is_recovered_without_loss_or_double_commit` | PASS (9/9) | — |
| Kontrola mutacją: wyłączona rekoncyliacja | jw., kod tymczasowo zmieniony i przywrócony | testy wykrywają (12/13 FAIL) | — |
| Lease bez blokady (tylko wygasłe), dzierżawa urządzenia, martwy run z blokadą, `maxRetries` | `RecoveryTests` (4 testy in-process) | PASS | — |
| Terminacja na cyklu (3 stany, 6 prób, każda krawędź raz, bez resetów po preflight) | `FrontierSchedulerEndToEndTests.A_cycle_terminates_…` | PASS | — |
| Nieskończona lista (60 stron × 12 wierszy, reasoner proponujący wszystkie wiersze): `Exhausted`, 2 stany, 2 tapnięcia wierszy, 10 zadań `representedBy` | `An_endless_list_terminates_…` | PASS | — |
| Budżet w przewidywalnym punkcie: łańcuch stanów zatrzymany dokładnie przy `maxStates` = 5, deterministycznie; `maxActions` = 12 zatrzymuje przed zadaniem, na które budżet nie starcza (ten sam punkt w dwóch runach), a wznowienie daje stany, krawędzie i zadania identyczne z runem bez przerwy | `A_chain_…`, `The_action_budget_stops_…` | PASS | — |
| Budżet reasonera per run | `Agent_budget_is_per_run` | PASS | — |
| Scheduler (kolejność, porządek całkowity, priorytety ryzyka, koszt) | `FrontierSchedulerTests` (10) | PASS | — |
| Store: claim jednokrotny, dzierżawa urządzenia, runy niezakończone, `represented_by`, UNIQUE `attempt_id`, migracja 4 → 5 | `LeaseStoreTests` (5) | PASS | — |
| Testy STEP-05 (E2E demo, determinizm, dowody, CLI) bez zmian semantyki | `ExplorationEngineTests`, `ExploreCommandTests` | PASS | — |
| CI na PR | `ci.yml`, `demo-app.yml` (w tym `android-explore` na emulatorze z TapCue 0.1.0) | wynik w PR #9 (Checks) | PR → Checks |
| Demo na emulatorze lokalnie | — | NOT RUN: brak Android SDK i emulatora w sesji (R-09) | — |
| Kill na emulatorze (prawdziwy TapCue) | — | NOT RUN: wymaga urządzenia; zakres STEP-09 (ROADMAP) | — |

## Ograniczenia, ryzyka, otwarte pytania

- **`OutcomeUnknown` po rekoncyliacji** nie zapisuje krawędzi `unknown`, bo próba nie ma jeszcze snapshotu „przed” w bazie (jest tylko w blobach). Obsługa według klasy ryzyka (ponowna obserwacja dla `safe`/`navigational`) to STEP-09.
- **Lease przy jednym zapisującym:** z blokadą workspace wygasanie nie ma znaczenia. Tryb bez blokady (`WorkspaceLockHeld = false`) ma testy, ale dziś używają go tylko testy. Dzierżawa urządzenia działa w obrębie jednego workspace, nie między workspace'ami.
- **Szacowany koszt** zakłada brak rozbieżności nawigacji. Rozbieżność może przekroczyć `maxActions` w trakcie zadania, wtedy zadanie wraca do `Pending` (bez akcji na urządzeniu po `PolicyChecked`).
- **`representedBy`** działa tylko dla elementów oznaczonych przez normalizację jako wiersze listy (`listTemplate`). Lista bez wykrytego szablonu (np. < 3 wiersze poza kontenerem przewijanym) nie jest ograniczana. Nawigacja po nieskończonej liście kończy się, bo strony mają tę samą tożsamość (`countBucket`).
- **Kolejność zadań** zmieniła się (nowość, ryzyko). Demo na `FakeDevice` daje ten sam graf, a wynik na emulatorze jest w CI PR.
- `RunCounters.ChargedActions` (wyliczane) trafia też do `counters_json` i `run.json`; przy odczycie jest ignorowane.
- **Pytania do Roberta:**
  1. czy `OutcomeUnknown` dla akcji `safe`/`navigational` ma w STEP-09 automatycznie przechodzić do ponownej obserwacji (architektura §5.3), czy zawsze czekać na operatora;
  2. czy `maxStates`/`maxScreens` mają mieć dodatkowo limit całego workspace.

## Stan roadmapy

- STEP-00…05 i 02B: zmergowane. STEP-06: PR #9 otwarty.
- W `docs/ROADMAP.md` zaktualizowane: statusy 05 i 06, R-02 i R-11. Macierz platform bez zmian, bo etap nie wymaga nowego urządzenia; demo na emulatorze sprawdza CI PR.
- Następny: STEP-07 (Profiles & variants), po merge tego PR. STEP-09 zależy od 06 i może iść po nim.

## Warunki startu STEP-07

- PR STEP-06 **zmergowany** do `main`, CI (`CI`, `Demo app`) zielone na `main`, lokalny `main` zaktualizowany.
- Kontrakty z tego etapu, na których kolejne etapy się opierają:
  - `FrontierScheduler` (kolejność, `PriorityOf`, `EstimatedCost`);
  - lease (`TryLeaseTaskAsync`, `RenewTaskLeaseAsync`) i `device_leases`;
  - fazy próby i `ExplorationOptions.OnAttemptPhase`;
  - reguły rekoncyliacji (`RecoverAsync`, `ExplorationEngine.InterruptedError`);
  - `AttemptCommit.TaskStatus`;
  - budżety per run;
  - `ScheduledAction.RepresentedByActionKey`;
  - `tests/MapCue.EndToEnd.CrashHost` do testów kill w kolejnych etapach.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-07: Profiles & variants.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-07, rejestr ryzyk R-04, R-06 i wspólne DONE), docs/architecture.md (§5.5, §5.6, §6, §7.3, §7.4, §10), ADR-0006, docs/handovers/STEP-06.md oraz kod: `src/MapCue.Application/Exploration/` (`ExplorationEngine`, `FrontierScheduler`), `src/MapCue.Application/Graph/` (`GraphCommitter`, `ScreenIdentityResolver`), `src/MapCue.Application/Configuration/`, `src/MapCue.Domain/Graph/ExplorationContext.cs`, `tests/MapCue.EndToEnd.Tests/` i `tests/MapCue.Testing/{FakeDevice,FakeDemoApp}.cs`, a także `samples/MapCue.DemoApp/` (`expected-graph.md`, `.tapcue/setup-*.yaml`).
> Zweryfikuj, że PR STEP-06 (<https://github.com/rtnk/MapCue/pull/9>) jest zmergowany, a `main` aktualny i z zielonym CI (`CI` i `Demo app`). Jeśli nie, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-07-profiles-variants` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-07.md` w tym samym PR. Nie rozpoczynaj STEP-08 ani STEP-09 i nie merguj PR.
> Zakres (ROADMAP STEP-07): `ExplorationProfile` (YAML, `setupFlow`, `inputs` jako referencje sekretów, `capabilities`, `environment: test`), konteksty, warianty `ScreenVariant`, `ReachabilityRecipe` per kontekst, hipotezy guardów i ich potwierdzanie obserwacją, ograniczone porównanie profili (klasy równoważności), macierz pokrycia profili. Rozstrzygnij (albo zapytaj Roberta) otwarte pytanie z STEP-05: czy run ma domyślnie odmawiać zmiany profilu (`profile.investor` z kontekstu `basic`).
> Testy: dwa profile w demo app (`basic`, `investor`): różne akcje i wspólne ekrany, dowody i krawędzie bez mieszania; guard `investments` przechodzi z `inferred` do `observed` dopiero po obu profilach; sprawdź margines wariantu Home `investor` (Jaccard 0,857 przy `τ_state` = 0,85, R-04). Wyniki urządzeniowe raportuj jako PASS, FAIL albo NOT RUN, z komendami.
> DoD: dwa profile odsłaniające różne akcje nie mieszają grafu ani dowodów; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury przechodzi; ROADMAP i handoff STEP-07 zaktualizowane.
