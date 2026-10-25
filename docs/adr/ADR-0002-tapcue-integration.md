# ADR-0002: Integracja z TapCue: MCP jako ścieżka główna, CLI dla pełnej hierarchii i replay

- Status: Proponowany (PR-00). Elementy oznaczone [PoC] potwierdza STEP-02.
- Data: 2026-10-25
- Dowody: `docs/tapcue-compatibility.md`, `docs/spikes/tapcue-smoke/README.md` (TapCue `v0.1.0`, commit `dbc0f18`)

## Kontekst

TapCue 0.1.0 wystawia przez MCP (STDIO) narzędzia `list_devices`, `inspect_screen`, `take_screenshot`, `run`, `cheat_sheet` (confirmed (smoke)), a przez CLI `hierarchy`, `test`, `check-syntax`, `--version`, `driver-setup`. Nie ma API pojedynczej akcji, timeoutu ani anulowania. `inspect_screen` zwraca stratny, kompaktowy JSON. `tapcue hierarchy` zwraca pełny `TreeNode`, ale każde wywołanie to nowy JVM i nowa sesja drivera.

## Rozważane opcje

| Opcja | Plusy | Minusy |
|---|---|---|
| A. Tylko CLI (`hierarchy` + `test` na każdą akcję) | pełna hierarchia, artefakty `test-output-dir` | nowy JVM i sesja drivera na każde wywołanie (wolno), `hierarchy` zawiesza się bez urządzenia, parsowanie tekstu |
| B. Tylko MCP | długo żyjąca sesja per urządzenie, JSON, szybkie wywołania | stratny snapshot (bez `important-for-accessibility`, kontenerów), brak lossless PNG, brak artefaktów per komenda |
| **C. MCP główne + CLI do pełnej hierarchii, replay i walidacji** | szybka eksploracja i pełne dane tam, gdzie potrzebne | dwa kanały, ryzyko kolizji portu drivera przy przełączaniu (R-03) |
| D. Własny driver / klasy wewnętrzne TapCue / `maui-dev-mcp` | pełna kontrola | zabronione przez wymagania (granica TOOL-008, DEC-013) |

## Decyzja

Opcja **C**:

- `IDeviceAutomation` z dwiema implementacjami transportu za jednym portem:
  - `TapCueMcpTransport`: proces `tapcue mcp` jako dziecko MapCue (czyste środowisko, bez dziedziczonych `MAESTRO_*`), jeden na urządzenie i run. Odpowiada za `list_devices`, `inspect_screen` (fidelity `fast`), `take_screenshot`, akcje (`run` z krótkim inline YAML).
  - `TapCueCliRunner`: `--version` (preflight), `check-syntax` (każdy generowany i eksportowany YAML), `hierarchy --no-reinstall-driver` (fidelity `full` dla ScreenContract i a11y), `test --test-output-dir --format junit` (replay recipe, walidacja eksportu).
- **Akcja = jeden krótki flow** generowany z typowanej, zwalidowanej akcji (`appId` z allowlisty, jedna komenda + opcjonalnie `waitForAnimationToEnd`). Żaden tekst od LLM nie trafia do YAML poza escapowanymi wartościami selektora pochodzącymi ze snapshotu.
- **Timeout i anulowanie** robi MapCue: twardy timeout na wywołanie, zabicie procesu (grupy procesów), restart sesji MCP i wynik `OutcomeUnknown`, jeśli akcja mogła zostać wysłana. Atomowości się nie obiecuje.
- **Serializacja per urządzenie** w MapCue, bo TapCue nie blokuje sesji.
- **Wersja**: tylko `tapcue --version`, bo `serverInfo.version` jest na sztywno. Wspierane wersje są przypięte w konfiguracji.
- **Klasyfikacja błędów** po tekście `isError` z tabelą wzorców w testach kontraktowych. Nieznany tekst daje `Unknown`, a nie domysł.
- [PoC STEP-02] Pomiar czasu: `inspect_screen`, `run` (1 akcja), `hierarchy` CLI, `check-syntax`. Sprawdzenie, czy CLI `hierarchy` w trakcie życia sesji MCP na tym samym urządzeniu działa bez reinstalacji drivera i bez zrywania sesji MCP. Jeśli koliduje, fidelity `full` wymaga chwilowego zamknięcia sesji MCP (opisane w adapterze).

## Konsekwencje

- Luki G-01…G-11 (`tapcue-compatibility.md` §6) są obsłużone po stronie MapCue albo oznaczone jako `unsupported`. Proponowane tickety do TapCue tworzy właściciel. MapCue nie implementuje prywatnych obejść.
- Testy kontraktowe na nagranych odpowiedziach (fixtures JSONL per wersja TapCue) są wymagane, zanim adapter dostanie nową wersję TapCue.
