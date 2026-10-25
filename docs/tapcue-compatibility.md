# TapCue — kompatybilność i publiczny interfejs dla MapCue

Status dokumentu: STEP-00 (audyt kodu, smoke bez urządzenia), uzupełniony w STEP-02 wynikami smoke na emulatorze Androida (§7, §9). Pozycje bez **confirmed (smoke)** nadal wymagają potwierdzenia.

## 1. Sprawdzana wersja

| Pozycja | Wartość |
|---|---|
| Repozytorium | `https://github.com/rtnk/TapCue` (publiczne, Apache-2.0, fork Maestro 2.11.0) |
| Commit audytu | `dbc0f18815fbc8faed27f9d0b09278e256ad549b` (`main`, 2026-10-24, to ten sam commit co tag `v0.1.0`) |
| Wersja CLI | `CLI_VERSION=0.1.0` (`maestro-cli/gradle.properties`) |
| Release | `v0.1.0`: `tapcue-0.1.0-macos-arm64.zip` + `SHA256SUMS`, SHA-256 `e8dd01b0…6ec1` (zweryfikowane) |
| Runtime | Java 17+ na `PATH`/`JAVA_HOME`; Android: `adb` + emulator/urządzenie; iOS: macOS arm64 + Xcode, tylko symulator |
| Stan i artefakty | `~/.tapcue` albo `$XDG_STATE_HOME/tapcue` (`maestro.utils.TapCueDirs`) |

Legenda statusów:

- **confirmed (smoke)**: wykonane w tej sesji, wynik w `docs/spikes/tapcue-smoke/README.md`;
- **confirmed (code)**: wynika wprost z kodu przy wskazanym SHA, ale nie zostało uruchomione;
- **inferred**: wniosek z kodu albo dokumentacji, który trzeba potwierdzić na urządzeniu;
- **missing**: TapCue tego nie ma;
- **blocked**: nie dało się sprawdzić w tej sesji.

Ścieżki do kodu są względne wobec katalogu głównego TapCue przy tym SHA. Skrót `cli/` oznacza `maestro-cli/src/main/java/maestro/cli/`.

## 2. Powierzchnie integracji

### 2.1 MCP (`tapcue mcp`, STDIO)

| Operacja | Interfejs | Wejście | Wynik | Status | Źródło |
|---|---|---|---|---|---|
| Handshake | `initialize` | protokół `2025-06-18` | `serverInfo {name:"tapcue", version:"1.0.0"}`, `tools.listChanged=true` | confirmed (smoke) | `cli/mcp/McpServer.kt:64-75` |
| Lista narzędzi | `tools/list` | — | 5 narzędzi (niżej) | confirmed (smoke) | `cli/mcp/McpServer.kt:77-83` |
| Urządzenia | `list_devices` | — | `{"devices":[{device_id,name,platform,type,connected}]}` | confirmed (smoke, pusta lista) | `cli/mcp/tools/ListDevicesTool.kt` |
| Snapshot UI | `inspect_screen` | `device_id` | kompaktowy JSON `{ui_schema, elements}` (szczegóły w §3) | confirmed (code); smoke tylko ścieżki błędu | `cli/mcp/tools/InspectScreenTool.kt:51-57`, `ViewHierarchyFormatters.kt:283-420` |
| Screenshot | `take_screenshot` | `device_id` | `ImageContent` JPEG (q=0.9), dłuższy bok ≤ 2000 px | confirmed (code) | `cli/mcp/tools/TakeScreenshotTool.kt:21,97` |
| Akcja / flow | `run` | `device_id` + dokładnie jedno z `yaml` / `files` / `dir`, opcjonalnie `env`, tagi | inline: `{success, device_id, commands_executed, message[, env_vars]}`; błąd: `isError=true` + tekst `Failed to run flow: <msg>` | confirmed (smoke, walidacja argumentów); wykonanie: confirmed (code) | `cli/mcp/tools/RunTool.kt` |
| Składnia flow | `cheat_sheet` | — | markdown dołączony do JAR | confirmed (code) | `maestro-cli/src/main/resources/mcp/cheat-sheet.md` |

