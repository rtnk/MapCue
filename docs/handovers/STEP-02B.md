# HANDOFF: STEP-02B: Demo/fixture app (MAUI)

## Status i identyfikatory

- Status: **DONE** (zakres ROADMAP STEP-02B zrealizowany; APK i flow na emulatorze PASS w CI; build iOS NOT RUN, opis niżej). Merge wykonuje Robert.
- Branch: `claude/step-02b-demo-app-wm0b88` (prompt etapu wskazywał `feat/step-02b-demo-app`; środowisko sesji wyznacza ten branch)
- PR URL i status: <https://github.com/rtnk/MapCue/pull/7> (open, czeka na review i merge)
- Commit SHA: kod i flow `2a08fcc` (zweryfikowany w CI: runy 40335436029 `Demo app` i 40335435627 `CI`); ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `2f38076ad3314133d125e9a21aa8b828dca2d168` (merge PR #6; CI na `main`: success, run 40299299229)
- Data i środowisko: 2026-10-26. Sesja agenta: kontener Linux x86_64 (Ubuntu 24.04), .NET SDK 10.0.113 (pakiet Ubuntu), Java 17, TapCue 0.1.0 (archiwum Release, SHA-256 przypięte), bez workloadów MAUI, Android SDK i urządzenia (`dl.google.com`: 403 przez proxy, R-09). Build APK i flow: GitHub Actions `ubuntu-24.04` (setup-dotnet z `global.json`, workload `maui-android`, Zulu JDK 17, emulator API 34 `google_apis` x86_64 z KVM).

## Zakres faktycznie wykonany

- **Decyzja 1 (ROADMAP):** Robert zatwierdził STEP-02B, wariant domyślny (MAUI w tym repo). Odnotowane w ROADMAP (statusy, „Zmiany względem planu bazowego” p. 1, „Decyzje do potwierdzenia” p. 1).
- **Aplikacja `samples/MapCue.DemoApp`** (`net10.0-android`; `net10.0-ios` poza Linuksem albo przez `-p:TargetFrameworks=net10.0-ios`). Kod C# bez XAML, bez backendu, sieci i uprawnień, dane stałe w pamięci (`DemoData`), każde uruchomienie zaczyna od wyboru profilu. 9 ekranów i 2 dialogi:
  1. wybór profilu (`basic`/`investor`) i przełącznik scenariusza pustych rachunków;
  2. ładowanie (`ActivityIndicator`, deterministyczne 1500 ms);
  3. Home z zakładkami;
  4. Accounts (2 lub 3 wiersze albo pusta lista z `accounts.empty`);
  5. Investments (tylko `investor`, element nie istnieje w drzewie dla `basic`);
  6. Ustawienia;
  7. szczegóły rachunku;
  8. strona modalna „What's new”;
  9. nieznany popup „Verify your identity” (blokada: ignoruje `back`, ma tylko akcje ryzykowne; na iOS pełny ekran).

  Dialogi: potwierdzenie usunięcia (`AlertDialog`) i komunikat zakupu. Cykle: Home ↔ Accounts (przycisk i zakładki), Details → Home, Settings → wybór profilu. Akcje ryzykowne: `details.delete` (accountChange), „Delete” (destructive), `investments.buy` (financial), `verify.sendcode` (communication), `verify.signout` (accountChange). Stałe `AutomationId` (34, `AutomationIds.cs`, format `<lowercase>.<lowercase>` bez cyfr).
- **Izolacja od `MapCue.sln`:** `samples/Directory.Build.props` i `samples/Directory.Packages.props` zatrzymują dziedziczenie ustawień repo (net10.0, warnings as errors, invariant globalization, central package management). Projekt nie jest w solucji; test architektury skanuje tylko `src/` i `tests/`.
- **Flow TapCue** `samples/MapCue.DemoApp/.tapcue/`: `setup-basic.yaml`, `setup-investor.yaml`, `setup-basic-empty.yaml` (setup profili) oraz `tour-basic.yaml`, `tour-investor.yaml` (sprawdzenie `expected-graph.md` na urządzeniu: modal, cykle, różnica profili, potwierdzenie usunięcia anulowane, blokada odporna na `back`). Żaden flow nie zatwierdza akcji ryzykownej.
- **Wyrocznia** `samples/MapCue.DemoApp/expected-graph.md`: jak MapCue widzi aplikację, profile i scenariusze, ekrany i stany, tabela przejść z klasą ryzyka, różnice profili, akcje ryzykowne, blokada, graf Mermaid, tabela „Ryzyko akcji” i pełna lista `AutomationId`.
- **Kontrole spójności:**
  - `check-consistency.py`: `AutomationId` z kodu = lista w `expected-graph.md`, każde używane przez stronę; flow używają tylko `id: "(.*/)?<AutomationId>"` z istniejącym id i `appId` z projektu;
  - `tests/MapCue.Application.Tests/Agent/DemoAppRiskOracleTests.cs`: tabela „Ryzyko akcji” = wynik `RiskClassifier` (bez zmian w `src/`).
- **Nagrania** `samples/MapCue.DemoApp/recordings/`: hierarchie Home po setupie `basic` i `investor` z emulatora (do kalibracji w STEP-05).
- **CI** `.github/workflows/demo-app.yml` (akcje przypięte do SHA, `permissions: contents: read`; uruchamiany przy zmianach `samples/**` i samego workflow, push na `main`, `workflow_dispatch`):
  - `android-build`: spójność, workload `maui-android`, `InstallAndroidDependencies`, `dotnet publish -c Release` (APK podpisany kluczem debug), artefakt `demo-apk` i binlog;
  - `android-smoke`: KVM, TapCue 0.1.0 (`fetch-tapcue.sh`), `check-syntax` wszystkich flow, emulator API 34, `run-flows.sh` (instalacja APK, 5 flow `tapcue test`, hierarchie), raport w podsumowaniu joba, artefakt `demo-android-flows`;
  - `ios-build`: tylko `workflow_dispatch` z `ios=true`, `macos-26`, workload `maui-ios`, build `iossimulator-arm64` bez podpisu.
- **Odkrycie:** w .NET 10 MAUI `AutomationId` na Androidzie to `resource-id` `com.mapcue.demoapp:id/<id>`, nie `content-desc` (jak sugerował kod MAUI z gałęzi `main`, sprawdzony przed implementacją). Pierwszy run (40333292191, selektory tekstowe) dał FAIL wszystkich 5 flow; poprawka `2a08fcc` przeszła. Opis w `tapcue-compatibility.md` §10 i w `expected-graph.md`.

## Pliki / API / migracje

- Nowe:
  - `samples/Directory.Build.props`, `samples/Directory.Packages.props`;
  - `samples/MapCue.DemoApp/`: `MapCue.DemoApp.csproj`, `App.cs`, `MauiProgram.cs`, `AutomationIds.cs`, `DemoData.cs`, `Pages/*.cs` (8 plików), `Platforms/Android/*` (manifest, `MainActivity`, `MainApplication`, `colors.xml`), `Platforms/iOS/*` (`AppDelegate`, `Program`, `Info.plist`), `.tapcue/*.yaml` (5), `expected-graph.md`, `check-consistency.py`, `run-flows.sh`, `recordings/*`;
  - `.github/workflows/demo-app.yml`;
  - `tests/MapCue.Application.Tests/Agent/DemoAppRiskOracleTests.cs`;
  - ten handoff.
- Zmienione: `docs/ROADMAP.md` (statusy 02B i 04, decyzja 1, R-10, macierz platform), `docs/tapcue-compatibility.md` (§10), `README.md` (sekcja „Aplikacja demo”).
- Kod `src/`, `MapCue.sln`, istniejące workflow i TapCue: **bez zmian**. Brak migracji i zmian API.
- Zmiany łamiące: brak.

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| Gate: PR #6 zmergowany, `main` aktualny, CI zielone | GitHub API PR #6 `merged: true` (2026-10-26 09:24), `main` = `2f38076` = HEAD startowy, run 40299299229 `success` | PASS | — |
| Build MapCue | `dotnet build MapCue.sln -c Release` / lokalnie SDK 10.0.113; CI run 40335435627 | PASS | — |
| Testy MapCue (z `DemoAppRiskOracleTests`, 24 przypadki) | `dotnet test MapCue.sln -c Release` / lokalnie i CI run 40335435627 | PASS | — |
| Format | `dotnet format MapCue.sln --verify-no-changes` / lokalnie i CI | PASS | — |
| Test architektury | `MapCue.Architecture.Tests` (17) / lokalnie i CI | PASS | — |
| Mutacja wyroczni (zmieniona klasa `investments.buy`) wykrywana | `dotnet test tests/MapCue.Application.Tests --filter DemoAppRiskOracle` z celowo błędnym wierszem | PASS (2 testy FAIL zgodnie z oczekiwaniem, plik przywrócony) | — |
| Spójność `AutomationId` ↔ flow ↔ `expected-graph.md` (+ mutacje: nieznane id, selektor tekstowy, zły format `id`) | `python3 -I samples/MapCue.DemoApp/check-consistency.py` / lokalnie i CI | PASS | — |
| `tapcue check-syntax` 5 flow | TapCue 0.1.0 lokalnie (Linux, Java 17) i CI | PASS | — |
| Build APK (Release, MAUI workload) | `dotnet workload install maui-android` + `dotnet publish samples/MapCue.DemoApp/MapCue.DemoApp.csproj -f net10.0-android -c Release` / GitHub Actions ubuntu-24.04, run 40335436029 (job ok. 3 min) | **PASS** (`com.mapcue.demoapp-Signed.apk`, 28 641 859 B, SHA-256 `9c22e8fc1345dbbccf5873b1090f53cad8e9a1c0cb57fcba67860bf79fce12dd`) | `demo-apk`, `demo-android-build-log` (binlog) |
| Build APK lokalnie | jw. w sesji agenta | **NOT RUN**: Android SDK niedostępny w sesji (`dl.google.com` 403 przez proxy, R-09); instalacji workloadu nie próbowano, weryfikacja przez CI | — |
| Flow setupu `basic` | `samples/MapCue.DemoApp/run-flows.sh "$TAPCUE" apk/com.mapcue.demoapp-Signed.apk reports/demo emulator-5554` / emulator API 34, run 40335436029 | **PASS** (13 s) | `demo-android-flows` |
| Flow setupu `investor` | jw. | **PASS** (11 s) | jw. |
| Flow setupu `basic` + puste rachunki | jw. | **PASS** (16 s) | jw. |
| Trasa `tour-basic` (modal, cykle Home↔Accounts, Details→Home, back z Details, brak Investments, cykl do wyboru profilu) | jw. | **PASS** (38 s) | jw. |
| Trasa `tour-investor` (Investments, 3 rachunki, potwierdzenie usunięcia anulowane, blokada odporna na `back`) | jw. | **PASS** (26 s) | jw. |
| Pierwszy run z selektorami tekstowymi | run 40333292191 | FAIL (wszystkie flow: `Assertion is false: "screen.profile" is visible`), przyczyna: `AutomationId` = `resource-id`; naprawione w `2a08fcc` | log joba |
| Build iOS (symulator) | `dotnet workload install maui-ios` + `dotnet build samples/MapCue.DemoApp/MapCue.DemoApp.csproj -f net10.0-ios -p:TargetFrameworks=net10.0-ios -c Debug -p:RuntimeIdentifier=iossimulator-arm64` / job `ios-build` (`macos-26`) | **NOT RUN**: job jest tylko `workflow_dispatch`, a `workflow_dispatch` działa dopiero, gdy workflow jest na `main`. Po merge: Actions → „Demo app” → Run workflow, `ios=true` | — |
| Flow na iOS Simulator | — | NOT RUN (poza zakresem 02B; R-01) | — |

## Ograniczenia, ryzyka, otwarte pytania

- **iOS: NOT RUN.** Kryterium: merge PR, ręczne uruchomienie `demo-app.yml` z `ios=true` (płatne minuty macOS). Możliwe ryzyko: wersja Xcode na `macos-26` niezgodna z workloadem `maui-ios` (wtedy błąd walidacji Xcode w logu).
- **Selektory iOS** (`id: "(.*/)?<id>"` na `accessibilityIdentifier`) są niesprawdzone; flow były uruchamiane tylko na Androidzie.
- Wersja workloadu `maui-android` nie jest przypięta (najnowsza dla pasma SDK z `global.json` w chwili runu); zmiana MAUI może zmienić mapowanie `AutomationId` lub drzewo. Wtedy `check-consistency.py` nie wykryje problemu, ale wykryją go flow na emulatorze. Do rozważenia: plik `rollback` workloadu w STEP-12.
- Strona modalna MAUI (What's new, Verify) to na Androidzie osobne okno; normalizacja MapCue nie oznaczy jej jako `modal` (brak roli `dialog` i id `android:id/*`). `AlertDialog` jest oznaczany. Kalibracja w STEP-05.
- `back` na Home/Accounts/Investments/Settings i na wyborze profilu wychodzi z aplikacji (koniec stosu). STEP-05 musi to traktować jako wyjście z `appId`.
- Ładowanie trwa 1500 ms; flow go nie sprawdzają (stan przejściowy, asercja byłaby wyścigiem). Widoczność stanu `loading` sprawdzi STEP-05 stabilizacją.
- APK jest podpisany kluczem debug SDK; to tylko aplikacja testowa, nie do dystrybucji.
- Dane w aplikacji są fikcyjne (nazwy rachunków, maskowane numery `•••• 0001`, kwoty); brak sekretów, sieci i uprawnień.
- Pytanie do Roberta: czy uruchomić `ios-build` po merge (koszt macOS), czy zostawić NOT RUN do STEP-12.

## Stan roadmapy

- STEP-00…04: zmergowane. STEP-02B: PR #7 otwarty. Statusy, decyzja 1, R-10 i macierz platform w `docs/ROADMAP.md` zaktualizowane.
- Następny: STEP-05 (zależy od 02, 02B, 03, 04), po merge tego PR.

## Warunki startu STEP-05

- PR #7 **zmergowany** do `main`, CI (`CI` i `Demo app`) zielone na `main`, lokalny `main` zaktualizowany.
- Z tego etapu STEP-05 bierze:
  - APK: artefakt `demo-apk` z `demo-app.yml` (albo `dotnet publish` z README) i `appId` `com.mapcue.demoapp`;
  - setup profili: `.tapcue/setup-basic.yaml`, `setup-investor.yaml`, `setup-basic-empty.yaml`;
  - wyrocznię `expected-graph.md` (≥ 3 stany do odkrycia: np. Home, Accounts, What's new, Settings; akcje ryzykowne, których polityka nie może wykonać);
  - nagrania `recordings/*.hierarchy.json` do kalibracji normalizacji bez urządzenia;
  - wzorzec joba emulatora z `demo-app.yml` (`android-smoke`).

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-05: Vertical slice crawler.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-05, rejestr ryzyk R-03/R-04 i wspólne DONE), docs/architecture.md (§5, §7, §9.5, §10), ADR-0002, ADR-0004, ADR-0006, docs/tapcue-compatibility.md (§9, §10), docs/handovers/STEP-04.md i docs/handovers/STEP-02B.md, `samples/MapCue.DemoApp/expected-graph.md` oraz kod: `src/MapCue.Application/Agent/` (`AgentInterpreter`, `AgentResponseValidator`, `PreExecutionGuard`, `PolicyGate`), `src/MapCue.Application/Graph/` (`SnapshotNormalizer`, `SnapshotRecorder`, `GraphCommitter`), `src/MapCue.Adapters.Reasoning/`, `src/MapCue.Adapters.TapCue/TapCueDeviceAutomation.cs`, `tests/MapCue.Testing/FakeDevice.cs`, `.github/workflows/demo-app.yml`.
> Zweryfikuj, że PR-02B (<https://github.com/rtnk/MapCue/pull/7>) jest zmergowany, a `main` aktualny i z zielonym CI (`CI` i `Demo app`). Jeśli nie, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-05-vertical-slice` (albo branchem wyznaczonym przez środowisko sesji) i `docs/handovers/STEP-05.md` w tym samym PR. Nie rozpoczynaj STEP-06 i nie merguj PR.
> Zakres (ROADMAP STEP-05): `ExplorationEngine` (preflight → pętla → zakończenie), pętla `AnalyzeState` (przez `AgentInterpreter`) + `ExploreAction` (`PreExecutionGuard` → `IDeviceAutomation.ExecuteAsync` → stabilizacja → obserwacja → `GraphCommitter.CommitAttemptAsync`), prosty frontier w SQLite (bez lease i odzysku), `mapcue explore --config`, `mapcue status`, artefakty prób. Kalibracja normalizacji na `samples/MapCue.DemoApp/recordings/` (R-04; MAUI daje `resource-id` z `AutomationId`, strony modalne MAUI nie mają roli `dialog`).
> Testy: E2E na `FakeDevice` (≥ 5 stanów, cykl, modal), deterministyczność (dwa runy z tym samym ziarnem = ten sam graf), demo na emulatorze z TapCue na `samples/MapCue.DemoApp` (setup przez `.tapcue/setup-basic.yaml`): ≥ 3 stany odkryte z Fake albo RuleBased reasonerem (real LLM opcjonalnie), a akcje ryzykowne z `expected-graph.md` nie są wykonane. Wyniki urządzeniowe raportuj jako PASS, FAIL albo NOT RUN, z komendami.
> DoD: demo odkrywa ≥ 3 stany z realistycznym przebiegiem TapCue; każda krawędź ma dowody before/after; `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` i CI zielone; test architektury przechodzi; handoff STEP-05 zapisany.
