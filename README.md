# MapCue

MapCue (nazwa robocza, TOOL-009 „MobileGraph Explorer”) to planowane narzędzie CLI w .NET 10. Ma deterministycznie eksplorować aplikacje mobilne (Android/iOS) przez [TapCue](https://github.com/rtnk/TapCue). Buduje trwały graf ekranów, stanów i wariantów profili, kontrakty ekranów z macierzą dostępności, a zweryfikowane ścieżki eksportuje jako flow YAML TapCue.

**Stan:** STEP-01 (fundament rozwiązania). Jest szkielet solucji .NET 10, porty, walidacja konfiguracji, redakcja logów i CLI z komendami-zaślepkami. Eksploracja jeszcze nie działa (od STEP-05).

## Szybki start (bez urządzenia, TapCue i LLM)

Wymagany .NET SDK 10 (wersja w `global.json`).

```bash
dotnet build MapCue.sln
dotnet test MapCue.sln
dotnet format MapCue.sln --verify-no-changes
dotnet run --project src/MapCue.Cli -- --help
dotnet run --project src/MapCue.Cli -- explore --config docs/examples/mapcue.example.yaml   # waliduje konfigurację, potem „not implemented”
```

Kody wyjścia `mapcue`: `0` sukces, `1` błędna linia poleceń, `2` błędna konfiguracja, `3` komenda jeszcze niezaimplementowana. Przykład konfiguracji: [`docs/examples/mapcue.example.yaml`](docs/examples/mapcue.example.yaml). Logi to JSON lines na stderr, zawsze po redakcji; poziom ustawia `MAPCUE_LOG_LEVEL` (domyślnie `Warning`).

## Dokumentacja

- Plan i statusy etapów: [`docs/ROADMAP.md`](docs/ROADMAP.md)
- Architektura: [`docs/architecture.md`](docs/architecture.md)
- Interfejs TapCue i luki: [`docs/tapcue-compatibility.md`](docs/tapcue-compatibility.md)
- Decyzje: [`docs/adr/`](docs/adr/)
- Handoffy etapów: [`docs/handovers/`](docs/handovers/)
- Zasady pracy agentów: [`AGENTS.md`](AGENTS.md)
