# HANDOFF: STEP-07: Profiles & variants

## Status i identyfikatory

- Status: **DONE**. Zakres ROADMAP STEP-07 zrealizowany, kontrole lokalne PASS. Wynik CI i demo na emulatorze: tabela dowodów i Checks PR. Merge wykonuje Robert.
- Branch: `feat/step-07-profiles-variants` (nazwa z promptu etapu)
- PR URL i status (open / merged / not created): <https://github.com/rtnk/MapCue/pull/10> (open, czeka na review i merge)
- Commit SHA: kod `346b530`, dokumentacja `618049a`. Ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `e07651b0dd55d91cadb673d384cb21d0fba87b7c` (merge PR #9; CI run 40918179871 success, Demo app run 40918179698 success)
- Data i środowisko: 2026-10-31. Lokalnie kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.114 (`apt-get install dotnet-sdk-10.0` w sesji). Bez urządzenia, Android SDK i TapCue (R-09).

## Zakres faktycznie wykonany

- **Gate:**
  - PR #9 zmergowany, `main` aktualny, CI i Demo app zielone;
  - ROADMAP: 06 → „zmergowany (PR #9)”;
  - handoff STEP-06: status PR = merged.
- **`ExplorationProfile` (YAML):** istniejące `environment: test`, `setupFlow`, `inputs` (tylko `secret://env/NAME` / `generated:`) i `capabilities`, plus nowe (walidowane):
  - `scenario` (identyfikator, domyślnie `default`);
  - `locale` (`und` albo tag języka);
  - `switchElements` (regex `resource-id`, 1–512 znaków, timeout 100 ms).

  Kontekst powstaje z profilu: `ExplorationOptions.Locale`/`ScenarioId` tylko nadpisują wartości profilu.
- **Decyzja (pytanie z STEP-05): run domyślnie odmawia zmiany profilu.** `policy.profileSwitch: deny|allow`, domyślnie `deny`.
  - `ProfileSwitchRules.For(settings, profile)` zamienia `switchElements` innych profili (tej samej aplikacji) na reguły `accountChange` (`profile-switch-<id>`) przed `policy.riskRules`.
  - Przy `shared-readonly` daje to `SkippedByPolicy`, przy `disposable` `RequiresApproval`. Akcja nigdy nie jest wykonywana bez zgody.
  - Reguły dostają walidator silnika i `RuleBasedReasoner` w CLI.
  - `mapcue.demo.yaml` nie potrzebuje już ręcznej reguły `other-profile`.
  - Uzasadnienie: jeden run = jeden kontekst. Zmiana profilu w środku runu zapisuje ekrany innego profilu jako dowody tego kontekstu. Test z `allow` pokazuje to mieszanie i rozbieżną nawigację.
- **Warianty i R-04:**
  - Margines Home `investor` (Jaccard 0,857 przy `τ_state` 0,85) okazał się realnym problemem na zakładce Accounts: 5/6 ≈ 0,83 < 0,85, więc bez nowej reguły byłby duplikatem stanu.
  - Reguła wariantu profilu w `ScreenIdentityResolver` (ADR-0006, STEP-07): `KnownStateVariant` z `ProfileVariantDelta`, gdy:
    - jest ten sam znacznik `screenHints`;
    - są te same dyskryminatory;
    - różnica ≤ `identity.profileVariantMaxDelta` (domyślnie 2) tokenów;
    - znany stan nie ma jeszcze wariantu w kontekście snapshotu.
  - W jednym kontekście inny zbiór elementów nadal daje inny stan. Bez `screenHints` reguła nie działa.
  - Remis dwóch stanów daje `Ambiguous`.
  - `GraphCommitter.KnownStatesAsync` jest wspólny dla commitu i read-only rozpoznania silnika.
- **`ScreenVariant.IdentityTokens`:** tokeny ostatniego snapshotu w kontekście (migracja `0006`). Na nich opiera się porównanie profili.
- **Ocena kandydata per kontekst (`candidate_assessments`, migracja `0006`):**
  - Błąd znaleziony w etapie: `action_candidates` mają klucz `(stateId, actionKey)` bez kontekstu, więc ryzyko i status z pierwszego profilu zostawały dla drugiego. Wyrocznia na FakeDevice zgłaszała „investor: action executed with risk AccountChange: tap profile.investor”.
  - Teraz interpreter (`Proposed`/`SkippedByPolicy`), planowanie (`Scheduled`) i commit (`Explored`) zapisują ocenę per kontekst; status tylko rośnie.
  - `ActionSpec` próby bierze ryzyko ze zwalidowanej akcji swojego zadania.
- **`ReachabilityRecipe` per kontekst:**
  - przy zakończeniu runu `RecipeBuilder` zapisuje recipe `rcp_… = H(contextId, targetStateId)` dla każdego stanu osiągalnego ze startu po odtwarzalnych krawędziach tego kontekstu;
  - kroki: `SetupFlow`/`LaunchApp`, potem akcje z oczekiwanym stanem;
  - `verifiedAt` pozostaje puste (replay to STEP-09);
  - nowe `IGraphRepository.ListRecipesAsync`.
- **Porównanie profili (`ProfileAnalysis.AnalyzeAsync`)**, deterministyczne, tylko z bazy:
  - grupa porównania: app, platforma, build, locale;
  - porównywane tylko tokeny interaktywne stanów widzianych w danym kontekście;
  - macierz pokrycia (`observed`/`notObserved`, snapshoty, krawędzie, recipe);
  - klasy równoważności kontekstów per stan;
  - hipotezy guardów `inferred` / `observed` / `refuted` (dopasowanie nazwy elementu do capability, potem obserwacja braku na tym samym stanie) z `evidenceIds` z obu stron.
- **CLI:** `mapcue status --profiles` wypisuje linie `context` / `coverage` / `equivalent` / `guard` (redagowane).
- **Demo:**
  - `mapcue.demo.yaml`: profile `basic` i `investor` (capability `investments`, `setup-investor.yaml`, `switchElements`);
  - `run-explore.sh`: dwa runy w jednym workspace (`--seed demo-<profile>`) i `status --profiles`;
  - `compare-expected.py`: kontrole per kontekst (przejścia `basic` 25, `investor` 33) i kontrola 5: jeden stan na znacznik ekranu, `tab.investments` tylko w wariantach `investor` i nigdy jako akcja `basic`, snapshoty obserwacji w kontekście krawędzi.

## Pliki / API / migracje

- **Nowe:**
  - `src/MapCue.Application/Profiles/{ProfileSwitchRules,ProfileAnalysis}.cs`;
  - `src/MapCue.Infrastructure.Sqlite/Migrations/0006_profile_variants.sql`;
  - testy: `tests/MapCue.EndToEnd.Tests/ProfilesEndToEndTests.cs`, `tests/MapCue.Application.Tests/Graph/ProfileVariantResolverTests.cs`, `tests/MapCue.Application.Tests/Profiles/ProfileConfigTests.cs`, `tests/MapCue.Infrastructure.Tests/Graph/ProfileStoreTests.cs`;
  - `FakeDemoProfiles` w `tests/MapCue.Testing/FakeDemoApp.cs`.
- **Zmienione:**
  - `MapCueSettings` (`ProfileSettings.{ScenarioId,Locale,SwitchElements}`, `PolicySettings.ProfileSwitch`, `IdentitySettings.ProfileVariantMaxDelta`), `MapCueConfigDocument`, `MapCueConfigValidator`;
  - `ScreenIdentityResolver` (`Resolve(…, contextId)`, `Delta`, `KnownState.ContextIds`, `IdentityResolution.ProfileVariantDelta`);
  - `GraphCommitter`, `AgentInterpreter`, `ExplorationEngine` (reguły zmiany profilu, kontekst z profilu, ryzyko próby, recipes), `ExplorationOptions`;
  - `IStateStore` (`UpsertCandidateAssessmentAsync`, `ListCandidateAssessmentsAsync`, `ListRecipesAsync`), `SqliteRepositories`;
  - domena: `ScreenVariant.IdentityTokens`, `CandidateAssessment`, `ReachabilityRecipe.IdOf`;
  - CLI: `ExploreCommand`, `StatusCommand`;
  - `FakeDevice` (`FakeAppModel.SetupScreens`), `ExplorationHarness` (`Basic`, `Investor`, `ProfilesSettings`, `RunProfileAsync`);
  - demo: `mapcue.demo.yaml`, `run-explore.sh`, `compare-expected.py`, `expected-graph.md`, `.github/workflows/demo-app.yml` (komentarz);
  - dokumenty: architektura §5.7, §6, §7.3; ADR-0006; ROADMAP; README; `docs/examples/mapcue.example.yaml`; handoff STEP-06.
- **Zmiany łamiące** (bez zewnętrznych konsumentów):
  - `ExplorationOptions.Locale`/`ScenarioId` są nullable;
  - nowe metody portu `IGraphRepository`;
  - `compare-expected.py` raportuje per kontekst;
  - przykład konfiguracji ma dwa profile.
- **Migracja:** `0006`, przyrostowo od 5, testowana. Warianty sprzed migracji mają `identity_tokens_json = '[]'` i nie są porównywane (nie zgaduje się ich z bazy stanu).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #9 zmergowany, `main` aktualny, CI i Demo app zielone | GitHub API: PR #9 merged; `main` = `e07651b`; runy 40918179871 (CI), 40918179698 (Demo app) `success` | PASS | — |
| Baseline przed zmianami | `dotnet test MapCue.sln -c Release --no-build` na `e07651b` / lokalnie | PASS (702 pass, 1 skip) | — |
| Build Release (0 ostrzeżeń) | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.114 | PASS | — |
| Testy (11 projektów testowych) | `dotnet test MapCue.sln -c Release --no-build` / lokalnie | PASS (739 pass, 1 skip: smoke CLI agenta bez zmiennej) | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` | PASS | — |
| Test architektury (17) | `MapCue.Architecture.Tests` | PASS | — |
| Dwa profile w jednym workspace (`FakeDemoProfiles`): 7 wspólnych stanów, Investments tylko `investor` (run investor: 1 nowy stan, 1 nowy ekran), jeden stan na znacznik ekranu, krawędzie `basic` niezmienione po runie `investor`, `tab.investments` tylko w kontekście `investor`, obserwacje, snapshoty, dowody i zadania każdy w swoim kontekście | `ProfilesEndToEndTests.Two_profiles_share_states_but_never_edges_tasks_or_evidence` | PASS | — |
| Guard `tab.investments`: po `investor` `inferred` (capability `investments`, brak `absent`), po `basic` `observed` (present = investor, absent = basic, stany Home/Accounts/Settings + Investments, dowody z obu kontekstów); kolejność odwrotna: brak hipotezy po `basic`, `observed` po `investor` | `The_investments_guard_is_inferred_…`, `Basic_first_gives_no_hypothesis_…` | PASS | — |
| Macierz pokrycia, klasy równoważności (Home i Accounts: 2 klasy, What's new i Details: 1), recipes per kontekst (7 basic, 8 investor, tylko krawędzie własnego kontekstu, start `SetupFlow`) | `Coverage_matrix_equivalence_classes_and_recipes_are_per_context` | PASS | — |
| Odmowa zmiany profilu (domyślnie): `profile.investor` nie wykonany w `basic`; z `allow` wykonany, a graf `basic` miesza profile (`AllRemainingBlocked` przez rozbieżną nawigację) | `A_run_refuses_…`, `With_profile_switch_allowed_…` | PASS | — |
| Ocena kandydata per kontekst: `profile.investor` = `accountChange`/`SkippedByPolicy` w `basic`, `navigational`/`Explored` w `investor` | `A_shared_candidate_has_its_risk_and_status_per_context`, `ProfileStoreTests` | PASS | — |
| R-04: bez reguły (`profileVariantMaxDelta: 0`) Accounts `investor` jest drugim stanem, a Home pozostaje jednym (0,857); nagrania Home: różnica 1 token, reguła rozpoznaje wariant także przy `τ_state` = 0,9 | `Without_the_profile_variant_rule_…`, `DemoAppRecordingCalibrationTests.The_investor_home_is_also_a_profile_variant_…` | PASS | — |
| Determinizm: dwa workspace'y z tą samą kolejnością i ziarnami dają identyczny graf i raport profili | `Two_workspaces_explored_in_the_same_order_give_the_same_graph` | PASS | — |
| Resolver (różnica w innym/tym samym kontekście, limit, znacznik, dyskryminatory, brak hintów, remis), konfiguracja (domyślne, odczyt, 6 błędów), reguły zmiany profilu, dopasowanie nazw | `ProfileVariantResolverTests` (6), `ProfileConfigTests` (13) | PASS | — |
| YAML → `explore --profile basic`, `explore --profile investor` → `status --profiles` (guard `observed`, `notObserved`), konfiguracja demo i przykład z dwoma profilami | `ExploreCommandTests.Two_profiles_from_yaml_…`, `ConfigLoaderTests` | PASS | — |
| Migracja 5 → 6, tokeny wariantu, recipes per kontekst | `ProfileStoreTests` (4) | PASS | — |
| Kontrola mutacją: walidator silnika bez `ProfileSwitchRules` | `ProfilesEndToEndTests`, kod tymczasowo zmieniony i przywrócony | testy wykrywają (4/9 FAIL) | — |
| Wyrocznia na workspace z FakeDevice (oba profile) | tymczasowy test zapisujący workspace + `python3 -I samples/MapCue.DemoApp/compare-expected.py <ws> <report>` | PASS (basic 25/25, investor 33/33); przed naprawą ocen kandydatów FAIL (opisane wyżej) | — |
| Spójność demo app | `python3 -I samples/MapCue.DemoApp/check-consistency.py` | PASS | — |
| CI na PR | `ci.yml`, `demo-app.yml` (w tym `android-explore`: dwa profile na emulatorze z TapCue 0.1.0) | wynik w PR #10 (Checks) | PR → Checks, artefakt `demo-android-explore` |
| Demo na emulatorze lokalnie | — | NOT RUN: brak Android SDK i emulatora w sesji (R-09) | — |

## Ograniczenia, ryzyka, otwarte pytania

- **Ochrona przed zmianą profilu zależy od `switchElements`.** MapCue nie zgaduje przycisków wyboru profilu. Profil bez `switchElements` nie jest chroniony; wtedy pomaga tylko reguła operatora.
- **Reguła wariantu profilu wymaga `screenHints`.** Bez znacznika ekranu różnica profilu poniżej `τ_state` daje nowy stan (jak przed STEP-07). `profileVariantMaxDelta` > 2 zwiększa ryzyko fałszywych scaleń; zakres walidacji to 0–8.
- **Alias rozpoznania jest globalny** (jak od STEP-03). Hash snapshotu z innego profilu, rozpoznany jako wariant, będzie tym samym stanem także w innych kontekstach. Dla rozłącznych profili nie ma to skutków; przy `allow` może scalić warianty.
- **Hipotezy guardów:**
  - `inferred` opiera się tylko na nazwie (segment po `/` w `resource-id` albo w kluczu roli i etykiety);
  - elementy ekranu dostępnego tylko w jednym profilu (np. `investments.buy`) zostają `inferred`, bo braku ekranu w drugim profilu nie traktuje się jako obserwacji braku elementu;
  - poziom `verified` (replay) nie istnieje przed STEP-09/10.
- **Recipes** są zapisywane tylko przy zakończeniu runu (także `Failed`/`BudgetExhausted`). Run zabity `SIGKILL` nie aktualizuje ich, robi to kolejny run. Brak replay i `verifiedAt` (STEP-09).
- **`ActionCandidate.RiskClass`/`Status`** zostają wartościami pierwszego kontekstu (zgodność wstecz). Prawdą per kontekst są `candidate_assessments`. Raporty STEP-10 powinny czytać oceny.
- **Pytania do Roberta:**
  1. czy `mapcue explore` ma dostać `--all-profiles` (kolejne konteksty w jednym procesie), czy zostajemy przy jednym profilu na wywołanie;
  2. czy domyślne `profileVariantMaxDelta` = 2 jest akceptowalne, czy wolisz 1 (tylko dokładnie przypadek Investments);
  3. otwarte pytania STEP-06 (`OutcomeUnknown` dla `safe`/`navigational`, limit `maxStates` na workspace) pozostają do STEP-09.

## Stan roadmapy

- STEP-00…06 i 02B: zmergowane. STEP-07: PR #10 otwarty.
- W `docs/ROADMAP.md` zaktualizowane: statusy 06 i 07, R-04 (wynik sprawdzenia marginesu i reguła), R-06 (odmowa zmiany profilu, oceny per kontekst). Macierz platform bez zmian do wyniku CI; demo dwóch profili na emulatorze sprawdza job `android-explore` w PR.
- Następny: STEP-08 (Screen Contract & accessibility, zależy od 07) albo STEP-09 (Recovery & operator handoff, zależy od 06, a od 07 dla wariantów). Oba po merge tego PR.

## Warunki startu STEP-08 (i STEP-09)

- PR STEP-07 **zmergowany** do `main`, CI (`CI`, `Demo app`) zielone na `main`, lokalny `main` zaktualizowany.
- Kontrakty z tego etapu, na których kolejne etapy się opierają:
  - `ProfileSettings.{ScenarioId,Locale,SwitchElements}`, `PolicySettings.ProfileSwitch`, `ProfileSwitchRules`;
  - reguła wariantu profilu (`IdentityResolverOptions.ProfileVariantMaxDelta`, `GraphCommitter.KnownStatesAsync`);
  - `ScreenVariant.IdentityTokens` (kontrakt per wariant w STEP-08);
  - `CandidateAssessment` (ryzyko per kontekst dla zgód operatora w STEP-09 i raportów STEP-10);
  - `ReachabilityRecipe` per kontekst (`IdOf`, `ListRecipesAsync`; replay w STEP-09);
  - `ProfileAnalysis` (macierz pokrycia per profil w STEP-10);
  - `FakeDemoProfiles`, `ExplorationHarness.ProfilesSettings`/`RunProfileAsync`.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-08: Screen Contract & accessibility.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-08, rejestr ryzyk R-07, luki G-03…G-05 i wspólne DONE), docs/architecture.md (§5.7, §6, §8.2, §14), docs/tapcue-compatibility.md, ADR-0002, ADR-0006, docs/handovers/STEP-07.md oraz kod: `src/MapCue.Application/Graph/` (`SnapshotNormalizer`, `GraphCommitter`), `src/MapCue.Application/Profiles/`, `src/MapCue.Application/Ports/IDeviceAutomation.cs`, `src/MapCue.Adapters.TapCue/`, `schemas/`, `tests/MapCue.Testing/{FakeDevice,FakeDemoApp}.cs`, a także `samples/MapCue.DemoApp/` (`expected-graph.md`).
> Zweryfikuj, że PR STEP-07 (<https://github.com/rtnk/MapCue/pull/10>) jest zmergowany, a `main` aktualny i z zielonym CI (`CI` i `Demo app`). Jeśli nie, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-08-screen-contract` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-08.md` w tym samym PR. Nie rozpoczynaj STEP-09 i nie merguj PR.
> Zakres (ROADMAP STEP-08): `schemas/screen-contract.v1.schema.json`, `AnalyzeScreen` (pełna hierarchia przez CLI), wersjonowanie kontraktu (`contentHash`), warianty elementów (per kontekst, z `ScreenVariant.IdentityTokens`), `AuditAccessibility` z macierzą TalkBack/VoiceOver/Keyboard i poziomami dowodu (architektura §14), graf fokusu klawiatury Android (Tab, PoC), przewidywana kolejność z drzewa jako `predicted/unverified`, protokół manualny operatora (`mapcue a11y record`) jako jedyna droga do `assistiveVerified` dla czytników.
> Testy: kontrakt per ekran demo app (oba profile); raport nigdy nie daje PASS przy poziomie `inspected`; Android keyboard graph na emulatorze (PASS albo NOT RUN, z komendą); iOS: `unsupported` w odpowiednich komórkach.
> DoD: kontrakt per ekran, test na platformie możliwej do uruchomienia, `unsupported`/`unverified` zamiast fałszywego PASS; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury przechodzi; ROADMAP i handoff STEP-08 zaktualizowane.
