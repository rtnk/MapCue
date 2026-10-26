# MapCue — ROADMAP

Zasada pracy: **jeden etap = jeden świeży agent = jeden branch = jeden PR**. Kolejny etap zaczyna się dopiero po merge poprzedniego PR do `main` (protokół w `AGENTS.md`). Statusy aktualizuje każdy PR etapu.

Dokumenty: `docs/architecture.md`, `docs/tapcue-compatibility.md`, `docs/adr/`, handoffy w `docs/handovers/STEP-NN.md`.

## Statusy etapów

| Etap | Nazwa | Priorytet | Zależy od | Status |
|---|---|---|---|---|
| 00 | Discovery & architecture | P0 | — | zmergowany (PR #2) |
| 01 | Solution foundation | P0 | 00 | zmergowany (PR #3) |
| 02 | TapCue adapter | P0 | 01 | zmergowany (PR #4) |
| 02B | Demo/fixture app (MAUI) | P0 | 01 (równolegle z 03/04 po 02) | zmergowany (PR #7) |
| 03 | Graph & persistence | P0 | 01 | zmergowany (PR #5) |
| 04 | Agent interpretation | P0 | 01, 03 | zmergowany (PR #6) |
| 05 | Vertical slice crawler | P0 (MVP) | 02, 02B, 03, 04 | nie rozpoczęty |
| 06 | Frontier scheduler | P0 (MVP) | 05 | nie rozpoczęty |
| 07 | Profiles & variants | P0 (MVP) | 06 | nie rozpoczęty |
| 08 | Screen Contract & accessibility | kontrakt P0, a11y P1 | 07 | nie rozpoczęty |
| 09 | Recovery & operator handoff | P0 (MVP) | 06 (07 dla wariantów) | nie rozpoczęty |
| 10 | Reports & TapCue export | P0 (MVP: coverage + eksport), diff buildów P1 | 09 | nie rozpoczęty |
| 11 | Optional HTTP fixtures (DevFlow) | opcjonalne | 10 | nie rozpoczęty |
| 12 | iOS, hardening & v0.1 | P1 | 10 (+11 jeśli wykonany) | nie rozpoczęty |

**MVP** = 00–07, 09, 10 (bez diffu buildów) + 02B. Docelowy scenariusz akceptacyjny produktu (prompt startowy §2) jest spełniony po STEP-10. Weryfikacja iOS w MVP ogranicza się do smoke w STEP-02 i adaptera z jawnymi `unsupported`.

### Zmiany względem planu bazowego (uzasadnienie)

1. **Nowy STEP-02B: aplikacja kontrolna.** Scenariusz akceptacyjny wymaga ≥ 5 stanów, cyklu, modala, wariantu profilu i celowej blokady, a w żadnym etapie bazowym nie ma aplikacji, która to zapewnia. Systemowe Settings wystarczą tylko do smoke. Wpływ: STEP-05 zależy od 02B. 02B można robić po 01 równolegle z 03/04, bo nie dotyka kodu MapCue. **Decyzja Roberta (2026-10-26): zatwierdzone**, wariant domyślny (aplikacja MAUI w tym repo, `samples/MapCue.DemoApp`, poza `MapCue.sln`). Odrzucone alternatywy: osobne repo, natywna aplikacja Android.
2. **Wczesny iOS smoke w STEP-02**, a nie dopiero w STEP-12 (wymóg promptu, ryzyko R-01).
3. **Model elementów i wariantów (`elementKey`, `ScreenVariant`) w STEP-03**, a artefakty ScreenContract w STEP-08. Transkrypt traktuje Structure i Variant Analyzer jako P0 (bez tego trzeba przebudować graf).
4. **`RuleBasedReasoner` w STEP-04**, żeby STEP-05…07 nie zależały od dostępności LLM i kosztów.
5. **`FakeDevice` (symulowany model aplikacji) w STEP-01/03**, żeby logika cykli, profili i restartu była testowana w CI bez urządzenia.

Wymagania TOOL-009 nie zostały usunięte. CAP-0116 (analiza kodu) jest poza roadmapą 00–12 jako P2 (zgodnie z transkryptem).

## Definicja „DONE” wspólna dla wszystkich etapów

- Zakres etapu zrealizowany. Odstępstwa opisane w handoffie, a istotne mają ADR albo zgodę Roberta.
- `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` (od STEP-01) zielone lokalnie i w CI.
- Kontrole wymagające urządzenia: wynik PASS, FAIL albo NOT RUN z przyczyną i komendą do odtworzenia. Bez fabrykowania wyników.
- Brak sekretów w repo i artefaktach. Nowe kontrakty mają `schemaVersion` i testy.
- `docs/handovers/STEP-NN.md` w tym samym PR (format w `AGENTS.md`) i aktualizacja tabeli statusów w tym pliku.

## Etapy

### STEP-00: Discovery & architecture

- **Wejścia:** TOOL-009, CAP-0104…0116, TOOL-008, DEC-010/013/015, transkrypt, kod TapCue `v0.1.0`.
- **Deliverables:** ten plik, `docs/architecture.md`, `docs/tapcue-compatibility.md`, `docs/sources.md`, `docs/adr/ADR-0001…0006`, `docs/spikes/tapcue-smoke/`, `AGENTS.md`, `README.md`, `docs/handovers/STEP-00.md`.
- **Testy:** smoke TapCue bez urządzenia (PASS), kontrola linków i Mermaid w dokumentach.
- **DONE:** PR-00 otwarty z kompletem dokumentów. Merge wykonuje Robert.
- **Ryzyka:** brak urządzeń i macOS w sesji (smoke na urządzeniu: NOT RUN).

### STEP-01: Solution foundation

- **Wejścia:** ADR-0001, architektura §3.1, §4, §10.3, §16.
- **Deliverables:** `MapCue.sln` (`net10.0`), projekty z §3.1 (puste szkielety portów), `Directory.Build.props` (nullable, warnings as errors, analyzers), `Directory.Packages.props` (central package management), `global.json` (przypięte SDK 10), host CLI (System.CommandLine) z komendami-szkieletami `explore|status|blocked|resume|report|export|diff|doctor` (zwracają „not implemented” z kodem wyjścia ≠ 0, z wyjątkiem `--help`/`--version`), konfiguracja YAML/JSON z walidacją i bezpiecznymi domyślnymi budżetami, logging strukturalny z redaktorem, `IClock`/`IIdGenerator`, fake porty (`FakeDevice` minimalny, `DeterministicFakeReasoner` szkielet), testy (xUnit lub uzasadniony odpowiednik), `.github/workflows/ci.yml` (ubuntu: restore, build, test, format; akcje przypięte do SHA, `permissions: contents: read`), `.gitignore` (`.mapcue/`, artefakty), `.editorconfig`.
- **Testy:** walidacja konfiguracji (brak allowlisty `appIds`/`environments` oznacza błąd), redaktor (sekret nie trafia do logu), `mapcue --help` (snapshot test), reguła zależności projektów (test architektury).
- **DONE:** CI zielone. `dotnet run --project src/MapCue.Cli -- --help` działa bez urządzenia, TapCue i LLM.
- **Ryzyka:** dostępność .NET 10 SDK w środowisku agenta (instalacja przez `dotnet-install` może być blokowana przez sieć; wtedy NOT RUN lokalnie i weryfikacja przez CI).
- **Minimalny PR:** szkielet + CI + konfiguracja + redakcja. Bez logiki domenowej grafu.

### STEP-02: TapCue adapter

- **Wejścia:** ADR-0002, `tapcue-compatibility.md`, `docs/spikes/tapcue-smoke/`.
- **Deliverables:** `MapCue.Adapters.TapCue`: klient MCP STDIO (JSON-RPC line-delimited, handshake, `tools/list` z weryfikacją wymaganych narzędzi), `TapCueCliRunner` (`--version`, `check-syntax`, `hierarchy --no-reinstall-driver`, `test --test-output-dir`), generator jednoakcyjnego YAML z typowanej akcji (szablony, escapowanie, zakazane komendy), twarde timeouty z zabiciem drzewa procesów, restart sesji, `OutcomeUnknown`, czyste środowisko procesu, redakcja `env_vars`, klasyfikacja błędów, preflight wersji, `mapcue doctor`. Fixtures JSONL nagranych odpowiedzi.
- **Testy:** kontraktowe na fixtures (bez urządzenia, CI). Integracyjne z fałszywym serwerem MCP (proces testowy) dla timeoutu, zawieszenia i zerwanego STDIO. **Smoke Android** (emulator w CI na ubuntu z KVM albo lokalnie): `list_devices` → `launchApp` Settings → `inspect_screen` → `back` → `inspect_screen` → `hierarchy` → `test --test-output-dir`. **Smoke iOS (najtańszy):** job GitHub Actions `macos-26` (ręczny `workflow_dispatch`): archiwum TapCue + SHA, `driver-setup --simulator`, boot symulatora, ten sam scenariusz na `com.apple.Preferences`. Pomiar czasów (ADR-0002 PoC) i sprawdzenie kolizji MCP i CLI na jednym urządzeniu (R-03).
- **DONE:** realny odczyt ekranu i bezpieczna akcja na ≥ 1 środowisku (Android) z zapisanym dowodem. iOS: PASS albo BLOCKED z kryterium odblokowania (R-01). Brak ścieżek na sztywno.
- **Ryzyka:** R-01, R-02, R-03, R-08, R-12.
- **Minimalny PR:** adapter + fixtures + smoke Android. Smoke iOS może być osobnym jobem `workflow_dispatch` w tym samym PR.

### STEP-02B: Demo/fixture app (MAUI)

- **Wejścia:** prompt §2 (scenariusz akceptacyjny), transkrypt §10.
- **Deliverables:** `samples/MapCue.DemoApp` (`net10.0-android`, `net10.0-ios`) z ≥ 6 ekranami: wybór profilu testowego (bez backendu, deterministyczne dane), Home z zakładkami, lista rachunków (pusta lub z danymi zależnie od scenariusza), szczegóły rachunku, Ustawienia; modal; stan ładowania (deterministyczne opóźnienie); cykle Home↔Accounts i Details→Home; funkcja „Investments” tylko dla profilu `investor`; przycisk „Delete” (do testu polityki); celowy nieznany popup (blokada); `AutomationId` na kluczowych elementach. Flow YAML TapCue `samples/MapCue.DemoApp/.tapcue/` (setup profili). Dokument mapy oczekiwanej (`expected-graph.md`) jako wyrocznia testów.
- **Testy:** build Android w CI (workload MAUI), smoke flow TapCue na emulatorze (setup obu profili).
- **DONE:** APK budowany w CI, flow setup przechodzi na emulatorze (albo NOT RUN z przyczyną). iOS: build na macOS (job ręczny) albo NOT RUN.
- **Ryzyka:** R-10 (workloady MAUI, koszt macOS).

### STEP-03: Graph & persistence

- **Wejścia:** ADR-0003, ADR-0006, architektura §6–§7, §12.
- **Deliverables:** typy domenowe (`LogicalScreen`, `ScreenState`, `ScreenVariant`, `ScreenEdge`, `UiSnapshot`, `UiElement`, `ActionCandidate`, `ActionObservation`, `Evidence`, `FrontierTask`, `TaskAttempt`, `Checkpoint`, `ReachabilityRecipe`, `BlockedReason`), normalizacja snapshotu i `identityHash`/`layoutHash` z `fingerprintVersion`, resolver tożsamości, SQLite + migracje, `IStateStore` z transakcją commitu próby, content-addressed `IArtifactStore`, outbox `events`. Import snapshotów z plików (`mapcue graph import`, narzędzie testowe).
- **Testy:** property-based stabilności fingerprintu (permutacja, cyfry, długość listy), cykl A→B→A, dwa profile bez mieszania wariantów i dowodów, idempotentny import (dwukrotny import = ten sam graf), migracje od zera i przyrostowo, atomowość (wyjątek w środku transakcji nie zostawia częściowego stanu).
- **DONE:** powtarzany import stanów daje stabilny graf. Cykle i profile testowane bez urządzenia.
- **Ryzyka:** R-04 (kalibracja progów na danych z 02/02B).

### STEP-04: Agent interpretation

- **Wejścia:** ADR-0004, architektura §9.
- **Deliverables:** `schemas/agent-exploration-request.v1.schema.json`, `…-response.v1.schema.json`, generator żądania (normalizacja i redakcja snapshotu, `graphHints`), walidator (schemat → stale → semantyka → ryzyko → `ValidatedDeviceAction`), klasyfikator ryzyka regułowy (wielojęzyczne słowa kluczowe, reguły operatora), `PolicyGate`, `DeterministicFakeReasoner`, `RuleBasedReasoner`, `CliAgentReasoner` (kontener domyślnie, `local` tylko jawnie), budżety tokenów i kosztu, zapis `AgentCall`.
- **Testy:** złote JSON (poprawne i błędne: dodatkowe pola, zły `elementRef`, inny `snapshotHash`, `inputText` z wartością literalną, YAML w polu tekstowym, klasa `destructive`), akcja spoza allowlisty zawsze odrzucona, naprawa formatu najwyżej 1 raz, test izolacji (brak fallbacku na host). Smoke `CliAgentReasoner` z prawdziwym CLI: PASS albo NOT RUN.
- **DONE:** agent nie może wykonać akcji spoza allowlisty; malformed i stale JSON jest odrzucany; testy kontraktowe zielone.
- **Ryzyka:** R-05, R-06; zmiany flag CLI providerów.

### STEP-05: Vertical slice crawler

- **Wejścia:** 02, 02B, 03, 04.
- **Deliverables:** `ExplorationEngine` (run: preflight → pętla → zakończenie), pętla `AnalyzeState` + `ExploreAction` (architektura §5.1), stabilizacja UI, prosty frontier w SQLite (bez lease i odzysku, które są w 06), `mapcue explore --config`, `mapcue status`. Artefakty prób.
- **Testy:** E2E na `FakeDevice` (≥ 5 stanów, cykl, modal), deterministyczność (dwa runy z tym samym ziarnem dają ten sam graf), **demo na emulatorze z TapCue**: ≥ 3 stany odkryte z Fake albo RuleBased reasonerem (real LLM opcjonalnie).
- **DONE:** demo odkrywa ≥ 3 stany z realistycznym przebiegiem TapCue. Każda krawędź ma dowody before/after.
- **Ryzyka:** R-03, R-04, czas na akcję.

### STEP-06: Frontier scheduler

- **Deliverables:** lease i claim zadań, dzierżawa urządzenia, priorytety (lokalność, nowość, głębokość, ryzyko), budżety runu, idempotentne próby (UNIQUE `attemptId`), fazy próby i rekoncyliacja po restarcie (architektura §5.3), deterministyczne kryteria zakończenia, `representedBy` dla wierszy list.
- **Testy:** kill procesu w każdej fazie próby (test integracyjny z procesem potomnym), brak podwójnego zatwierdzenia krawędzi, terminacja na grafie z cyklem i z nieskończoną listą, budżet zatrzymuje się w przewidywalnym punkcie.
- **DONE:** wznowienie po awarii bez utraty postępu, brak podwójnych commitów, terminacja na grafie z cyklem.

### STEP-07: Profiles & variants

- **Deliverables:** `ExplorationProfile` (YAML, `setupFlow`, `inputs` jako referencje sekretów, `capabilities`, `environment: test`), konteksty, warianty `ScreenVariant`, `ReachabilityRecipe` per kontekst, hipotezy guardów i ich potwierdzanie obserwacją, ograniczone porównanie profili (klasy równoważności), macierz pokrycia profili.
- **Testy:** dwa profile w demo app (`basic`, `investor`): różne akcje i wspólne ekrany, dowody i krawędzie bez mieszania. Guard `investments` przechodzi z `inferred` do `observed` dopiero po obu profilach.
- **DONE:** dwa profile odsłaniające różne akcje nie mieszają grafu ani dowodów.

### STEP-08: Screen Contract & accessibility

- **Deliverables:** `schemas/screen-contract.v1.schema.json`, `AnalyzeScreen` (pełna hierarchia przez CLI), wersjonowanie kontraktu (`contentHash`), warianty elementów, `AuditAccessibility`: macierz TalkBack/VoiceOver/Keyboard z poziomami dowodu (architektura §14), graf fokusu klawiatury Android (Tab, **PoC**), przewidywana kolejność z drzewa jako `predicted/unverified`, protokół manualny operatora (`mapcue a11y record`) jako jedyna droga do `assistiveVerified` dla czytników.
- **Testy:** kontrakt per ekran demo app; test, że raport nigdy nie daje PASS przy poziomie `inspected`; Android keyboard graph na emulatorze (PASS albo NOT RUN); iOS: `unsupported` w odpowiednich komórkach.
- **DONE:** kontrakt per ekran, test na platformie możliwej do uruchomienia, `unsupported`/`unverified` zamiast fałszywego PASS.
- **Ryzyka:** R-07, G-03, G-04, G-05.

### STEP-09: Recovery & operator handoff

- **Deliverables:** `Checkpoint`, deterministyczny `resume.md`, `mapcue blocked`, `mapcue resume <task> [--approve <category>]`, bezpieczny replay `ReachabilityRecipe` z wykrywaniem rozbieżności, obsługa `OutcomeUnknown` według klasy ryzyka, instrukcja operatora (`docs/operator-guide.md`).
- **Testy:** kill i restart w środku eksploracji na FakeDevice i na emulatorze; nieznany popup w demo app daje `Blocked` z kompletnym `resume.md`; wznowienie bez żadnych danych z poprzedniej sesji LLM (nowy proces, nowy reasoner).
- **DONE:** kill/restart w środku eksploracji, brak zależności od poprzedniej sesji LLM.

### STEP-10: Reports & TapCue export

- **Deliverables:** `coverage.json/md` (rozróżnienie: odkryte, zaobserwowane, wykonane, zweryfikowane, zablokowane, nieznane, pominięte przez politykę; per profil; udział słabych kluczy), `graph.mmd`, `VerifyEdge` (replay i postcondition), eksport tylko zweryfikowanych ścieżek do `exports/flows/*.yaml` + `check-syntax` + replay przez `tapcue test`, `mapcue diff --from --to` (P1).
- **Testy:** powtarzalność raportów (ten sam stan bazy daje identyczne pliki), eksportowany flow przechodzi `tapcue test` na emulatorze, testy diff na syntetycznych grafach.
- **DONE:** powtarzalne raporty, działające wyeksportowane flow, testy diff. **Scenariusz akceptacyjny produktu** sprawdzony end-to-end na demo app (Android) i opisany w handoffie.

### STEP-11: Optional HTTP fixtures (DevFlow) [opcjonalny]

- **Deliverables:** `IHttpCaptureSource` dla MAUI DevFlow (wersja i API potwierdzone w etapie, bo DevFlow jest eksperymentalny), zapis per próba z `captureStatus`, redakcja, kandydaci na mocki i walidacja, provenance, jawny brak WSS.
- **DONE:** bez DevFlow produkt działa; dane wrażliwe są redagowane; mocki są walidowane; brak obietnicy WSS.

### STEP-12: iOS, hardening & v0.1 [P1]

- **Deliverables:** macierz platform z prawdziwymi wynikami (Android emulator, iOS Simulator; fizyczne: NOT RUN albo wynik), instrukcja hosta i brokera (opis, bez implementacji platformy TOOL-007), fault injection (zawieszony TapCue, zerwane STDIO, brak miejsca na dysku, zabity agent), tryb offline (RuleBased), pakowanie (`dotnet publish` single-file per RID), Release v0.1 (jeśli uprawnienia i CI pozwalają), `docs/user-guide.md`.
- **DONE:** macierz platform z prawdziwymi wynikami, udokumentowane ograniczenia, wydanie v0.1 albo gotowy artefakt z instrukcją.

## Rejestr ryzyk

| ID | Ryzyko | Prawd. / wpływ | Mitigacja | Etap |
|---|---|---|---|---|
| R-01 | iOS przez TapCue nieprzetestowany dla flow (CI TapCue czyta tylko hierarchię) | wysokie / wysoki | smoke iOS w STEP-02 na `macos-26`. **Kryterium odblokowania:** dostępny runner macOS arm64 z Xcode (GitHub Actions albo host Roberta) + archiwum TapCue 0.1.0; PASS dla `inspect_screen` i jednej akcji na `com.apple.Preferences`. | 02 |
| R-02 | brak timeoutu i anulowania w MCP (G-01) | pewne / średni | timeout + kill + `OutcomeUnknown` | 02, 06 |
| R-03 | kolizja sesji MCP i CLI `hierarchy` na jednym urządzeniu | **zmierzone w 02:** `hierarchy` bez kolizji, `test` zrywa driver MCP / średni | adapter zamyka sesję MCP przed `test`, `DriverLost` restartuje sesję; tryb `exclusive` dostępny | 02, 08 |
| R-04 | niestabilny fingerprint (MAUI, dane dynamiczne, brak `resourceId`) | średnie / wysoki | property tests (STEP-03: generowane ekrany i nagrania Androida), `fingerprintVersion` (= 1, przypięte hashe nagrań), metryka słabych kluczy (`WeakKeyCount`); `screenHints` i kalibracja progów na danych 02B w STEP-05 | 03, 05 |
| R-05 | jakość i koszt LLM | średnie / średni | RuleBased baseline, budżety, analiza raz na stan. **STEP-04:** `RuleBasedReasoner` (offline), budżety `maxCalls/maxTokens/maxCostUsd` sprawdzane przed każdym wywołaniem (brak raportu tokenów = estymata), jedna naprawa formatu; smoke `claude -p` lokalnie: 1 wywołanie, ~6,8 tys. tokenów, ~0,04 USD na ekran | 04 |
| R-06 | błędna klasyfikacja akcji niszczącej | niskie / krytyczny | deny-by-default, `max(agent, reguły)`, tylko profile testowe, zgody per kategoria. **STEP-04:** słowa kluczowe EN/PL/DE/FR/ES/IT/PT (+ słowa z `resource-id`), reguły operatora tylko podnoszą ryzyko, `stateChanging`/`unknown` tylko przy `disposable`, klasy wysokiego ryzyka wymagają zgody kategorii i `disposable`, typ akcji spoza allowlisty zawsze `Rejected`; zgody operatora (`resume --approve`) w STEP-09 | 04, 07 |
| R-07 | brak automatyzacji czytników ekranu (G-05) | pewne / średni | `unsupported` + protokół manualny | 08 |
| R-08 | Linux nieoficjalny dla TapCue | średnie / niski | macOS arm64 jako referencja; Android na Linuksie best effort | 02, 12 |
| R-09 | polityka sieci środowiska agenta blokuje narzędzia (np. `dl.google.com`) | wystąpiło / średni | archiwa Release, CI GitHub, jawne NOT RUN | każdy |
| R-10 | workloady MAUI i koszt macOS w CI | średnie / średni | Android build na ubuntu, iOS job ręczny. **STEP-02B:** aplikacja poza `MapCue.sln` (własne `samples/Directory.Build.props`/`Directory.Packages.props`), więc build MapCue nie wymaga workloadów; `demo-app.yml` instaluje `maui-android` na ubuntu (APK + flow na emulatorze tylko przy zmianach w `samples/**`), `maui-ios` tylko w ręcznym jobie `ios-build` na `macos-26` (2026-10-26: FAIL, obraz ma Xcode 26.6, workload wymaga Xcode 27.1; kryterium odblokowania: runner z Xcode 27.1 albo przypięta wersja workloadu zgodna z Xcode runnera). Lokalnie w sesji agenta workload i Android SDK są niedostępne (R-09) | 02B, 12 |
| R-11 | SQLite: jeden zapisujący | pewne / niski (MVP) | jedna instancja na workspace (STEP-03: blokada `.mapcue/lock`, serializowane transakcje `BEGIN IMMEDIATE`); port pod przyszły store | 03, 06 |
| R-12 | zmiany TapCue między wersjami | średnie / średni | przypięcie wersji, fixtures per wersja | 02 |
| R-13 | wyciek sekretów przez echo `env_vars` (G-06) | pewne bez mitigacji / wysoki | czyste env + redakcja wyniku + test | 02 |

## Macierz platform (aktualizowana w każdym etapie z urządzeniem)

| Platforma | Snapshot | Akcja | Replay/eksport | A11y | Stan |
|---|---|---|---|---|---|
| Android emulator (Linux host) | PASS (MCP i CLI) | PASS (`launchApp`, `back`; STEP-02B: `tapOn` po `id`, `back`, `assert*` na demo app) | PASS (`test --test-output-dir`, bez eksportu; STEP-02B: 5 flow demo app) | NOT RUN | STEP-02: API 34 w GitHub Actions z KVM (`tapcue-compatibility.md` §9). STEP-02B: APK demo app (MAUI, Release) zbudowany na ubuntu, flow setupu obu profili i trasy `expected-graph.md` PASS (run 40335436029) |
| Android emulator (macOS host) | NOT RUN | NOT RUN | NOT RUN | NOT RUN | — |
| iOS Simulator (macOS arm64) | NOT RUN | NOT RUN | NOT RUN | NOT RUN | R-01: job `ios` gotowy (`workflow_dispatch`), nie uruchomiony. STEP-02B: build demo app `net10.0-ios` (job `ios-build` w `demo-app.yml`, `workflow_dispatch`) **FAIL** (2026-10-26, runy 40354238048, 40354509027, 40354956988): `macos-26` ma tylko Xcode 26.6, a pakiety .NET for iOS wymagają dokładnie Xcode 27.1 (27.1.10018) albo 26.0 (26.0.11017); R-10 |
| Android fizyczne | NOT RUN | NOT RUN | NOT RUN | NOT RUN | nieprzetestowane w TapCue |
| iOS fizyczne | unsupported (TapCue MCP) | unsupported | unsupported | unsupported | luka TapCue |

## Decyzje do potwierdzenia przez Roberta

1. ~~Włączenie **STEP-02B** (demo app MAUI w tym repo).~~ **Zatwierdzone przez Roberta (2026-10-26)**, wariant domyślny; zrealizowane w STEP-02B (PR #7).
2. Pierwszy realny provider LLM dla `CliAgentReasoner`: domyślnie Claude CLI (`claude -p`) w kontenerze, Codex jako drugi adapter. **STEP-04:** zaimplementowany tylko `claude` (smoke lokalny PASS, kontener NOT RUN). Obraz kontenera agenta (`agent.containerImage`) wybiera operator; Codex nie jest zaimplementowany.
3. Dostęp do runnera macOS dla smoke iOS (GitHub Actions `macos-26` albo host Roberta).
4. Czy eksperyment z AppScreenMap (ADR-0005) jest wymagany przed STEP-03. Domyślnie: nie, opcjonalnie w STEP-05/10.
5. Wysyłanie screenshotów do LLM: domyślnie wyłączone.
