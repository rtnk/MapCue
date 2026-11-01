# Protokół manualny accessibility (`mapcue.a11y-protocol/1`)

TapCue 0.1.0 nie steruje TalkBackiem ani VoiceOverem i nie odczytuje ich fokusu (luka G-05). Klawisz Tab na iOS też nie jest dostępny (G-04). Dlatego poziom `assistiveVerified` (architektura §14.1) daje tylko ręczne sprawdzenie przez operatora według tego protokołu, zapisane komendą `mapcue a11y record`. Nic innego go nie daje: ani drzewo UI (poziom `inspected`), ani próba fokusu klawiatury (poziom `automated`).

## Kiedy

Po `mapcue explore` z sekcją `contracts` i po `mapcue contract build`. Kontrakt wskazuje `screenKey` ekranu, a `mapcue status --profiles` pokazuje kontekst (profil, build, locale). Sprawdzenie dotyczy jednego ekranu w jednym kontekście: ten sam profil, ten sam build aplikacji i to samo urządzenie co eksploracja.

## Warunki

- Tylko profil testowy (`environment: test`) i dane syntetyczne. Na urządzeniu nie ma danych klientów.
- **TalkBack:** fizyczne urządzenie albo emulator Android z włączonym TalkBackiem.
- **VoiceOver:** tylko fizyczny iPhone lub iPad. Na symulatorze VoiceOver nie działa (CAP-0114).
- **Klawiatura:** Android z klawiaturą sprzętową (USB, Bluetooth albo klawiatura hosta emulatora). Klawiatura na iOS jest poza zakresem (`unsupported`, G-04) i `a11y record` odrzuca takie wpisy.
- Wersje urządzenia, systemu i technologii asystującej podajesz w `--device`.

## Kroki (cały ekran: `--property screen`)

1. Otwórz ekran tak, jak robi to recipe kontekstu (setup flow profilu i ścieżka z grafu). Zanotuj, który stan ekranu sprawdzasz.
2. **Czytnik ekranu:** przejdź gestem „następny element” (TalkBack/VoiceOver: przesunięcie w prawo) od pierwszego do ostatniego elementu ekranu. Dla każdego elementu zapisz, co czytnik ogłosił: nazwę, rolę i stan.
3. Sprawdź, czy każdy cel interaktywny z kontraktu (`elements[]` z `keyboardTarget: true`) został osiągnięty i ma zrozumiałą nazwę oraz właściwą rolę. Sprawdź też, czy kolejność jest logiczna.
4. Aktywuj (podwójne stuknięcie) co najmniej jeden bezpieczny element nawigacyjny i sprawdź, że czytnik ogłosił zmianę ekranu. **Nie aktywuj** akcji ryzykownych (usuwanie, płatności, komunikacja, zmiana konta). Polityka MapCue ich nie wykonuje, więc protokół też nie.
5. **Klawiatura (Android):** Tab i Shift+Tab przez cały ekran. Każdy cel musi dostać widoczny fokus, a fokus nie może utknąć (pułapka). Enter albo spacja ma aktywować bezpieczny element.
6. Zapisz transkrypt w pliku tekstowym UTF-8 (najwyżej 1 MB): kroki, ogłoszenia, problemy, wynik. Nie wpisuj danych osobowych ani sekretów. Plik i tak przechodzi redakcję przed zapisem.
7. Wynik:
   - `pass`: wszystkie cele osiągnięte, nazwane, z poprawną rolą, w logicznej kolejności, bez pułapki;
   - `fail` w każdym innym przypadku.

## Zapis

```bash
mapcue a11y record --config mapcue.yaml \
  --screen screen-0c683202ea0c --profile basic \
  --technology talkback --result pass --property screen \
  --evidence talkback-settings.txt \
  --operator qa-1 --device "Pixel 8, Android 15, TalkBack 15.0"
mapcue contract build --config mapcue.yaml      # rekord trafia do kolejnej wersji kontraktu
```

- `--property screen` to sprawdzenie całego ekranu według kroków 1–7. Tylko taki rekord z wynikiem `pass` (i bez żadnego rekordu `fail` dla tej technologii) daje komórce `assistiveVerified` status `pass`.
- Rekordy jednego elementu (`--element <elementKey>`, `--property label|role|focusOrder|reachable|state|announcement`) dokumentują pojedyncze problemy. Wynik `fail` takiego rekordu daje komórce `fail`. Wynik `pass` nie daje komórce `pass`, bo nie obejmuje całego ekranu.
- `--context` wybiera kontekst, gdy profil był eksplorowany z kilkoma buildami albo locale. `--state` zawęża rekord do jednego stanu ekranu.
- Odrzucane są:
  - TalkBack dla iOS, VoiceOver dla Androida, klawiatura dla iOS;
  - nieznany ekran, stan albo kontekst;
  - brak pliku dowodu albo plik, który nie jest tekstem UTF-8.
- `--operator`, `--device` i `--note` są redagowane. Wystarczą inicjały albo rola.

## Co trafia do kontraktu

`accessibility.manualRecords[]` (identyfikator rekordu, technologia, stan, element, właściwość, wynik, kontekst, wersja protokołu, dowód) i komórka `assistiveVerified` z `recordIds`. Dowód (zredagowany transkrypt) jest blobem `OperatorRecord` w workspace.
