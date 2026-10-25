# ADR-0004: Port `IExplorationReasoner`, wersjonowane schematy i izolacja wykonania agenta

- Status: Proponowany (PR-00). Flagi CLI providerów potwierdza STEP-04 [PoC].
- Data: 2026-10-25
- Źródła: CAP-0108, TOOL-006 (CAP-0068, CAP-0080, CAP-0093), DEC-010 B, TOOL-007

## Kontekst

LLM ma interpretować ekran i proponować akcje, ale nie sterować. TOOL-006 (gateway) nie jest dostępny jako pakiet, a MVP nie może na niego czekać. Testy muszą być deterministyczne. Agent jest niezaufanym procesem.

## Decyzja

1. Port domenowy `IExplorationReasoner.InterpretAsync(AgentExplorationRequest) → ReasonerResult` (odpowiedź, zużycie, referencje surowych danych, status walidacji). Schematy `mapcue.agent-exploration-request/1` i `…-response/1` (JSON Schema 2020-12, `additionalProperties: false`) leżą w `schemas/`. Typy C# są generowane z nich albo testowane na nich.
2. Adaptery: `DeterministicFakeReasoner` (testy), `RuleBasedReasoner` (offline, bez LLM, punkt odniesienia), `CliAgentReasoner` (`claude -p` / `codex exec` ze schematem wyjścia, [PoC STEP-04]), w przyszłości `Tool006GatewayReasoner`. Kopiowanie biblioteki TOOL-006 jest zabronione.
3. Walidacja w MapCue niezależnie od providera: schemat, zgodność `requestId`/`snapshotHash` (stale), semantyka (`elementRef`, `valueRef`), ryzyko `max(agent, reguły)`, jedna naprawa formatu (CAP-0068). Odrzucenia są zapisywane jako dowód.
4. Izolacja: `agent.runtime: container` domyślnie (obraz z CLI agenta, montowany tylko plik żądania (ro), wyjście przez stdout, egress tylko do API LLM). `local` wymaga `agent.allowLocalExecution: true`. Brak fallbacku na host przy awarii kontenera: zadanie przechodzi w `Blocked(ReasonerUnavailable)`. Konfiguracja egress i obrazu może w przyszłości przyjść z TOOL-007. MapCue nie buduje własnej platformy kontenerów.
5. Minimalizacja danych: do agenta trafia znormalizowany, zredagowany snapshot. Screenshot jest domyślnie wyłączony. Nie trafiają sekrety, wartości `inputs`, pełne HTTP.
6. Ciągłość nie zależy od sesji LLM: każde wywołanie jest bezstanowe, a kontekst budowany jest z bazy (CAP-0083, CAP-0109).

## Konsekwencje

- MVP może działać całkowicie bez LLM (RuleBased), a LLM jest ulepszeniem jakości.
- Koszt: utrzymanie schematów i adapterów CLI per provider. Zmiany flag CLI wymagają testów kontraktowych na nagranych wyjściach (CAP-0090).
