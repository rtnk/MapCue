# ADR-0006: Tożsamość stanu z drzewa UI (bez hashy pikseli), kontekst poza tożsamością stanu

- Status: Proponowany (PR-00). Progi i reguły normalizacji: STEP-03 (testy), STEP-05 (nagrania demo app, fingerprint version 2, `screenHints`), STEP-07 (reguła wariantu profilu, ocena kandydata per kontekst).
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

## Implementacja w STEP-03 (fingerprint version 1)

Reguły są w `src/MapCue.Application/Graph/SnapshotNormalizer.cs`, `TextMasker.cs`, `SystemUiRules.cs` i `ScreenIdentityResolver.cs`. Każda zmiana wyniku wymaga podbicia `SnapshotNormalizer.FingerprintVersion`. Strażnik: przypięte `identityHash` nagranych ekranów Androida (`RecordedSnapshotFingerprintTests`).

Doprecyzowania względem architektury §7 (do kalibracji w STEP-05 na demo app, R-04):

- **System UI** to jawne reguły per platforma (pakiet `resource-id`): `com.android.systemui`, klawiatury Gboard i AOSP. Usuwane jest całe poddrzewo i puste kontenery, które po nim zostają. Klawiatura jest odfiltrowana, ale daje dyskryminator `k:keyboard`. Dla iOS nie ma jeszcze reguł, bo nie ma nagrania iOS (R-01), więc nic nie jest filtrowane.
- **Listy:** dzieci porównywane są jako multizbiór kształtów, nie po sąsiedztwie. Dzięki temu permutacja nie zmienia wyniku. Lista to kontener przewijany z ≥ 2 dziećmi tego samego kształtu (albo bez dzieci, co daje bucket `0`) lub dowolny kontener z ≥ 3. W kontenerze nieprzewijanym wierszami są tylko dzieci o sygnaturze korzenia wierszy (rola, `resource-id`, flagi), np. częściowo widoczny ostatni wiersz. Pozostałe rodzeństwo zachowuje własną tożsamość. W praktyce bucket `1` nie występuje, bo pojedyncze dziecko jest nieodróżnialne od opakowania.
- **Tożsamość** obejmuje elementy interaktywne i kotwice z `resource-id` spoza wierszy list, tokeny list, elementy `selected` i dyskryminatory (`modal`, `loading`, `keyboard`, `empty`). Statyczny tekst bez `resource-id` nie wchodzi do tożsamości (np. powitanie z imieniem), tylko do `elementSetHash` wariantu i do `layoutHash`.
- **Klucz słaby** to ścieżka ról od najbliższego przodka z kluczem, bez indeksów, więc nie zależy od kolejności.
- **Resolver:** podobieństwo to Jaccard multizbiorów tokenów. Rozpoznanie przez podobieństwo (`KnownStateVariant`) zapisuje alias `identityHash → stateId`, więc kolejne rozpoznania tego samego hasha nie zależą od późniejszego wzrostu grafu. `Ambiguous` tworzy nowy stan `provisional` i zdarzenie `state.identity-ambiguous` z kandydatami, bez scalania. `screenHints` i `Transient` (stabilizacja) należą do STEP-05.

## Implementacja w STEP-05 (fingerprint version 2)

Kalibracja na nagraniach `samples/MapCue.DemoApp/recordings/` (Home po `setup-basic` i `setup-investor`, emulator API 34, TapCue 0.1.0 `hierarchy`). Testy: `DemoAppRecordingCalibrationTests`.