Zachowanie potwierdzone kodem:

- **Sesja na urządzenie jest trzymana w pamięci** (`computeIfAbsent`, `cli/mcp/McpMaestroSessionManager.kt:35`). Długo żyjący proces MCP nie instaluje drivera przy każdym wywołaniu. Pierwsze wywołanie instaluje driver (`reinstallDriver = true`, `:119`, `:132`).
- **Brak blokady na urządzenie.** `withSession` nie serializuje wywołań. MapCue musi mieć własną wyłączną dzierżawę urządzenia (jeden wykonawca UI na urządzenie).
- **Wspierane urządzenia MCP:** emulator i fizyczne urządzenie Android; tylko symulator iOS (`isSupported`, `:68-72`). Fizyczny iOS: **missing**.
- **Błędy są wynikami narzędzia** (`isError=true` + tekst), nie błędami JSON-RPC (confirmed (smoke)). Klasyfikacja błędów w adapterze opiera się na tekście, więc jest krucha. Wzorce trzeba zapisać w testach kontraktowych.
- **`run` inline** zapisuje YAML do pliku tymczasowego i wykonuje go przez `Orchestra.runFlow` (`RunTool.kt:159`). Sekcja config jest opcjonalna (cheat sheet). Bez `appId` nie zadziała `launchApp`.
- **`run` odsyła zmienne środowiskowe** w polu `env_vars`: przekazane `env` oraz wszystkie zmienne powłoki `MAESTRO_*` procesu TapCue (`RunTool.kt:171,228`, `Env.kt:107-114`). Sekrety przekazane przez `env` wracają w wyniku. MapCue musi redagować wynik przed zapisem i uruchamiać proces TapCue z oczyszczonym środowiskiem.
- **Brak parametrów timeout i anulowania** w narzędziach. Anulowania MCP (`notifications/cancelled`) nie sprawdzono: **inferred: nieobsługiwane**, bo handler wykonuje `runBlocking`. Jedyny pewny sposób przerwania to zabicie procesu MCP. Wtedy skutek akcji jest nieznany.
- **Wynik `run` nie ma danych per komenda** (czasów, stanu). Dla dowodów per akcja MapCue i tak wykonuje jedną akcję na wywołanie i robi własny snapshot przed akcją i po niej.
- Wersja w `serverInfo` jest na sztywno `1.0.0` (`McpServer.kt:67`), więc **nie nadaje się do sprawdzania kompatybilności**. Wersję czyta się z `tapcue --version`.

### 2.2 CLI (`tapcue`)

| Komenda | Użycie w MapCue | Status | Uwagi |
|---|---|---|---|
| `tapcue --version` | preflight, przypięcie wersji | confirmed (smoke) | wyjście `tapcue 0.1.0` + linia atrybucji |
| `tapcue list-devices [--platform]` | diagnostyka | confirmed (smoke) | wyjście tekstowe (tabela), **nie JSON**. Do automatyzacji służy MCP `list_devices`. |
| `tapcue check-syntax <flow>` | walidacja każdego generowanego i eksportowanego YAML | confirmed (smoke) | exit 0 `OK` / exit 1 + komunikat z pozycją |
| `tapcue [--device <id>] hierarchy [--no-reinstall-driver] [--compact]` | pełna hierarchia (`TreeNode` JSON) dla ScreenContract i accessibility | confirmed (code) | Domyślnie `--reinstall-driver=true` (`cli/command/PrintHierarchyCommand.kt:71`): **zawsze podawać `--no-reinstall-driver`**. Każde wywołanie to nowy JVM i nowa sesja drivera. Bez urządzenia **zawiesza się** (smoke: brak wyniku po 60 s). |
| `tapcue [--device <id>] test [--no-reinstall-driver] --format junit --output <f> --test-output-dir <dir> [-e K=V] <flow>` | replay `ReachabilityRecipe`, walidacja eksportowanych flow | confirmed (smoke, emulator API 34) | Artefakty lądują w **podkatalogu** `<dir>/<timestamp>/<nazwa flow>/`: `manifest.json`, `commands.json`, `logs/maestro.log`, `logs/device-logcat.txt`, `takeScreenshot/*.png`; `--output` daje `report.xml`. Format `commands.json`: nadal nie analizowany (STEP-10). **Zrywa połączenie z driverem otwartej sesji MCP** (R-03, §9). |
| `tapcue driver-setup --simulator` | preflight iOS (zbudowanie drivera XCTest przed eksploracją) | confirmed (code) | buduje runner ze źródeł w JAR przy użyciu lokalnego Xcode |
| `tapcue start-device`, `record`, `query`, `chat`, `bugreport` | nieużywane | — | `chat` jest ukryty i zależny od API. MapCue go nie używa. |

