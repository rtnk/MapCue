# ADR-0005: Budujemy własny silnik; AppScreenMap jako źródło wzorców, nie zależność

- Status: Proponowany (PR-00). Eksperyment uruchomieniowy AppScreenMap: **NOT RUN** (brak macOS i urządzenia w sesji).
- Data: 2026-10-25
- Źródła: TOOL-009 „Otwarte: Etap 0: zbudować, zaadaptować czy użyć AppScreenMap”, transkrypt wiadomość 2 (etap 0), repozytorium `https://github.com/MKS-01/appscreenmap` (commit `250d608ab045a0f1a6bf6dce998398ef0e2bf0f6`, 2026-10-17, licencja MIT)

## Ustalenia z legalnie dostępnych źródeł (README, `docs/ARCHITECTURE.md`)

| Cecha | AppScreenMap | Wymaganie MapCue |
|---|---|---|
| Stos | Python (uv) + Bun/TypeScript (dashboard), macOS | .NET 10 |
| Sterowanie | bezpośrednio `adb` / `simctl` + `idb` | **wyłącznie TapCue** (TOOL-008, DEC-013) |
| Eksploracja | deterministyczna, bez LLM; fingerprint ignorujący treść list i cyfry, scalanie przez podobieństwo; priorytet akcji (bottom nav → tabs → buttons → scroll → rows → toggles → back) | deterministyczny orkiestrator + LLM do interpretacji |
| Profile klientów, warianty, guardy | nie potwierdzono | P0 (CAP-0106) |
| Trwała kolejka, wznawianie, `resume.md` | nie potwierdzono (run folder z `map.json`, `trace.jsonl`) | P0 (CAP-0109) |
| Eksport Maestro | tak (`flows/sNNN.yaml`) | tak (tylko zweryfikowane) |
| Accessibility | raport WCAG 2.2 z drzewa | macierz VO/TB/klawiatura z poziomami dowodu (CAP-0114) |
| Bezpieczeństwo | lista niebezpiecznych słów w 24 językach, pinning aplikacji na Androidzie | allowlisty + klasy ryzyka |
| Status | „Early alpha”, iOS na symulatorze | — |

## Decyzja

- **Build**: własny silnik w .NET. Bez zależności runtime od AppScreenMap. Bez reuse kodu: inny język i inna warstwa sterowania, która łamałaby granicę TapCue.
- **Borrow ideas** (z atrybucją w dokumentacji, MIT pozwala na reuse): maskowanie cyfr i treści list w fingerprincie, uczenie się szablonów wierszy list, priorytety akcji nawigacyjnych, wielojęzyczne słowa ryzyka, wykrywanie wyjścia z aplikacji i relaunch, kontrakt pliku mapy z wersją.
- Opcjonalny eksperyment porównawczy (P1, nie blokuje roadmapy): uruchomić AppScreenMap na demo app w STEP-05 lub STEP-10 i porównać liczbę stanów i jakość fingerprintu. Wynik idzie do raportu, bez zmiany tej decyzji, chyba że różnice będą istotne.

## Konsekwencje

- Pytanie „Etap 0” z TOOL-009 jest rozstrzygnięte analizą architektury, nie eksperymentem. Eksperyment jest opcjonalny. Jeśli Robert wymaga eksperymentu przed STEP-03, trzeba dodać zadanie z hostem macOS i urządzeniem.
- DroidBot, GraphDroid, FuncDroid, Fastbot2, GraphWalker: tylko koncepcje (transkrypt). Nie analizowano kodu, nie wprowadzono zależności.
