# ADR-0003: SQLite jako źródło prawdy, artefakty content-addressed, JSONL jako eksport

- Status: Proponowany (PR-00)
- Data: 2026-10-25
- Źródła: TOOL-009 („SQLite z JSONL i artefaktami”), CAP-0104 (jedna transakcja: graf + koniec zadania + frontier), CAP-0105 (wariant PostgreSQL z raportu DEC-015)

## Kontekst

Graf, frontier i stan prób muszą być aktualizowane atomowo i przetrwać zabicie procesu. MVP działa na jednym hoście, w trybie local-first i offline. Raport do DEC-015 rozważa PostgreSQL ze względu na rozproszone lease, ale ta decyzja nie zapadła.

## Opcje

| Opcja | Plusy | Minusy |
|---|---|---|
| **SQLite (WAL) + `Microsoft.Data.Sqlite` + własne migracje SQL** | zero infrastruktury, transakcje ACID, przenośny plik, offline | jeden zapisujący, brak rozproszonego lease |
| SQLite + EF Core | migracje generowane, LINQ | cięższy, trudniejsza kontrola SQL `ON CONFLICT` i transakcji, wolniejszy start CLI |
| PostgreSQL | lease rozproszony, `SKIP LOCKED` | wymaga serwera, sprzeczne z local-first MVP |
| Pliki JSON/JSONL jako źródło prawdy | czytelne | brak atomowości wielu obiektów, trudny dedupe |

## Decyzja

- **SQLite w trybie WAL**, `Microsoft.Data.Sqlite`, ręczny SQL w repozytoriach, numerowane migracje (`NNNN_name.sql`) z tabelą `schema_migrations` i testem „migracja od zera = migracja przyrostowa”. Ewentualny lekki mapper (np. Dapper) rozstrzyga STEP-03, bez zmiany tej decyzji.
- `busy_timeout`, `foreign_keys=ON`, `synchronous=NORMAL` (WAL). Zapis tylko przez jeden proces MapCue (blokada `.mapcue/lock`).
- Commit próby to **jedna transakcja** (architektura §12), z outboxem zdarzeń w tabeli `events`. `events.jsonl` i `graph.json` to eksport, nie źródło prawdy.
- Artefakty są **content-addressed** (`blobs/sha256/…`), zapisywane i `fsync`-owane przed commitem. Baza trzyma referencje i hash. `mapcue gc` usuwa bloby bez odwołań.
- Kontrakty JSON (agent, ScreenContract, manifest runu) mają własne `schemaVersion`, niezależne od wersji schematu bazy.

## Konsekwencje

- Brak HA i pracy wielu hostów (świadomie, ADR-0001). Port `IStateStore` pozwala później dodać adapter PostgreSQL lub TOOL-010.
- Testy crash-recovery muszą zabijać proces w każdej fazie próby (STEP-06/09).