### 2.3 Format flow YAML

Format Maestro bez zmian, sekcja `appId:` i `---`. Komendy istotne dla MapCue (confirmed (code), cheat sheet): `launchApp` (`clearState`, `arguments`), `stopApp`, `killApp`, `clearState`, `tapOn`/`doubleTapOn`/`longPressOn` (selektory `text` jako regex całego tekstu, `id`, `index`, `below/above/leftOf/rightOf`, `containsChild`, `enabled/checked/focused/selected`, `point`), `inputText`, `eraseText`, `hideKeyboard`, `pressKey`, `back`, `scroll`, `swipe`, `scrollUntilVisible`, `assertVisible`, `assertNotVisible`, `extendedWaitUntil`, `waitForAnimationToEnd`, `takeScreenshot`, `openLink`, `setPermissions`.

Komendy, które polityka MapCue **zabrania** w generowanym YAML: `runScript`, `evalScript`, `assertTrue` z JS, `runFlow` (poza eksportem z własnych plików), `openLink` do zewnętrznych schematów, `setAirplaneMode`, `clearKeychain`, `addMedia`, `travel`, `setLocation` (chyba że profil zezwala). Uzasadnienie: JavaScript w flow ma dostęp do `http.*` na adresach lokalnych i prywatnych (README TapCue), a pozostałe zmieniają stan urządzenia.

## 3. Snapshot UI: co naprawdę dostajemy

### 3.1 Model źródłowy (`maestro.TreeNode`)

`attributes: Map<String,String>`, `children`, `clickable`, `enabled`, `focused`, `checked`, `selected` (`maestro-client/src/main/java/maestro/TreeNode.kt`).

| Atrybut | Android (`AndroidDriver.kt:1150-1235`) | iOS (`IOSDriver.kt:211-238`) |
|---|---|---|
| `text` | `text` | `title` albo `value` |
| `accessibilityText` | `content-desc` | `label` |
| `resource-id` | `resource-id` | `identifier` (MAUI `AutomationId`: inferred) |
| `hintText` | `hintText` | `placeholderValue` |
| `bounds` | `[x1,y1][x2,y2]` | `frame` |
| `class` | klasa widoku | **missing** (typ elementu XCUI nie trafia do `TreeNode`) |
| `value`, `title` | — | tak |
| `clickable` | tak | **missing** |
| `scrollable` | tak | **missing** |
| `important-for-accessibility` | tak | — |
| `error` | tak | — |
| `checked` | tak | tylko dla typów checkable (`value == "1"`) |
| `focused` | fokus wejścia (nie fokus TalkBack: inferred) | `hasFocus` |

### 3.2 `inspect_screen` (MCP) jest stratny

`ViewHierarchyFormatters.convertToCompactNode` zachowuje tylko `b, a11y, txt, val, rid, cls, hint, scroll, clickable, checked, enabled, focused, selected`. Węzły o zerowym rozmiarze i kontenery bez treści są **usuwane** z drzewa, a ich dzieci podpinane wyżej (`:283-300`). Tracimy przez to:

- `important-for-accessibility`, `error`, `title` (iOS) i grupowanie kontenerów;
- informację potrzebną do kolejności fokusu i grupowania elementów dla czytników ekranu.

