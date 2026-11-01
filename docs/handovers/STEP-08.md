# HANDOFF: STEP-08: Screen Contract & accessibility

## Status i identyfikatory

- Status: **DONE**. Zakres ROADMAP STEP-08 zrealizowany. Kontrole lokalne, CI i demo na emulatorze: wyniki w tabeli niżej. Wynik PoC klawiatury na emulatorze: fokus jest obserwowalny, ale Tab na demo app MAUI nie przechodzi przez wszystkie cele (`unverified`; szczegóły w „Ograniczenia”). Merge wykonuje Robert.
- Branch: `claude/step-08-screen-contract-o7xzdp` (wyznaczony przez środowisko sesji zamiast `feat/step-08-screen-contract`)
- PR URL i status (open / merged / not created): <https://github.com/rtnk/MapCue/pull/12> (open, czeka na review i merge)
- Commit SHA:
  - kod `ad93fc3`;
  - demo `ed98a42`;
  - dokumentacja `2e17957`;
  - poprawka próby klawiatury `894aaf2`.

  Ten handoff jest w kolejnym commicie (tylko dokumenty), więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `6bf69ce2e118b675be782b17e3a1712fccb23e3d` (merge PR #11; PR #10 STEP-07 zmergowany jako `e65f136`; CI run 41301115329 i Demo app run 41301115370 `success`)
- Data i środowisko: 2026-11-01. Lokalnie kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.114 (`apt-get install dotnet-sdk-10.0` w sesji). Bez urządzenia, Android SDK i TapCue (R-09). Urządzenie: GitHub Actions `ubuntu-24.04`, emulator API 34 `google_apis` x86_64, TapCue 0.1.0.

## Zakres faktycznie wykonany

- **Gate:**
  - PR #10 zmergowany;
  - `main` aktualny (`6bf69ce`, z poprawką R-14 z PR #11);
  - CI i Demo app zielone;
  - ROADMAP: 07 → „zmergowany”;
  - handoff STEP-07: status PR = merged.
- **Schemat `schemas/screen-contract.v1.schema.json`** (`mapcue.screen-contract/1`, `SchemaVersions.ScreenContract`, osadzony w `MapCue.Contracts`):
  - komórki macierzy są typowane per poziom, więc komórka `inspected` nie może mieć statusu `pass`;
  - `assistiveVerified` przyjmuje tylko `pass`/`fail`/`notTested`/`unsupported`;
  - kolejność przewidywana ma zawsze `predicted`/`unverified`.

  DTO: `MapCue.Contracts.ScreenContracts.ScreenContractDocument`, `ScreenContractJson` (`contentHash`).
- **`AnalyzeScreen` (pełna hierarchia przez CLI)** w `ExplorationEngine` (`ExplorationEngine.Contracts.cs`), włączane sekcją `contracts` (domyślnie wyłączona):
  - po wyczerpaniu zadań `ExploreAction` kontekstu każdy stan (poza `loading`) dostaje zadanie;
  - kroki: dojście do stanu, `tapcue hierarchy`, kontrolna obserwacja fast, która musi rozpoznać ten sam stan; dopiero wtedy snapshot full zapisuje się jako snapshot stanu (`fidelity = Full`) z dowodami;
  - snapshot full nie przechodzi przez resolver;
  - `AnalyzeState` czyta tylko snapshoty fast.
- **`AuditAccessibility`: graf fokusu klawiatury Android (Tab, PoC):**
  - nowy typ akcji `PressKey`: tylko silnik, klucze z allowlisty `DeviceKeys` (tylko `Tab`), szablon `- pressKey: Tab` w generatorze;
  - reasoner nie może go zaproponować (`AgentPolicy.ProposableActionTypes`, enum schematu odpowiedzi);
  - do `contracts.maxTabPresses` naciśnięć, po każdym obserwacja fast i flaga `focused`;
  - każde naciśnięcie liczy się do `budgets.maxActions` (`RunCounters.KeyPresses`);
  - dokument `mapcue.focus-probe/1` jest blobem z dowodem `FocusProbe`;
  - na iOS zadanie nie powstaje (G-04).
- **Wersjonowanie kontraktu:**
  - `ScreenContractBuilder` liczy kontrakt tylko z bazy i blobów, bez zegara;
  - `contentHash` = SHA-256 zwartego JSON z `version` 0;
  - `ScreenContractPublisher` waliduje schematem, zapisuje zredagowany blob i nadaje nową wersję (`screen_contracts`) tylko przy zmianie hasha;
  - plik: `screens/<screenKey>/<grp_…>/screen-contract.v<k>.json`.
- **Warianty elementów per kontekst:**
  - kontrakt na ekran i grupę porównania (app, platforma, build, locale);
  - warianty mają `identityTokens` z `ScreenVariant.IdentityTokens` i źródło snapshotu;
  - elementy mają obecność per kontekst i stan, a `visibleWhen` to hipoteza guardu z STEP-07;
  - role na iOS mają `roleLevel: inferred` (G-03).
- **Macierz TalkBack/VoiceOver/Klawiatura z poziomami dowodu (architektura §14.3):**
  - reguły drzewa dają `inspected` (`present`/`issues`): `label.missing`, `label.duplicate`, `a11y.hidden`, `target.small`;
  - TalkBack/VoiceOver `automated` = `unsupported` (G-05);
  - klawiatura Android: `pass` tylko przy zamkniętym cyklu ze wszystkimi celami, `fail` przy cyklu bez celu, inaczej `unverified`, `blocked` albo `notTested`;
  - klawiatura iOS = `unsupported`;
  - technologia innej platformy = `unsupported`.
- **Kolejność przewidywana z drzewa** (od góry do dołu, od lewej do prawej) jako `predicted`/`unverified`, porównana z obserwowaną (`matchesPredicted`).
- **Protokół manualny:**
  - `mapcue a11y record` i `docs/a11y-manual-protocol.md` (`mapcue.a11y-protocol/1`);
  - to jedyna droga do `assistiveVerified`;
  - wymagany plik dowodu (tekst UTF-8, redagowany);
  - `pass` komórki wymaga rekordu całego ekranu i braku rekordu `fail`;
  - odrzucane: TalkBack na iOS, VoiceOver na Androidzie, klawiatura na iOS, nieznany ekran, stan albo kontekst.
- **CLI:**
  - `mapcue contract build` (linie `contract` i `a11y` z macierzą);
  - `mapcue a11y record`;
  - `mapcue status` pokazuje `captures` i `key-presses`.
- **Demo:**
  - `mapcue.demo.yaml`: `contracts` i `maxActions: 400`;
  - `run-explore.sh`: `contract build`, raport;
  - `compare-expected.py` kontrola 6: snapshot full każdego wariantu, kontrakt każdego stanu, brak `pass` przy `inspected`, VoiceOver `unsupported` na Androidzie, brak `assistiveVerified` bez rekordu; plus tabele znalezisk i obserwowanej kolejności fokusu jako diagnostyka;
  - `expected-graph.md`: sekcja kontraktów.
- **Poprawka po pierwszym runie na emulatorze (41306424435):**
  - próba uznawała dwa naciśnięcia na tym samym elemencie za zamknięty cykl i dawała fałszywy `fail` (Settings, Verify, Profile, Investments: `cycleClosed` po 2 naciśnięciach, 1 element z fokusem);
  - teraz ten sam element dwa razy z rzędu to `focusDidNotMove` (`unverified`, w `detail` element, na którym fokus utknął);
  - wyjątek: jedyny cel ekranu trzymający fokus to pełny cykl jednego elementu;
  - test regresji: `Focus_that_stops_moving_is_unverified_never_a_closed_cycle` (pułapka fokusu w `FakeDevice`).

## Pliki / API / migracje

- **Nowe:**
  - `schemas/screen-contract.v1.schema.json`;
  - `src/MapCue.Contracts/ScreenContracts/ScreenContractDocument.cs`;
  - `src/MapCue.Domain/Graph/ScreenContracts.cs` (`ScreenCapture`, `AccessibilityRecord`, `ScreenContractVersion`, enumy);
  - `src/MapCue.Application/ScreenContracts/{FocusProbe,ScreenContractBuilder,ScreenContractPublisher,AccessibilityRecorder}.cs`;
  - `src/MapCue.Application/Exploration/ExplorationEngine.Contracts.cs`;
  - `src/MapCue.Infrastructure.Sqlite/SqliteScreenContractRepository.cs`, `Migrations/0007_screen_contracts.sql`;
  - `src/MapCue.Cli/ContractCommands.cs`;
  - `docs/a11y-manual-protocol.md`;
  - testy: `ScreenContractEndToEndTests` (10), `ScreenContractSchemaTests` (8), `ScreenContractStoreTests` (3), `ContractConfigTests` (5), test CLI, generator `pressKey`, nagrania Home.
- **Zmienione:**
  - `ActionType.PressKey`, `DeviceKeys`;
  - `ValidatedDeviceAction.Key`;
  - `RawElement.ImportantForAccessibility`;
  - `UiElement.Bounds`/`ImportantForAccessibility` (bez wpływu na hashe; przypięte hashe nagrań bez zmian);
  - `TapCueParsers.ParseHierarchy`, `FlowYamlGenerator`;
  - `ArtifactKind` (`FocusProbe`, `OperatorRecord`, `ScreenContract`), `EvidenceKind` (`FocusProbe`, `OperatorRecord`);
  - `TaskKeys.AnalyzeScreen/AuditAccessibility`;
  - `RunCounters.ScreenCaptures/KeyPresses` (`ChargedActions` liczy naciśnięcia);
  - `ContractSettings` (`contracts.analyzeScreens|keyboardFocus|maxTabPresses`), dokument i walidator konfiguracji;
  - `IUnitOfWork.Contracts` (`IScreenContractRepository`);
  - `ExplorationEngine` (partial, dispatch zadań, `AnalyzeState` tylko na snapshotach fast);
  - CLI (`MapCueCli`, `StatusCommand`, snapshot pomocy);
  - `FakeDevice` (`PressKey Tab`, `focused`, `KeyboardFocusable`, `TrapsKeyboardFocus`, `ReportsKeyboardFocus`, liczniki);
  - dokumentacja: architektura §14.2/§14.3, ROADMAP, README, `tapcue-compatibility.md` (G-04, G-05, §12), `expected-graph.md`, przykład konfiguracji, handoff STEP-07.
- **Migracja `0007`** (przyrostowo od 6, testowana): `screen_captures`, `screen_contracts` (`UNIQUE(screen_id, group_key, version)`), `a11y_records`.
- **Zmiany łamiące** (bez zewnętrznych konsumentów):
  - nowa właściwość portu `IUnitOfWork.Contracts`;
  - `RunCounters.ChargedActions` obejmuje naciśnięcia klawiszy;
  - linia `counters` w `mapcue status` ma dwa nowe pola.

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #10 zmergowany, `main` aktualny, CI i Demo app zielone | GitHub API: PR #10 merged; `main` = `6bf69ce`; runy 41301115329 (CI), 41301115370 (Demo app) `success` | PASS | — |
| Baseline przed zmianami | `dotnet build MapCue.sln -c Release && dotnet test MapCue.sln -c Release --no-build` na `6bf69ce` / lokalnie | PASS (0 ostrzeżeń; 751 pass, 1 skip) | — |
| Build Release (0 ostrzeżeń) | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.114 | PASS | — |
| Testy | `dotnet test MapCue.sln -c Release --no-build` / lokalnie | PASS (781 pass, 1 skip: smoke CLI agenta bez zmiennej) | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` | PASS | — |
| Test architektury | `MapCue.Architecture.Tests` (17) | PASS | — |
| Kontrakt per ekran demo app, oba profile (`FakeDemoProfiles`): 8 kontraktów, warianty per kontekst, `tab.investments` z `visibleWhen` `observed`/`investments`, 15 snapshotów full = 15 wariantów, schemat OK | `ScreenContractEndToEndTests.Both_profiles_give_one_contract_per_screen_…` | PASS | — |
| Raport nigdy nie daje PASS przy `inspected` (wszystkie kontrakty, schemat odrzuca `pass` w komórce `inspected`, `verified`, inny poziom) | `The_matrix_never_passes_from_the_tree_…`, `ScreenContractSchemaTests.A_cell_status_outside_its_level_is_rejected` (4) | PASS | — |
| Kontrola mutacją: `present` → `pass` w komórce `inspected` (kod) | testy E2E i CLI | wykryte (5 FAIL), kod przywrócony | — |
| Kontrola mutacją: `pass` przy `inspected` w pliku kontraktu | `compare-expected.py` na workspace z `FakeDevice` | wykryte (exit 1) | — |
| iOS: TalkBack i klawiatura `unsupported` we wszystkich komórkach, VoiceOver `automated` `unsupported`, `assistiveVerified` `notTested`, role `inferred`, brak zadań `AuditAccessibility`, rekord klawiatury odrzucony | `On_iOS_the_keyboard_and_screen_reader_automation_cells_are_unsupported` | PASS | — |
| Klawiatura: cel nieosiągalny → `fail` + `keyboard.unreachable`; brak fokusu → `unverified` (3 naciśnięcia); fokus stoi w miejscu → `unverified`, nigdy cykl; pojedynczy cel → cykl | `An_element_the_keyboard_never_reaches_…`, `Without_an_observable_focus_…`, `Focus_that_stops_moving_…` | PASS | — |
| Wersjonowanie (`contentHash` stały przy tym samym stanie bazy, nowa wersja tylko przy zmianie, rekord operatora → v2 tylko tego ekranu, `fail` wygrywa) i odrzucanie rekordów (7 przypadków, redakcja sekretu w dowodzie) | `Contract_versions_change_only_…`, `Manual_records_that_cannot_be_true_are_refused` | PASS | — |
| Przechwytywanie nie zmienia grafu; budżet akcji liczy naciśnięcia (`BudgetExhausted`, potem kolejny run kończy przechwycenia) | `Captures_never_change_the_explored_graph`, `Key_presses_are_charged_to_the_action_budget` | PASS | — |
| CLI: YAML → `explore` z przechwytywaniem → `contract build` (v1, potem bez zmian) → `a11y record` → v2 z `assistiveVerified=fail`; odrzucenia; brak workspace | `ExploreCommandTests.Contracts_from_yaml_…` | PASS | — |
| Migracja 6 → 7, repozytorium | `ScreenContractStoreTests` (3) | PASS | — |
| Nagrania Home z emulatora: `important-for-accessibility` i bounds, cele, kolejność przewidywana | `DemoAppRecordingCalibrationTests.The_full_hierarchy_carries_…` | PASS | — |
| Wyrocznia na workspace z `FakeDevice` (oba profile, kontrola 6) | tymczasowy test zapisujący workspace + `python3 -I samples/MapCue.DemoApp/compare-expected.py <ws> <report>` | PASS (8 kontraktów, klawiatura `pass` na modelu) | — |
| Spójność demo app | `python3 -I samples/MapCue.DemoApp/check-consistency.py` | PASS | — |
| CI na PR (head `2e17957`) | `ci.yml` run 41306424422, `tapcue-smoke.yml` run 41306424459, `demo-app.yml` run 41306424435 | PASS (wszystkie joby) | PR #12 → Checks |
| **Demo na emulatorze, run 1 (head `2e17957`)** | `demo-app.yml` job `android-explore`, run 41306424435: `run-explore.sh` → `explore --profile basic`, `--profile investor`, `status --profiles`, `contract build`, `compare-expected.py` | **PASS** (hard checks); `basic` 346 s (`Exhausted`, 25/25 przejść), `investor` 376 s (`Exhausted`, 33/33); 15/15 snapshotów full `Captured`, 15/15 prób klawiatury `Captured`; 8 kontraktów v1; TalkBack wszędzie `issues`/`unsupported`/`notTested`; VoiceOver `unsupported`; klawiatura: 4 ekrany `unverified`, 4 ekrany `fail` (**fałszywy fail**, błąd próby, poprawiony w `894aaf2`) | artefakt `demo-android-explore` (ID 13084385388) |
| **Demo na emulatorze, run 2 (head `894aaf2`)** | jak wyżej | RUN2_RESULT | RUN2_ARTIFACT |
| Demo na emulatorze lokalnie, artefakt workspace | — | NOT RUN: brak Android SDK i emulatora w sesji; pobranie artefaktu z Azure Blob odmówione przez proxy sesji (R-09), więc szczegóły pochodzą z logu joba | — |
| iOS na urządzeniu lub symulatorze | — | NOT RUN: brak runnera macOS z odpowiednim Xcode (R-01, R-10); komórki iOS sprawdzone na `FakeDevice` | — |

## Ograniczenia, ryzyka, otwarte pytania

- **PoC klawiatury (architektura §14.2) na emulatorze:**
  - **Wynik:** flaga `focused` zmienia się po `pressKey Tab`, więc fokus klawiatury jest obserwowalny przez TapCue 0.1.0. Odwiedzonych było 1–6 elementów na ekran.
  - Tab na demo app MAUI nie przechodzi jednak przez wszystkie cele. Fokus zatrzymuje się po 1–6 elementach (`focusDidNotMove`), a część elementów z fokusem to nie cele z drzewa (np. kontenery).
  - Wynik `automated` dla klawiatury to więc `unverified`, a nie werdykt.
  - Do ustalenia przez człowieka protokołem manualnym, czy to realna pułapka fokusu w aplikacji (MAUI Shell / `BottomNavigationView`), czy artefakt obserwacji: TapCue może raportować `focused` z opóźnieniem albo na rodzicu.
- **Reguły drzewa na emulatorze dają dużo znalezisk** (np. Settings 12 na kontekst). Lista per reguła jest w raporcie runu 2 (tabela znalezisk). Prawdopodobnie dominuje `target.small`: emulator API 34 o niskiej rozdzielczości, a próg 48 px przy nieznanej gęstości jest heurystyką. Znaleziska są „do sprawdzenia” (`inspected`), nie werdyktem. Kalibracja progu (gęstość z `adb shell wm density` albo z TapCue) to pytanie na kolejny etap.
- **Reguły drzewa dla TalkBack to poziom `inspected`, nie `automated`** (odstępstwo od tabeli §14.2, opisane w §14.3): drzewo nie mówi, co ogłosi czytnik, a DoD wymaga braku PASS z drzewa.
- **Snapshoty kroków próby klawiatury** (bloby surowe i znormalizowane) są wskazane tylko z dokumentu próby, nie z tabeli `evidence`. Przyszłe `mapcue gc` musi je traktować jako osiągalne przez dokument `FocusProbe`.
- **Przechwytywanie wydłuża demo:** `basic` 211 s → 346 s, `investor` 227 s → 376 s. Każde naciśnięcie Tab to jedno `run` MCP.
- **Rekordy operatora** są idempotentne: ten sam ekran, kontekst, technologia, element, właściwość, wynik i dowód dają ten sam identyfikator. Nie ma komendy do wycofania rekordu (P1).
- **Pytania do Roberta:**
  1. czy `target.small` ma pozostać w regułach przy nieznanej gęstości, czy wymagać gęstości urządzenia (wtedy `notTested` bez niej);
  2. czy próbę klawiatury rozszerzyć o Shift+Tab i strzałki (D-pad), skoro sam Tab nie przechodzi przez MAUI Shell;
  3. otwarte pytania STEP-07 (`--all-profiles`, `profileVariantMaxDelta`) i STEP-06 pozostają.

## Stan roadmapy

- STEP-00…07 i 02B: zmergowane. STEP-08: PR #12 otwarty.
- W `docs/ROADMAP.md` zaktualizowane:
  - statusy 07 i 08;
  - R-07 (realizacja: `unsupported`, `inspected` bez PASS, protokół manualny, PoC klawiatury);
  - macierz platform: kolumna A11y dla Android emulator.
- Następny: STEP-09 (Recovery & operator handoff, zależy od 06, a od 07 dla wariantów). Start po merge tego PR.

## Warunki startu STEP-09

- PR STEP-08 **zmergowany** do `main`, CI (`CI`, `Demo app`) zielone na `main`, lokalny `main` zaktualizowany.
- Kontrakty z tego etapu, na których kolejne etapy się opierają:
  - `IScreenContractRepository` (przechwycenia, wersje, rekordy);
  - `ScreenContractBuilder`/`Publisher` (raporty STEP-10 mogą czytać kontrakty);
  - `ActionType.PressKey` (tylko silnik);
  - `ContractSettings`;
  - `TaskType.AnalyzeScreen/AuditAccessibility` w frontierze (STEP-09: przechwycenie, którego nie dało się wykonać, ma status `Failed`, nie `Blocked`, i nie wymaga `resume`).

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-09: Recovery & operator handoff.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-09, rejestr ryzyk R-02, R-03, R-06, R-14 i wspólne DONE), docs/architecture.md (§5.3, §5.6, §5.7, §6, §13, §14.3), docs/tapcue-compatibility.md, ADR-0001, ADR-0002, ADR-0004, docs/handovers/STEP-08.md oraz kod: `src/MapCue.Application/Exploration/` (`ExplorationEngine`, `RecipeBuilder`, `FrontierScheduler`), `src/MapCue.Application/Ports/IStateStore.cs`, `src/MapCue.Cli/` (zaślepki `blocked`, `resume`), `tests/MapCue.EndToEnd.Tests/` (`RecoveryTests`, `ExplorationHarness`), `tests/MapCue.Testing/FakeDemoApp.cs`, a także `samples/MapCue.DemoApp/` (`expected-graph.md`, sekcja „Blokada”).
> Zweryfikuj, że PR STEP-08 (<https://github.com/rtnk/MapCue/pull/12>) jest zmergowany, a `main` aktualny i z zielonym CI (`CI` i `Demo app`). Jeśli nie, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-09-recovery` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-09.md` w tym samym PR. Nie rozpoczynaj STEP-10 i nie merguj PR.
> Zakres (ROADMAP STEP-09): `Checkpoint`, deterministyczny `resume.md`, `mapcue blocked`, `mapcue resume <task> [--approve <category>]`, bezpieczny replay `ReachabilityRecipe` z wykrywaniem rozbieżności (`verifiedAt`), obsługa `OutcomeUnknown` według klasy ryzyka (otwarte pytania STEP-06), instrukcja operatora `docs/operator-guide.md`.
> Testy: kill i restart w środku eksploracji na FakeDevice i na emulatorze; nieznany popup w demo app (Verify) daje `Blocked` z kompletnym `resume.md`; wznowienie bez danych z poprzedniej sesji LLM.
> DoD: kill/restart w środku eksploracji, brak zależności od poprzedniej sesji LLM; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury przechodzi; ROADMAP i handoff STEP-09 zaktualizowane.