- **MAUI na Androidzie:** `AutomationId` to `resource-id` `<appId>:id/<AutomationId>`, więc klucze elementów mają siłę `ResourceId`. W nagraniach Home nie ma słabych kluczy, a usuniętych elementów System UI jest 26.
- **Kontenery frameworka nie są kotwicami tożsamości** (`FrameworkContainerRules`: `android:id/content`, nazwy MAUI `action_bar_root`, `navigation_layout`, `navigationlayout_content`, `nav_host`). W wersji 1 stanowiły 6 z 12 tokenów Home. Zakładka Accounts (inny tytuł, bez przycisków Home, ten sam pasek) miała z Home Jaccard 9/13 ≈ 0,69 ≥ `τ_screen`, więc byłaby stanem ekranu Home. W wersji 2 jest to inny ekran (3/7 ≈ 0,43). Kontenery zostają w zbiorze elementów i w `layoutHash`. Zmiana wymagała podbicia `FingerprintVersion` do 2 i przepięcia hashy nagrań STEP-02.
- **Wariant profilu:** Home `investor` ma jeden token więcej (`tab.investments`). Jaccard 6/7 ≈ 0,857 ≥ `τ_state` (0,85), więc to `KnownStateVariant` stanu Home `basic`, zgodnie z `expected-graph.md`. Margines jest mały: ekran z 2 dodatkowymi elementami profilu byłby nowym stanem tego samego ekranu (przy `screenHints`) albo nowym ekranem (bez nich). Do sprawdzenia w STEP-07.
- **`screenHints`** (`identity.screenHints`, architektura §7.3 krok 3): wzorce `resource-id` elementu, który nazywa ekran. Znacznik snapshotu to zbiór pasujących `resource-id` jego elementów tożsamości. Stany z innym znacznikiem nie są porównywane podobieństwem. Ten sam znacznik przy podobieństwie poniżej `τ_screen` daje nowy stan tego ekranu, a nie nowy ekran. Hashe tożsamości się nie zmieniają, więc hinty nie wymagają nowej wersji. Wzorzec z przekroczonym limitem czasu dopasowania nie tworzy znacznika.
- **Strony modalne MAUI** (What's new, Verify) nie mają roli `dialog` ani `android:id/*`, więc normalizacja nie oznacza ich jako `modal`. Rozróżnia je znacznik `screen.*`. `AlertDialog` (`android:id/alertTitle`, `buttonPanel`) daje `modal`. Hierarchii strony modalnej z emulatora nie ma w nagraniach: dane z joba `android-explore` (artefakt `demo-android-explore`, surowe snapshoty w `workspace/blobs`) służą do dalszej kalibracji.
- **Wyjście z aplikacji** (`back` na ekranie głównym) jest rozpoznawane po pakietach (`AppPresenceDetector`, architektura §5.5), a ekran launchera nie staje się stanem grafu.

## Implementacja w STEP-07 (profile i warianty)

- **Margines Home `investor` (R-04):** Home `investor` ma z Home `basic` Jaccard 6/7 ≈ 0,857, czyli 0,007 nad `τ_state`, więc to wariant przez podobieństwo. Na emulatorze (runy 41204402837 i 41205728559, diagnostyka różnicy tokenów w `compare-expected.py`) Accounts `investor` był jednak osobnym stanem. Powód: `RecyclerView` rachunków (`accounts.list`) nie jest przewijany, a wersja 2 uznawała kontener nieprzewijany za listę dopiero od 3 powtórzonych dzieci. 2 rachunki (`basic`) dawały więc 2 tokeny `i:label:button:… · ‹amount›`, a 3 (`investor`) token `l:id:…accounts.list|…|many`. Razem z `tab.investments` różnica wynosiła 4 tokeny (Jaccard 4/10).
- **Decyzja: fingerprint w wersji 3.**
  - Widżet listy (rola `list`: `RecyclerView`, `ListView`, `CollectionView`, `ScrollView`…) ma reguły kontenera przewijanego niezależnie od flagi `scrollable`: ≥ 2 powtórzone dzieci albo brak dzieci (`countBucket` 0), a wszystkie dzieci są wierszami.
  - Kontener wykrytej listy nie jest tokenem interaktywnym (`i:`), tylko kotwicą z `resource-id`, bo jego przewijalność zależy od liczby wierszy na ekranie.
  - Zwykły kontener nadal potrzebuje 3 powtórzeń, więc dwa podobne przyciski nie są listą.
  - Zmieniły się przypięte hashe nagrań Settings i launchera (kontenery list).
  - Stany zapisane w wersji 2 nie są porównywane z nowymi, więc istniejący workspace zaczyna nowy graf. Surowe snapshoty zostają do przeliczenia (decyzja 4).
- **Decyzja: reguła wariantu profilu** (zabezpieczenie marginesu, niezależne od wersji 3). Snapshot jest wariantem znanego stanu (`KnownStateVariant`, `profileVariantDelta`), gdy spełnia wszystkie warunki:
  - z podobieństwa wyszedłby nowy stan ekranu;
  - ma ten sam znacznik `screenHints` i te same dyskryminatory `stateKind`;
  - różni się o ≤ `identity.profileVariantMaxDelta` tokenów (multizbiór, domyślnie 2);
  - znany stan nie ma jeszcze wariantu w kontekście snapshotu.

  Uzasadnienie: profil pokazuje albo ukrywa kilka elementów na wspólnym ekranie (przy 2 elementach profilu Jaccard Home spadłby do 6/8 = 0,75), a w jednym kontekście inny zbiór elementów to nadal inny stan. Reguła nie zależy od kolejności profili (subset i superset liczą się tak samo) i nie zmienia hashy, więc nie wymaga nowej `fingerprintVersion`. Zapisuje alias jak każdy `KnownStateVariant`. Bez `screenHints` nie działa (brak znacznika ekranu = brak bezpiecznej kotwicy). Odrzucone alternatywy:
  - obniżenie `τ_state` (scalałoby stany w jednym kontekście);
  - większy limit reguły zamiast wersji 3 (różnica 4 tokenów z danych scalałaby zbyt wiele);
  - wyłączenie tokenów capability z tożsamości (wymagałoby listy elementów profilu i ukrywałoby różnice).
- **Ryzyko i status kandydata są per kontekst** (`candidate_assessments`). Kandydat `(stateId, actionKey)` pozostaje wspólny jak stan. Pierwszy zapis kandydata nie decyduje już o ryzyku w innym kontekście: przycisk `profile.investor` jest `accountChange` w `basic` i `navigational` w `investor`.
- **Konsekwencja:** na `FakeDemoProfiles` (z `RecyclerView` jak na emulatorze) (i w wyroczni `compare-expected.py`) jeden stan odpowiada jednemu znacznikowi ekranu, a Investments jest jedynym stanem tylko w `investor`. Dowody, snapshoty, obserwacje i krawędzie są zawsze w swoim kontekście.
