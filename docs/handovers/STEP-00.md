# HANDOFF: STEP-00: Discovery & architecture

## Status i identyfikatory

- Status: **DONE** (dokumentacja kompletna). Kontrole na urządzeniach mają status NOT RUN, z przyczyną opisaną niżej.
- Branch: `docs/step-00-discovery`
- PR URL i status: <https://github.com/rtnk/MapCue/pull/2> (open, czeka na review i merge przez Roberta)
- Commit SHA: `96ce8df9dfcaf2ebdd8baf7325714a3e2851856c` (dokumenty). Ten handoff jest w kolejnym commicie na tym samym branchu, więc aktualny head jest w PR.
- Base branch/SHA: `main` @ `f86e3a70d95f7856c35cfbe97db748f5d07718f7`
- Data i środowisko: 2026-10-25. Kontener Linux x86_64 (sesja Claude Code w chmurze), OpenJDK 21.0.12, Node 22.22, Python 3. Brak .NET SDK, `adb`, KVM, macOS i Xcode. Ruch sieciowy przez proxy, `dl.google.com` zablokowany (403).

## Zakres faktycznie wykonany

- Przeczytane źródła (SHA w `docs/sources.md`): TOOL-009, CAP-0104…CAP-0116 (13), TOOL-008, DEC-010 (zdecydowane B), DEC-013 (zdecydowane C), DEC-015 (**czeka na Roberta**, MVP od niego nie zależy), transkrypt 2026-10-24 (wiadomości 1–4), nagłówki TOOL-006/007/010.
- Audyt kodu TapCue `v0.1.0` (`dbc0f18`): MCP (5 narzędzi, sesje, błędy, echo `env_vars`), CLI (`hierarchy`, `test`, `check-syntax`, `--version`, `driver-setup`), model `TreeNode` i mapowanie Android/iOS, stratność `inspect_screen`, klawisze iOS. Wynik: `docs/tapcue-compatibility.md` ze statusami confirmed/inferred/missing/blocked i lukami G-01…G-11.
- Realny smoke TapCue z archiwum Release (SHA zweryfikowane) bez urządzenia: `docs/spikes/tapcue-smoke/`.
- Ocena AppScreenMap (README i ARCHITECTURE przy `250d608`, MIT): budujemy własny silnik i przejmujemy wzorce (ADR-0005). Eksperyment uruchomieniowy: NOT RUN.
- Architektura (`docs/architecture.md`): granice, komponenty i projekty .NET, porty, pętla próby, typy zadań, maszyny stanów zadania i runu, model ScreenGraph, fingerprint i klucze dedupe, statusy weryfikacji, kontrakt agenta v1 (szkic) i walidacja, polityka, budżety, sekrety, granica zaufania, trwałość i artefakty, recovery, ScreenContract i macierz a11y z poziomami dowodu, HTTP (opcjonalnie, bez WSS), testy.
- ADR-0001…0006, ROADMAP z etapami 01–12, proponowany STEP-02B, rejestr ryzyk, macierz platform, `AGENTS.md` z protokołem, `README.md`.

## Pliki / API / migracje

- Nowe: `README.md`, `AGENTS.md`, `docs/ROADMAP.md`, `docs/architecture.md`, `docs/tapcue-compatibility.md`, `docs/sources.md`, `docs/adr/ADR-0001-architecture.md`, `ADR-0002-tapcue-integration.md`, `ADR-0003-storage-sqlite.md`, `ADR-0004-reasoner-port-and-isolation.md`, `ADR-0005-build-vs-appscreenmap.md`, `ADR-0006-screen-identity-and-dedupe.md`, `docs/spikes/tapcue-smoke/{README.md, run-smoke.sh, mcp_smoke.py, single-action.yaml, settings-replay.yaml}`, `docs/handovers/STEP-00.md`.
- API, kod produkcyjny, migracje: brak (zgodnie z zakresem).

## Dowody i testy

| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|
| SHA archiwum TapCue 0.1.0 | `sha256sum -c SHA256SUMS` / Linux | PASS | `docs/spikes/tapcue-smoke/README.md` |
| `tapcue --version`, `list-devices` | `run-smoke.sh <dir>` / Linux, bez urządzenia | PASS | j.w. |
| `check-syntax` poprawny i błędny flow | j.w. | PASS (exit 0 / exit 1) | `single-action.yaml` |
| MCP `initialize`, `tools/list`, `list_devices`, błędy `inspect_screen`/`run` | `python3 -I mcp_smoke.py --tapcue …` / Linux | PASS | j.w. |
| `tapcue hierarchy` bez urządzenia | `timeout 60 tapcue hierarchy --no-reinstall-driver` | FAIL (zawieszenie, exit 124, luka G-10) | j.w. |
| Build TapCue ze źródeł | `./gradlew :maestro-cli:installDist` | BLOCKED (`dl.google.com` 403) | — |
| Smoke Android (snapshot, akcja, hierarchy, test) | `DEVICE_ID=… APP_ID=com.android.settings run-smoke.sh` | NOT RUN (brak emulatora i KVM) | — |
| Smoke iOS | `DEVICE_ID=<udid> APP_ID=com.apple.Preferences run-smoke.sh` / macOS | NOT RUN (brak macOS) | — |
| AppScreenMap uruchomienie | — | NOT RUN (macOS + urządzenie) | ADR-0005 |
| Linki względne w `*.md` | skrypt Python (scratchpad sesji) | PASS (0 zepsutych) | — |
| Składnia Mermaid (5 diagramów) | `mermaid@11.4.1` `mermaid.parse` w Chromium (Playwright) | PASS | — |
| markdownlint | `markdownlint-cli2@0.14.0` (MD013/MD033/MD041/MD036 wyłączone) | PASS (0 błędów) | — |
| `bash -n run-smoke.sh`, `py_compile mcp_smoke.py` | Linux | PASS | — |