Wniosek dla MapCue (ADR-0002): do **eksploracji** (szybka obserwacja, identyfikacja stanu) służy `inspect_screen`. Do **ScreenContract i accessibility** służy pełne `tapcue hierarchy --no-reinstall-driver` w osobnym, rzadszym zadaniu `AnalyzeScreen`/`AuditAccessibility`. Przełączanie MCP i CLI na tym samym urządzeniu: wynik PoC R-03 w §9.

## 4. Akcje i stabilizacja

- Nie ma jednoakcyjnego API. Każda akcja to krótki flow YAML przez MCP `run` (confirmed (code)). Adapter generuje YAML **wyłącznie z typowanej, zwalidowanej akcji** i nigdy nie przekazuje tekstu od LLM (architektura §7).
- Selektor: preferowany `id` (`resource-id`). Potem pełny tekst jako zakotwiczony regex z escapowaniem znaków specjalnych, potem selektor względny. `point` tylko z jawną polityką. Tekstowy matcher ignoruje wielkość liter i musi pasować do całego tekstu (cheat sheet).
- Orchestra po komendach czeka na ustabilizowanie UI (zachowanie Maestro: inferred). MapCue dokłada własną politykę stabilizacji: dwa kolejne snapshoty o tym samym odcisku struktury w oknie `uiSettleTimeout`.
- **Atomowość: brak.** `run` może przerwać się po częściowym wykonaniu. Timeout lub zabicie procesu daje wynik `OutcomeUnknown`. Akcji ryzykownych nie ponawia się automatycznie (ADR-0001, architektura §8).

## 5. Platformy

| Obszar | Android | iOS |
|---|---|---|
| Host | macOS arm64 (wydanie). Linux: CLI działa z archiwum (confirmed (smoke) bez urządzenia), oficjalnie nieprzetestowany. | tylko macOS arm64 + Xcode |
| Urządzenia | emulator (CI TapCue: API 34), fizyczne: nieprzetestowane | tylko symulator. Fizyczne: missing w MCP. |
| Driver | APK w JAR | budowany ze źródeł przy pierwszym użyciu (minuty), `driver-setup --simulator` |
| Hierarchia w CI TapCue | flow na emulatorze API 34 (aplikacja Settings) | tylko odczyt hierarchii symulatora, flow na iOS **nie są uruchamiane w CI** |
| Klawiatura (`pressKey: Tab`) | tak (`KeyCode.TAB`) | **missing**: iOS mapuje tylko `Backspace`, `Enter`, `Home`, `Lock` (`IOSDriver.kt:165-187`) |
| Czytnik ekranu (TalkBack/VoiceOver) | **missing**: brak sterowania czytnikiem i odczytu fokusu dostępności | **missing**. Prawdziwy VoiceOver wymaga fizycznego urządzenia (CAP-0114). |
| Web | wyłączony | wyłączony |

## 6. Luki (missing) i propozycje

