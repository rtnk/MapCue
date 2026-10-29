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

## Implementacja w STEP-03

- `Microsoft.Data.Sqlite` z ręcznym SQL, bez Dappera i EF. Mapowanie jest w `src/MapCue.Infrastructure.Sqlite/SqliteRepositories.cs`.
- Migracje `Migrations/NNNN_name.sql` są zasobami osadzonymi. `schema_migrations` przechowuje wersję, nazwę, sumę SHA-256 i datę, a `PRAGMA user_version` wskazuje ostatnią wersję. Baza z nieznaną (nowszą) migracją albo ze zmienioną sumą jest odrzucana (`SchemaMismatchException`).
- Jedno połączenie i transakcje `BEGIN IMMEDIATE` serializowane w procesie. Zagnieżdżanie jest zabronione. Wyjątek albo anulowanie oznacza rollback. Blokada workspace `.mapcue/lock` (`WorkspaceLock`) to wyłączna blokada pliku zwalniana przez system przy śmierci procesu (R-11).
- Identyfikatory obiektów grafu (stan, ekran, krawędź, zadanie, snapshot, dowód, kontekst) pochodzą z treści (hash), więc ten sam wkład daje ten sam graf. Generatora `IIdGenerator` używają tylko zdarzenia outboxa.
- Artefakty: `FileSystemArtifactStore` redaguje tekst (`IRedactor`) przed hashowaniem i zapisem (plik tymczasowy, `fsync`, rename). Binarnie można zapisać tylko `Screenshot`. `mapcue gc` jeszcze nie istnieje.

## Implementacja w STEP-06

- Migracja `0005_leases_recovery`: `frontier_tasks.represented_by`, `task_attempts.run_id`, tabela `device_leases` (klucz `device_id`) i indeks `(status, lease_owner)`.
- Claim to warunkowy `UPDATE … WHERE status = 'Pending'`, więc dwóch właścicieli nie może wziąć tego samego zadania. Idempotencję próby daje `attempt_id` jako `PRIMARY KEY` (plus `UNIQUE(task_id, n)`), a obserwacji: `attempt_id` jako klucz `action_observations`.
- Jeden zapisujący pozostaje założeniem MVP (R-11). Lease mają czas wygaśnięcia, ale przy blokadzie workspace (`WorkspaceLockHeld`) nie trzeba na nie czekać: każdy obcy lease należy do martwego procesu. Wygasanie ma znaczenie dopiero dla przyszłego store z wieloma zapisującymi.