## Ograniczenia, ryzyka, otwarte pytania

Fakty:

- TapCue 0.1.0 nie ma MCP do pojedynczej akcji, timeoutu ani anulowania. `inspect_screen` jest stratny. Na iOS `TreeNode` nie ma `class`/`clickable`/`scrollable` i nie obsługuje `pressKey: Tab`. `run` odsyła `env_vars` (w tym `MAESTRO_*`). `serverInfo.version` to na sztywno `1.0.0`. Szczegóły: `docs/tapcue-compatibility.md` §6.
- TapCue nie steruje TalkBack ani VoiceOver. Poziom `assistiveVerified` dla czytników jest dziś możliwy tylko manualnie.

Hipotezy do potwierdzenia ([PoC] w dokumentach):

- Czasy i stabilność sesji MCP oraz kolizja z CLI `hierarchy` (R-03), format `commands.json`, zachowanie okien systemowych w snapshotach (STEP-02).
- Mapowanie MAUI `AutomationId` na `resource-id` na obu platformach (STEP-02B/05).
- Android: czy `focused` odzwierciedla fokus klawiatury po Tab (STEP-08).
- Flagi `claude -p` / `codex exec` do wymuszania schematu (STEP-04).

Ryzyka: R-01 (iOS) jest najwyższe. Najtańszy wczesny test to job `macos-26` w STEP-02, z kryterium odblokowania w ROADMAP.

Breaking changes: brak. Luki bezpieczeństwa: brak w repo, bo nie ma kodu. Wymagania mitigacji G-06 i R-13 są wpisane do STEP-02.

Decyzje do potwierdzenia przez Roberta (ROADMAP, ostatnia sekcja): STEP-02B; pierwszy provider LLM; runner macOS; czy eksperyment AppScreenMap jest wymagany; screenshoty do LLM (domyślnie off). **STEP-01 nie zależy od żadnej z nich.**

## Stan roadmapy

- Zamknięte (po merge PR-00): STEP-00. Wszystkie ADR mają status „Proponowany” i stają się obowiązujące po merge.
- Zmiany względem planu bazowego: nowy STEP-02B (propozycja), iOS smoke przeniesiony do STEP-02, model elementów i wariantów w STEP-03, `RuleBasedReasoner` w STEP-04, `FakeDevice` od STEP-01. Uzasadnienie w `docs/ROADMAP.md`.
- Pozostaje: STEP-01…12.

## Warunki startu STEP-01

- PR #2 (`docs/step-00-discovery`) **zmergowany** do `main`, lokalny `main` zaktualizowany (`git fetch origin main && git checkout main && git pull`).
- .NET 10 SDK (lokalnie albo przynajmniej w CI GitHub `actions/setup-dotnet` z przypiętą wersją). Jeśli instalacja SDK w środowisku agenta jest zablokowana przez sieć, weryfikacja lokalna to NOT RUN, a dowodem jest zielone CI.
- Nie są potrzebne urządzenie, TapCue ani LLM.

## Dokładny prompt dla świeżego agenta

> Jesteś kolejnym agentem MapCue. Masz wykonać wyłącznie STEP-01: Solution foundation.
> Najpierw przeczytaj AGENTS.md, README.md, docs/ROADMAP.md (sekcja STEP-01 i wspólne DONE), docs/architecture.md (§3.1, §4, §10.2–10.3, §16), ADR-0001, ADR-0003, ADR-0004 i docs/handovers/STEP-00.md.
> Zweryfikuj, że PR-00 (<https://github.com/rtnk/MapCue/pull/2>) jest zmergowany, a `main` aktualny. Jeśli nie jest, zatrzymaj się i zgłoś to.
> Następnie przedstaw krótki plan, zaimplementuj, przetestuj, utwórz jeden PR z branchem `feat/step-01-foundation` i `docs/handovers/STEP-01.md` w tym samym PR. Nie rozpoczynaj STEP-02 i nie merguj PR.
> Zakres: rozwiązanie `MapCue.sln` (`net10.0`, `global.json` z przypiętym SDK 10, `Directory.Build.props` z nullable i warnings-as-errors, `Directory.Packages.props`), projekty-szkielety z architektury §3.1 z interfejsami portów z §4 (bez implementacji adapterów), host CLI (System.CommandLine) z komendami `explore|status|blocked|resume|report|export|diff|doctor` (poza `--help`/`--version` zwracają „not implemented” i niezerowy kod), ładowanie i walidację konfiguracji YAML z bezpiecznymi domyślnymi budżetami z §10.2 (brak allowlisty `appIds` albo `environments` jest błędem walidacji; sekrety tylko jako `secret://env/NAME`), logging strukturalny z redaktorem, `IClock`/`IIdGenerator`, minimalne `FakeDevice` i `DeterministicFakeReasoner` w `MapCue.Testing`, projekty testów, test reguł zależności między projektami, `.editorconfig`, `.gitignore` (`.mapcue/`, `bin/`, `obj/`), `.github/workflows/ci.yml` (ubuntu, restore/build/test/`dotnet format --verify-no-changes`, akcje przypięte do SHA, `permissions: contents: read`).
> DoD: CI zielone; `dotnet run --project src/MapCue.Cli -- --help` działa bez urządzenia, TapCue i LLM; testy walidacji konfiguracji i redakcji (sekret nie trafia do logu) przechodzą; handoff STEP-01 zapisany. Wyniki raportuj jako PASS, FAIL albo NOT RUN, z komendami.