| ID | Luka | Wpływ na MapCue | Obejście zgodne z zasadami | Proponowany ticket do TapCue |
|---|---|---|---|---|
| G-01 | Brak timeoutu i anulowania w MCP `run` / `inspect_screen` | nie da się przerwać pojedynczej akcji | twardy timeout i zabicie procesu MCP z restartem sesji; wynik `OutcomeUnknown` | parametr `timeout_ms` i obsługa `notifications/cancelled` |
| G-02 | `inspect_screen` stratny (§3.2) | słabszy ScreenContract i a11y | pełne `tapcue hierarchy` przez CLI | opcja `format: full` w `inspect_screen` (lossless `TreeNode`) |
| G-03 | iOS: brak `class`/typu elementu, `clickable`, `scrollable` w `TreeNode` | role i akcje na iOS tylko wnioskowane | heurystyki (traits z etykiety, rozmiar), status `inferred` | dodać `elementType`/traits do mapowania iOS |
| G-04 | iOS: brak `pressKey: Tab` | brak grafu fokusu klawiatury na iOS | `unsupported` w macierzy a11y | mapowanie Tab/Shift+Tab (Full Keyboard Access) |
| G-05 | Brak sterowania TalkBack/VoiceOver | brak poziomu `assistiveVerified` z automatu | protokół manualny operatora lub osobny adapter (P1/P2), zawsze jawny | (poza zakresem TapCue, decyzja w STEP-08) |
| G-06 | `run` odsyła `env_vars` i dziedziczy `MAESTRO_*` | wyciek sekretów do artefaktów | czyste środowisko procesu + redakcja wyniku | opcja wyłączenia echa `env_vars` |
| G-07 | `serverInfo.version` na sztywno `1.0.0` | brak negocjacji wersji przez MCP | `tapcue --version` w preflight | raportować `CLI_VERSION` w `serverInfo` |
| G-08 | Błędy jako tekst (`isError`) | krucha klasyfikacja błędów | tabela wzorców w testach kontraktowych, nieznany tekst → `Unknown` | strukturalny kod błędu w wyniku |
| G-09 | Brak blokady urządzenia | wyścigi przy równoległych klientach | wyłączna dzierżawa urządzenia w MapCue | — |
| G-10 | `hierarchy` CLI bez urządzenia się zawiesza | zawieszony proces | timeout i zabicie drzewa procesów | szybki błąd przy braku urządzenia |
| G-11 | Tylko archiwum macOS arm64 | Linux i x64 bez oficjalnego wsparcia | Android na Linuksie jako „best effort” z archiwum JVM, macOS arm64 referencyjny | archiwum niezależne od platformy |

Żadna luka nie blokuje MVP na Androidzie. G-03, G-04 i G-05 ograniczają accessibility i iOS. Są ujęte w macierzy platform (ROADMAP) i zgłoszone jako ryzyka. Tickety do TapCue tworzy właściciel. MapCue nie modyfikuje TapCue.

## 7. Smoke testy

Odtwarzalne skrypty: `docs/spikes/tapcue-smoke/`.

| ID | Test | Środowisko | Status |
|---|---|---|---|
| S-01 | wersja, `list-devices`, `check-syntax` (poprawny i błędny flow) | Linux x86_64, archiwum v0.1.0 | PASS |
| S-02 | MCP `initialize`, `tools/list`, `list_devices`, ścieżki błędów `inspect_screen` i `run` | j.w. | PASS |
| S-03 | `hierarchy` bez urządzenia kończy się szybkim błędem | j.w. | FAIL (zawieszenie, luka G-10) |
| S-04 | Android: `list_devices`, `run launchApp`, `inspect_screen`, `run back`, `inspect_screen` po akcji, `hierarchy`, `test --test-output-dir` (przez adapter MapCue) | emulator Android API 34 (google_apis, x86_64), GitHub Actions ubuntu-24.04 z KVM | **PASS** (STEP-02, run 40238138686). `take_screenshot`: NOT RUN (adapter go nie używa). |
| S-05 | iOS: to samo na symulatorze z `com.apple.Preferences` | macOS arm64 + Xcode | NOT RUN (STEP-02: job `ios` w `tapcue-smoke.yml`, tylko `workflow_dispatch`; czeka na merge i decyzję o minutach macOS, R-01). |
| S-06 | Build ze źródeł | Linux | BLOCKED (`dl.google.com` 403 w polityce sieci sesji) |

## 8. Polityka wersji

- MapCue przypina wspieraną wersję: `tapcue.supportedVersions: ["0.1.0"]`. Preflight czyta `tapcue --version` i odmawia pracy z inną wersją, chyba że ustawiono jawne `tapcue.allowUntestedVersion: true` (wtedy run dostaje flagę `untestedAdapter`).
- Ścieżka do `tapcue` pochodzi z konfiguracji albo z `PATH`. Brak ścieżek na sztywno.
- Testy kontraktowe STEP-02 używają nagranych odpowiedzi MCP (fixtures JSONL) dla danej wersji. Nowa wersja TapCue wymaga nowych fixtures i przejścia smoke.

