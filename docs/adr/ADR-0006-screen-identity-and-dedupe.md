# ADR-0006: Tożsamość stanu z drzewa UI (bez hashy pikseli), kontekst poza tożsamością stanu

- Status: Proponowany (PR-00). Progi i reguły normalizacji kalibruje STEP-03 (testy) i STEP-05 (demo app) [PoC].
- Data: 2026-10-25
- Źródła: CAP-0105, CAP-0107, transkrypt wiadomość 2 §4–5; wzorce AppScreenMap (ADR-0005)

## Kontekst

Deduplikacja musi być stabilna mimo dynamicznych danych (saldo, daty, listy), musi rozróżniać istotne stany (modal, pusta lista, błąd, wybrana zakładka) i nie może mieszać profili. Hash screenshotu jest niestabilny i nie rozróżnia semantyki.

## Decyzja

1. Tożsamość stanu = `identityHash` z **znormalizowanego drzewa**: posortowany multizbiór kluczy elementów interaktywnych i kotwic, szablony list z `countBucket`, dyskryminatory `stateKind`. Hash nie zawiera bounds, pikseli ani treści dynamicznych (architektura §7).
2. `LogicalScreen` ⊃ `ScreenState` ⊃ `ScreenVariant(context)`. Tożsamość stanu jest **niezależna od kontekstu** (wspólna mapa profili). Krawędzie, warianty, dowody i zadania są **zawsze per kontekst** (`contextSignature`).
3. Rozpoznawanie jest deterministyczne (dokładny hash, potem Jaccard z progami `τ_state=0,85`, `τ_screen=0,6`, potem `screenHints`). Niejednoznaczność daje `Ambiguous`, bez cichego scalania. Propozycje agenta to hipotezy.
4. Każdy hash ma `fingerprintVersion`. Surowe, zredagowane snapshoty są przechowywane, więc zmiana algorytmu oznacza przeliczenie, nie utratę danych.
5. Klucz zadania: `H(type, fromStateId, actionKey, contextSignature, scenarioId, appBuildId)`, wstawiany z `ON CONFLICT DO NOTHING`.

## Konsekwencje

- Cykl A→B→A nie tworzy nowych zadań, a inny profil tworzy osobny wariant i osobne krawędzie bez mieszania dowodów (wymagania scenariusza akceptacyjnego).
- Aplikacje bez `resourceId` (słabe klucze) dają mniej stabilną tożsamość. Na iOS role są wnioskowane (G-03). Raport pokaże udział słabych kluczy jako metrykę jakości mapy.
