# HANDOFF: STEP-03: Graph & persistence

## Status i identyfikatory

- Status: **DONE** (zakres ROADMAP STEP-03 zrealizowany i przetestowany bez urządzenia). Merge wykonuje Robert.
- Branch: `claude/step-03-graph-persistence-068set` (prompt etapu wskazywał `feat/step-03-graph-persistence`; środowisko sesji wyznacza ten branch i nie pozwala pushować na inny)
- PR URL i status: <https://github.com/rtnk/MapCue/pull/5> (open, czeka na review i merge)
- Commit SHA: kod `884b126` (ostatnia zmiana w `src/`/`tests/`); ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `82d311191a54af6c810fb3eee704396e14a4b195` (merge PR #4; CI na `main`: success, run 40241494690)
- Data i środowisko: 2026-10-25. Kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.113 (pakiet Ubuntu `dotnet-sdk-10.0`, doinstalowany w sesji), bez urządzenia, TapCue i LLM (niepotrzebne w tym etapie).

## Zakres faktycznie wykonany

- **Typy domenowe** (`src/MapCue.Domain/Graph/`):
  - rekordy z architektury §6: `LogicalScreen`, `ScreenState`, `ScreenVariant`, `UiSnapshot`, `UiElement` (+ `UiElementStates`), `UiList`, `NormalizedSnapshot`, `ActionCandidate`, `ScreenEdge`, `ActionObservation`, `Evidence`, `BlockedReason`, `FrontierTask`, `TaskAttempt`, `FrontierStats`, `Checkpoint`, `RecipeStep`, `ReachabilityRecipe`, `OutboxEvent`;
  - enumy: statusy, fazy, kody blokad, siła klucza;
  - `ExplorationContext` (sygnatura `H(profil, platforma, appId, build, locale, scenariusz, posortowane capabilities)`, `contextId` z sygnatury);
  - `TaskKeys` (§7.4), `EdgeStatuses.Raise` (status nie spada), `ContentHash`.
- **Normalizacja i fingerprint** (`src/MapCue.Application/Graph/`, `FingerprintVersion = 1`):
  - `SystemUiRules`: jawne, testowane reguły per platforma. Android: `com.android.systemui`, klawiatury Gboard i AOSP. Usuwane jest poddrzewo i puste kontenery, które po nim zostają. Klawiatura daje dyskryminator. iOS: brak reguł (brak nagrań, R-01).
  - `TextMasker`: wejścia profilu, znaczniki redakcji, e-mail, IBAN, daty (ISO, numeryczne, nazwy miesięcy EN/PL), godziny, kwoty, liczby → `#`.
  - Listy: multizbiór kształtów dzieci; `list(template, 0|1|many)`; wiersz częściowy rozpoznawany po sygnaturze korzenia.
  - `elementKey`: `id:` → `label:rola:etykieta` → `path:` (słaby).
  - `identityHash` (interaktywne i kotwice z `resource-id` spoza wierszy, listy, `selected`, dyskryminatory `modal/loading/keyboard/empty`), `layoutHash` (kolejność i bounds w kubełkach 8 px), `elementSetHash` (wariant).
  - `StateKind`, `ObservedPackages`, `WeakKeyCount` (metryka R-04).
  - Doprecyzowania reguł opisuje ADR-0006 („Implementacja w STEP-03”).
- **Resolver tożsamości** (`ScreenIdentityResolver`):
  - dopasowanie: dokładny hash albo alias → `KnownState`; Jaccard ≥ 0,85 bez różnicy dyskryminatorów → `KnownStateVariant` (zapis aliasu); ≥ 0,6 → `NewStateOfScreen`; inaczej `NewScreen`;
  - dwa różne kandydaty w odległości < 0,02 → `Ambiguous`: nowy stan `provisional` i zdarzenie z hipotezami, bez scalania;
  - porównywane są tylko stany z tym samym `fingerprintVersion`.
- **Zapis**:
  - `SnapshotRecorder`: redakcja pól, dokument `mapcue.raw-snapshot/1` do store'u (druga redakcja), normalizacja z dokumentu **w postaci zapisanej**, dokument `mapcue.normalized-snapshot/1`, dowody `RawSnapshot` i `NormalizedSnapshot` z liczbą i regułami redakcji;
  - `GraphCommitter`: jedna transakcja na obserwację lub próbę, idempotentna po `attemptId`. Zakres transakcji: stan, wariant, snapshot, dowody, kandydat, krawędź (`nondeterministic` przy różnych celach), obserwacja, zadanie `ExploreAction` (Completed), próba (`n` po istniejących), nowe `AnalyzeState` `ON CONFLICT DO NOTHING`, zdarzenia outboxa;
  - `GraphImporter`: deterministyczne `attemptId` z planu, checkpoint tylko przy zmianie;
  - `GraphQueries`: podsumowanie i kanoniczny widok grafu.
- **SQLite** (`src/MapCue.Infrastructure.Sqlite/`):
  - `SqliteStateStore` (WAL, `foreign_keys=ON`, `synchronous=NORMAL`, `busy_timeout`, `BEGIN IMMEDIATE`, rollback przy wyjątku i anulowaniu, zakaz zagnieżdżeń);
  - `SqliteMigrator`: zasoby `Migrations/0001_graph_core.sql`, `0002_edges_frontier_outbox.sql`; `schema_migrations` z sumą SHA-256 i `user_version`; nowsza lub zmieniona migracja → `SchemaMismatchException`;
  - repozytoria `Graph`, `Frontier`, `Events` (ręczny SQL, bez Dappera);
  - `WorkspaceLock` (`.mapcue/lock`, R-11).
- **Artefakty**: `FileSystemArtifactStore` (`blobs/sha256/ab/<hex>`, redakcja przed hashem, temp + `fsync` + rename, walidacja referencji przeciw path traversal, binarnie tylko `Screenshot`).
- **CLI**: `mapcue graph import <plan> [--workspace|-w]` (narzędzie testowe, plan `mapcue.graph-import/1`). Kody wyjścia `5` (`InputInvalid`) i `6` (`WorkspaceUnavailable`). Snapshot help zaktualizowany.
- **CI**: krok „Graph import smoke” w `ci.yml` (dwa importy nagrań = ten sam graf).

## Pliki / API / migracje

- Nowe: `src/MapCue.Domain/Graph/{ContentHash,GraphEnums,ExplorationContext,GraphModel,TaskKeys}.cs`, `src/MapCue.Application/Graph/{TextMasker,SystemUiRules,SnapshotNormalizer,ScreenIdentityResolver,GraphJson,SnapshotRecorder,GraphCommitter,GraphImporter,GraphQueries}.cs`, `src/MapCue.Infrastructure.Sqlite/{SqliteStateStore,SqliteRepositories,SqliteMigrator,Db,WorkspaceLock}.cs` + `Migrations/*.sql`, `src/MapCue.Infrastructure.Artifacts/FileSystemArtifactStore.cs`, `src/MapCue.Cli/Graph/{GraphCommand,GraphImportFile}.cs`, `tests/Fixtures/graph-import/android-settings-cycle.json`, `tests/MapCue.Testing/UiTrees.cs`.
- Zmienione: `src/MapCue.Application/Ports/{IStateStore,IArtifactStore}.cs`, `src/MapCue.Cli/{MapCueCli,ExitCodes}.cs`, `Directory.Packages.props` (`Microsoft.Data.Sqlite` 10.0.13, `FsCheck.Xunit` 3.3.2), csproj Sqlite i trzech projektów testowych, `README.md`, `docs/ROADMAP.md` (statusy, R-04, R-11), `docs/adr/ADR-0003…`, `docs/adr/ADR-0006…` (sekcje „Implementacja w STEP-03”), `.github/workflows/ci.yml`, `tests/MapCue.Cli.Tests/Snapshots/help.txt`.
- **Zmiany łamiące (bez konsumentów poza testami):**
  - `IArtifactStore.PutAsync(Stream, …)` zastąpione przez `PutTextAsync`, `PutBinaryAsync`, `ReadTextAsync` i `Exists`; nowe `StoredArtifact`, `ArtifactKind.NormalizedSnapshot` i `RedactionInfo.None`;
  - `IUnitOfWork` ma repozytoria `Graph`, `Frontier` i `Events`;
  - nowa komenda `graph` i kody wyjścia `5`/`6`.
- Migracje: `0001_graph_core`, `0002_edges_frontier_outbox` (pierwsze migracje projektu).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #4 zmergowany, `main` aktualny, CI zielone | GitHub API PR #4 `merged: true` (2026-10-25 22:42), `git fetch origin main` = `82d3111` = HEAD startowy, run 40241494690 `success` | PASS | — |
| Build Release | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.113 | PASS (0 ostrzeżeń) | — |
| Testy (9 projektów, 424) | `dotnet test MapCue.sln -c Release --no-build` / lokalnie | PASS | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` | PASS | — |
| Test architektury | `MapCue.Architecture.Tests` (17) | PASS | — |
| Property-based: permutacja, cyfry, długość listy (2..40), pusta lista, System UI, determinizm, nowy element | `FingerprintPropertyTests` (FsCheck, 100–300 przypadków na property); jednorazowo 5000/property (zmiana cofnięta) | PASS | — |
| Te same własności na nagranych snapshotach Androida + przypięte `identityHash` v1 + fast/full launcher ≥ τ_state | `RecordedSnapshotFingerprintTests` (ContractTests) | PASS | `Fixtures/tapcue-0.1.0/` |
| Odfiltrowanie System UI jako jawna reguła | `SnapshotNormalizerTests` (reguły, iOS bez reguł, reguły własne, klawiatura) + 26 elementów usuniętych z obu nagrań | PASS | — |
| Cykl A→B→A bez nowych zadań dla znanej pary | `GraphCommitTests.Cycle_home_accounts_home…`, `GraphImportTests.Cycle_in_the_plan…` | PASS | — |
| Dwa profile bez mieszania wariantów, krawędzi, dowodów i zadań | `GraphCommitTests.Two_profiles…`, `GraphImportTests.Second_profile_import…` | PASS | — |
| Idempotentny import (2× = ten sam graf; 2 świeże workspace = identyczny graf) | `GraphImportTests`, `GraphCommandTests.Import_of_recorded_android_snapshots_is_idempotent` | PASS | — |
| Migracje od zera i przyrostowo (schemat, dane, `user_version`), zmieniona i nowsza migracja odrzucona | `SqliteStoreTests` | PASS | — |
| Atomowość: wyjątek w środku transakcji, awaria na końcu commitu próby (wstrzyknięta w outbox), awaria późniejszej próby, anulowanie | `SqliteStoreTests` | PASS | — |
| Redakcja przed zapisem (e-mail, zarejestrowany sekret; brak w żadnym blobie), fingerprint odtwarzalny z zapisanego bloba | `GraphCommitTests`, `FileSystemArtifactStoreTests` | PASS | — |
| Smoke CLI: `graph import` 2× na nagraniach (jak krok CI) | `dotnet run --project src/MapCue.Cli -c Release --no-build -- graph import tests/Fixtures/graph-import/android-settings-cycle.json --workspace <tmp>` ×2 | PASS (`new-states=0 … already-committed=2`, ten sam wiersz `graph`) | — |
| CI GitHub Actions na PR #5 | `.github/workflows/ci.yml`, `tapcue-smoke.yml` | NOT RUN w chwili pisania (uruchamia się po pushu; wynik w PR) | PR #5 → Checks |
| Testy na urządzeniu | — | NOT RUN (etap nie wymaga urządzenia) | — |

## Ograniczenia, ryzyka, otwarte pytania

- **R-04 (kalibracja):** reguły list, kotwic i progów są skalibrowane tylko na dwóch ekranach systemowych Androida i na danych syntetycznych. Wymagają weryfikacji na demo app (02B) w STEP-05. Znane decyzje:
  - statyczny tekst bez `resource-id` nie wchodzi do tożsamości: dwa ekrany różniące się tylko takim tytułem zleją się, do rozwiązania przez `screenHints` w STEP-05;
  - w kontenerze nieprzewijanym lista wymaga ≥ 3 powtórzeń; lista z 2 wierszami w takim kontenerze to inny stan niż z 3;
  - bucket `1` w praktyce nie występuje;
  - daty rozpoznawane są tylko po angielsku i polsku.
- **iOS:** brak reguł System UI (brak nagrań iOS, R-01). Pierwsze nagranie iOS powinno je dodać i podbić `fingerprintVersion`.
- `screenHints`, `Transient`/stabilizacja i scalanie `Ambiguous` (`mapcue graph merge`, P1) nie wchodzą w zakres STEP-03. Hipotezy są tylko w zdarzeniach outboxa (bez osobnej tabeli).
- Resolver wczytuje wszystkie stany przy każdym commicie (O(n)). Wystarcza przy budżecie `maxStates = 120`, przy większych grafach trzeba indeksu.
- `ScreenVariant.SnapshotCount` liczy **różne** snapshoty. Różnica elementów opisuje ostatni nowy snapshot względem bazy stanu (pierwsza obserwacja).
- `ReachabilityRecipe` ma typ, tabelę i repozytorium, ale nie ma budowania z grafu (planer: STEP-06/09). Lease i claim zadań: STEP-06. `mapcue gc`: później.
- `graph import` czyta pliki wskazane w planie (ścieżki względne do planu). To narzędzie testowe, plan nie zawiera wartości wejść.
- Zdarzenia outboxa mają id z `IIdGenerator` (UUIDv7 w CLI), więc nie wchodzą do kanonicznego porównania grafu.
- Pytania do Roberta: brak nowych. Nadal otwarte decyzje z ROADMAP (02B, runner macOS).

## Stan roadmapy

- STEP-00, 01, 02: zmergowane. STEP-03: PR #5 otwarty. Tabela statusów i rejestr ryzyk (R-04, R-11) w `docs/ROADMAP.md` są zaktualizowane.
- Następne: STEP-04 (zależy od 01 i 03) po merge tego PR; STEP-02B (decyzja 1) niezależnie.

## Warunki startu STEP-04

- PR #5 **zmergowany** do `main`, CI zielone na `main`, lokalny `main` zaktualizowany.
- Kontrakty z tego etapu, na których STEP-04 się opiera:
  - `NormalizedSnapshot` / `UiElement` (`elementRef` `e1…`, `elementKey`, `Label` zamaskowany, `Text` zredagowany, `Interactive`, `ListTemplate`) jako wejście generatora `AgentExplorationRequest`; `snapshotHash` dla kontroli stale = `UiSnapshot.IdentityHash` albo `RawSha256`, do decyzji w STEP-04;
  - `ActionCandidate` (`actionKey = H(type, canonicalSelector, paramClass)`, `CandidateSource.Agent`);
  - `TaskKeys.AnalyzeState(stateId, context, reasonerPromptVersion)`; commit przyjmuje `ReasonerPromptVersion` (domyślnie `none`).

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-04: Agent interpretation.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-04, rejestr ryzyk R-05/R-06 i wspólne DONE), docs/architecture.md (§9, §10), ADR-0004, docs/handovers/STEP-03.md oraz kontrakty `NormalizedSnapshot`/`UiElement`/`ActionCandidate` w `src/MapCue.Domain/Graph/GraphModel.cs`, `SnapshotNormalizer` i `GraphCommitter` w `src/MapCue.Application/Graph/`, a także istniejące DTO w `src/MapCue.Contracts/Agent/`.
> Zweryfikuj, że PR-03 (<https://github.com/rtnk/MapCue/pull/5>) jest zmergowany, a `main` aktualny i z zielonym CI. Jeśli nie jest, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-04-agent-interpretation` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-04.md` w tym samym PR. Nie rozpoczynaj STEP-05/02B i nie merguj PR.
> Zakres (ROADMAP STEP-04): `schemas/agent-exploration-request.v1.schema.json` i `…-response.v1.schema.json`; generator żądania z zredagowanego `NormalizedSnapshot` (elementy po `elementRef`, `graphHints` z grafu); walidator w kolejności: schemat, stale (`snapshotHash`), semantyka (`elementRef` istnieje i jest interaktywny), ryzyko, a na końcu `ValidatedDeviceAction`; regułowy klasyfikator ryzyka (wielojęzyczne słowa kluczowe, reguły operatora, `max(agent, reguły)`); `PolicyGate`; `DeterministicFakeReasoner`, `RuleBasedReasoner`, `CliAgentReasoner` (kontener domyślnie, `local` tylko jawnie); budżety tokenów i kosztu; zapis `AgentCall` (żądanie i odpowiedź jako artefakty przez `IArtifactStore.PutTextAsync`); kandydaci akcji jako `ActionCandidate` (`CandidateSource.Agent`).
> Testy: złote JSON (poprawne i błędne: dodatkowe pola, zły `elementRef`, inny `snapshotHash`, `inputText` z wartością literalną, YAML w polu tekstowym, klasa `destructive`); akcja spoza allowlisty zawsze odrzucona; naprawa formatu najwyżej raz; test izolacji (brak fallbacku na host). Smoke `CliAgentReasoner` z prawdziwym CLI: PASS albo NOT RUN.
> DoD: agent nie może wykonać akcji spoza allowlisty; malformed i stale JSON jest odrzucany; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury nadal przechodzi; handoff STEP-04 zapisany. Wyniki raportuj jako PASS, FAIL albo NOT RUN, z komendami.