## 9. Wyniki STEP-02 (adapter MapCue na emulatorze Androida)

Środowisko: GitHub Actions `ubuntu-24.04` z KVM, emulator API 34 `google_apis` x86_64, TapCue 0.1.0 z archiwum (SHA-256 przypięte), Zulu JDK 17, aplikacja `com.android.settings`. Workflow `.github/workflows/tapcue-smoke.yml`, job `android`: run 40238138686 (commit `a571e15`, pomiar i wykrycie kolizji) oraz run 40239216271 (commit `defec15`, po poprawce R-03; z niego pochodzą nagrane fixtures). Raport: artefakt `android-smoke-reports` (`smoke/report.md`) i podsumowanie jobu. Czasy z dwóch runów podane jako zakres.

### 9.1 Czasy (PoC ADR-0002)

| Operacja | Czas | Uwagi |
|---|---|---|
| `tapcue --version` | 1,1 s | start JVM |
| `tapcue check-syntax` | 1,2–2,3 s | osobny JVM na każde wywołanie; adapter sprawdza każdy kształt szablonu raz (cache po `ShapeKey`) |
| start `tapcue mcp` + `initialize` + `tools/list` + `list_devices` | 3,8–4,9 s | |
| `run` `launchApp` (pierwsze wywołanie na urządzeniu w sesji) | 13,1–20,1 s | zawiera instalację drivera (`reinstallDriver = true`) |
| `inspect_screen` (zimny / ciepły) | 1,0–1,4 s / 0,15–0,8 s | |
| `run` `back` + `waitForAnimationToEnd` | 3,1–4,9 s | |
| CLI `hierarchy --no-reinstall-driver` | 4,7–8,1 s | nowy JVM i sesja drivera |
| CLI `test --test-output-dir` (launchApp + screenshot) | 10,7–18,2 s | |
| restart sesji MCP po zamknięciu + `inspect_screen` | 3,7–7,0 s | tryb `exclusive` |
| `mapcue doctor --device` (całość) | 13,4 s | |

### 9.2 Kolizja MCP i CLI na jednym urządzeniu (R-03)

- `hierarchy --no-reinstall-driver` przy otwartej sesji MCP: **brak kolizji**. Kolejne `inspect_screen` w tej samej sesji działa (bez restartu). Domyślne `tapcue.fullObservation: shared` zostaje.
- `test --no-reinstall-driver` przy otwartej sesji MCP: **kolizja**. Następne `inspect_screen` zwraca `isError`: `Failed to inspect screen: Device server died during 'deviceInfo' on emulator-5554 (...): StatusRuntimeException: UNAVAILABLE`, a TapCue trzyma martwe połączenie w pamięci sesji. Mitigacja w adapterze: `RunFlowAsync` zawsze zamyka sesję MCP przed `tapcue test`; błąd `Device server died` jest klasyfikowany jako `DriverLost` i powoduje restart sesji (wywołania tylko do odczytu są ponawiane w limicie `maxRetries`, akcje nie).
- Po poprawce (run 40239216271) `inspect_screen` po `test` przechodzi: adapter otwiera nową sesję MCP (licznik restartów 1, 7,0 s razem z instalacją drivera).
- Tryb `exclusive` (zamykanie sesji na czas CLI) działa; kosztuje restart sesji (3,7–7,0 s).

### 9.3 Inne obserwacje

- Wynik `run` przy sukcesie: `{"success":true,"device_id":…,"commands_executed":3,"message":"Flow executed successfully"}` (3 komendy: wygenerowany flow ma `launchApp`/`back` i `waitForAnimationToEnd`, Maestro liczy też komendę konfiguracji). Bez `env` nie ma `env_vars` (proces bez `MAESTRO_*`).
- `inspect_screen` na Androidzie zawiera elementy System UI (pasek stanu) przed treścią aplikacji. Normalizacja i odfiltrowanie należą do STEP-03.
- `ForegroundAppId` nie jest raportowany przez TapCue 0.1.0 (ani MCP, ani `hierarchy`).
