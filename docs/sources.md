# Źródła STEP-00 (sprawdzone wersje)

| Źródło | Wersja / SHA | Dostęp | Co przeczytano |
|---|---|---|---|
| `rtnk/ToolsIdeasExploration` | `1f7728354da764cf3d6d5d8ff9ad18371b94213f` (`main`, shallow clone 2026-10-25) | prywatne, dostęp przez sesję | `tools/TOOL-009-mobilegraph-explorer-eksploracja-aplikacji.md` (status: propozycja); `wiki/eksploracja-aplikacji/**` (CAP-0104…CAP-0116, komplet 13); `tools/TOOL-008-lokalny-runner-testow-ui-fork-maestro.md`; `tools/decisions/DEC-010` (**zdecydowane**, B), `DEC-013` (**zdecydowane**, C), `DEC-015` (**czeka na Roberta**); `raw/chats/2026-10-24-architektura-eksploratora-aplikacji/transcript.md` (wiadomości 1–4); nagłówki TOOL-006, TOOL-007, TOOL-010 |
| `rtnk/TapCue` | `dbc0f18815fbc8faed27f9d0b09278e256ad549b` (`main` = tag `v0.1.0`, 2026-10-24) | publiczne | `README.md`, `AGENTS.md` (nota forka), `maestro-cli/src/main/java/maestro/cli/{App.kt, command/*, mcp/**}`, `maestro-cli/src/main/resources/mcp/cheat-sheet.md`, `maestro-client/.../TreeNode.kt`, `drivers/AndroidDriver.kt`, `drivers/IOSDriver.kt`, `KeyCode.kt`, `maestro-orchestra-models/.../util/Env.kt`, `docs/handovers/` (lista) |
| Release TapCue `v0.1.0` | `tapcue-0.1.0-macos-arm64.zip`, SHA-256 `e8dd01b061f5fe9d381cf510c65161c7181108833e0c05c757796750ddab6ec1` | publiczne | archiwum pobrane i sprawdzone, smoke (`docs/spikes/tapcue-smoke/README.md`) |
| `MKS-01/appscreenmap` | `250d608ab045a0f1a6bf6dce998398ef0e2bf0f6` (2026-10-17), MIT | publiczne | `README.md`, `docs/ARCHITECTURE.md`, układ repozytorium (ADR-0005) |
| `rtnk/MapCue` (to repo) | `f86e3a7` (`main` przed PR-00) | zapis | stan początkowy: tylko `.claude/settings.json`; brak `AGENTS.md`, CI i kodu |

Nie analizowano kodu DroidBot, GraphDroid, FuncDroid, DroidAgent, Fastbot2 ani GraphWalker. Znane są tylko z transkryptu, który przytacza ich opisy. MAUI DevFlow (`dotnet/maui-labs`) nie był sprawdzany w STEP-00: jest opcjonalny i należy do STEP-11.
