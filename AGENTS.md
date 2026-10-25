# AGENTS.md: MapCue

MapCue to CLI w .NET 10 do deterministycznej eksploracji aplikacji mobilnych przez TapCue (TOOL-009). Zanim zaczniesz pracę, przeczytaj `docs/ROADMAP.md`, `docs/architecture.md`, odpowiednie `docs/adr/*` i ostatni `docs/handovers/STEP-NN.md`. Te dokumenty mają pierwszeństwo przed domysłami.

## Protokół etapu (obowiązuje każdego agenta)

Agent dostaje jawne `STEP=NN` i wykonuje **tylko ten etap**.

1. **Read:** `README.md`, ten plik, `docs/ROADMAP.md`, `docs/architecture.md`, właściwe ADR, `docs/handovers/STEP-(NN-1).md`, aktualny kod.
2. **Gate:** sprawdź, że PR poprzedniego etapu jest zmergowany, a `main` jest aktualny i spełnia zależności z ROADMAP. Jeśli nie, nie zaczynaj. Praca na branchu zależnym tylko za jawną zgodą użytkownika, z zapisanym ryzykiem i target branchem.
3. **Plan:** przed implementacją podaj krótki plan: zakres, pliki, testy, ryzyka, DONE. Istotne odstępstwo od ROADMAP wymaga ADR albo zgody użytkownika.
4. **Implement:** jeden branch, jedna odpowiedzialność. Nie zmieniaj TapCue, nie używaj jego klas wewnętrznych ani `maui-dev-mcp`, nie rozszerzaj po cichu publicznego API.
5. **Verify:** testy unit, kontraktowe i integracyjne, wymagany smoke, build oraz lint/format. Wynik zapisz jako PASS, FAIL albo NOT RUN, z dokładną komendą i środowiskiem. Nie fabrykuj wyników.
6. **Review:** samodzielny przegląd bezpieczeństwa, edge cases, niezawodności, kontraktów i regresji. Popraw istotne problemy.
7. **PR:** commit, push i PR z celami, plikami, dowodami, testami i breaking changes. Jeśli PR nie może powstać, zatrzymaj się z gotowym branchem, SHA i instrukcją. Nie deklaruj sukcesu.
8. **Handoff:** `docs/handovers/STEP-NN.md` w tym samym PR (format niżej) i pełny handoff w odpowiedzi końcowej. Instrukcja dla kolejnego agenta odwołuje się do `main` dopiero po merge.
9. **STOP:** nie zaczynaj następnego etapu. Nie merguj własnego PR bez osobnej, jawnej prośby.

Gdy brakuje dostępu, uprawnień, urządzenia albo sieci: nie obchodź blokady i nie wymyślaj wyników. Opisz blokadę i wykonaj tylko bezpieczne, dostępne działania. Dane dostępowe nigdy nie trafiają do promptów, handoffów, repo ani artefaktów.

## Zasady techniczne (skrót architektury)

- MapCue ma stan, graf, frontier, politykę i raporty. TapCue ma urządzenie (tylko publiczne CLI/MCP, ADR-0002). LLM ma tylko interpretację i nie dostaje narzędzi TapCue (ADR-0004).
- Wynik LLM przechodzi kolejno: JSON Schema, kontrolę stale/semantyki/ryzyka, mapowanie na typowaną akcję, ponowny snapshot i dopiero wtedy TapCue. Swobodny tekst, YAML ani komenda od LLM nigdy nie trafia do wykonania.
- Sekrety są tylko referencjami (`secret://env/NAME`). Redakcja odbywa się przed każdym trwałym zapisem. Proces TapCue startuje z czystym środowiskiem.
- Raporty rozróżniają `discovered / executed / observed / verified / blocked / unknown` oraz poziomy `observed / inferred / verified / unsupported / blocked / notTested`. Accessibility nigdy nie dostaje PASS na podstawie samego drzewa UI.
- Wszystkie timeouty są twarde. `OutcomeUnknown` dla akcji zmieniającej stan nie jest automatycznie ponawiany.
- Determinizm: wstrzykiwane `IClock` i `IIdGenerator`, stabilne sortowanie zadań, brak losowości bez ziarna.
- Modele typów, kod i dokumentacja techniczna mogą być po angielsku. Komunikacja z użytkownikiem jest po polsku.

## Format `docs/handovers/STEP-NN.md`

```markdown
# HANDOFF: STEP-NN: <nazwa>

## Status i identyfikatory
- Status: DONE / PARTIAL / BLOCKED
- Branch:
- PR URL i status (open / merged / not created):
- Commit SHA:
- Base branch/SHA:
- Data i środowisko:

## Zakres faktycznie wykonany
## Pliki / API / migracje
## Dowody i testy
| Kontrola | Komenda / środowisko | Wynik | Artefakt |
|---|---|---|---|

## Ograniczenia, ryzyka, otwarte pytania
## Stan roadmapy
## Warunki startu STEP-(NN+1)
## Dokładny prompt dla świeżego agenta
```
