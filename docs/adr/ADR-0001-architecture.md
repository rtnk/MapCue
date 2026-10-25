# ADR-0001: Architektura i granice odpowiedzialności MapCue

- Status: Proponowany (PR-00). Zatwierdzenie następuje przez merge PR-00.
- Data: 2026-10-25
- Źródła: TOOL-009, CAP-0104, CAP-0108, CAP-0109, DEC-010 (zdecydowane B), DEC-013 (zdecydowane C), DEC-015 (**czeka na Roberta**, nie wiąże MapCue)

## Kontekst

TOOL-009 wymaga deterministycznego programu, który prowadzi eksplorację, utrzymuje graf, wznawia pracę i zleca agentom tylko interpretację ekranów. TapCue (TOOL-008) jest osobnym narzędziem z publicznym CLI i MCP. TOOL-006 (gateway LLM) i TOOL-010 (trwały rdzeń workflow) nie są jeszcze dostępne jako biblioteki. DEC-015 nie jest zatwierdzone.

## Decyzja

1. MapCue to osobne CLI w .NET 10 z warstwami `Domain → Application → Adapters/Infrastructure → Cli` (architektura §3.1).
2. **MapCue ma** stan, graf, frontier, politykę, budżety, checkpointy i raporty. **TapCue ma** sterowanie urządzeniem, wyłącznie przez publiczne CLI/MCP (ADR-0002). **Reasoner (LLM)** dostaje tylko zredagowane dane i zwraca JSON zgodny ze schematem. Nie ma dostępu do urządzenia ani narzędzi TapCue (zgodnie z duchem DEC-013: „agent nie dostaje `run` bezpośrednio”).
3. Stan zadań eksploracji należy do MapCue (DEC-010 B). Biblioteka LLM (dziś adapter CLI, w przyszłości TOOL-006) zwraca tylko identyfikatory wywołania, liczbę prób i status.
4. ScreenGraph jest odrębny od jakiegokolwiek grafu zadań i workflow. MVP **nie zależy** od Netflix Maestro, JVM workflow ani TOOL-010 (DEC-015 otwarte). Port `IStateStore` pozwala w przyszłości wynieść kolejkę zadań do TOOL-010 bez zmiany modelu grafu.
5. Jeden wykonawca UI na urządzenie, jedna instancja MapCue na workspace (MVP na jednym hoście).
6. Agent działa domyślnie w izolowanym kontenerze. Lokalnie tylko przy jawnej konfiguracji, bez automatycznego fallbacku (ADR-0004). Sterowanie iOS wymaga hosta macOS. Zdalny broker urządzeń to przyszły adapter, nie część MVP.

## Konsekwencje

- Na plus: jasna granica zaufania; testy całego silnika bez urządzenia (FakeDevice) i bez LLM (Fake/RuleBased reasoner); zgodność z DEC-010 i DEC-013.
- Na minus: pojedynczy host i proces (brak HA). To świadomy wybór dla MVP (por. pytanie 1 w DEC-015). Dwa adaptery TapCue (MCP i CLI), bo MCP nie daje pełnej hierarchii (ADR-0002).
- Do zmiany decyzji potrzebne będą: zatwierdzenie DEC-015 z wariantem wymagającym HA albo pojawienie się TOOL-006 i TOOL-010 jako bibliotek. Wtedy powstaje nowy ADR i adaptery za istniejącymi portami.
