# Implementation Plan: Tryb porównywania (Compare Mode)

## Overview

Implementacja rozdziela się na warstwę ustawień, model wieloźródłowy serwera, endpoint porównawczy + payload profilu, shim `platform.js` (klawiatura F8/F9 + snackbar) oraz UI i ścieżkę startu w `MainWindow`. Każdy krok buduje na poprzednim i kończy się integracją — bez wiszącego, niepodpiętego kodu. Logikę .NET weryfikuje xUnit (`dotnet test`), a `platform.js` weryfikuje asercją integracyjną (treść shimu) + demo ręczne, bo repozytorium nie ma runnera JS.

## Tasks

- [x] 1. UserSettings — pola CompareMode i CompareAppB
  - [x] 1.1 Dodaj pola CompareMode (bool) i CompareAppB (string) do UserSettings oraz czyszczenie w Save/Load
    - Rozszerz klasę `UserSettings` o `CompareMode` (domyślnie false) i `CompareAppB` (domyślnie "").
    - W `UserSettingsStore.Save` czyść `CompareAppB` tak jak `DefaultApp` (`.Trim()`, puste → ""); `CompareMode` normalizuj do bool.
    - W `UserSettingsStore.Load` uzupełnij brakujące pola (`CompareMode = false`, `CompareAppB = ""`) dla starych plików, jak dziś robisz to dla `DefaultApp`/`HiddenProfiles`.
    - Nie ruszaj pozostałych pól ani istniejących testów.
    - _Requirements: 8.1, 8.2, 8.4, 8.5_

  - [x]* 1.2 Napisz testy property dla round-tripu ustawień porównawczych
    - **Property 1: Round-trip ustawień porównawczych**
    - **Validates: Requirements 8.1, 8.2, 8.3, 8.5**
    - Testy w `UserSettingsTests.cs`: round-trip `CompareMode` (true/false) i `CompareAppB` (z białymi znakami), domyślne wartości puste/fałsz, brakujące pola w starym JSON → domyślne, pozostałe pola (`DefaultApp`, `HiddenProfiles`) zachowane.
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5_

- [x] 2. AppServer — model wieloźródłowy
  - [x] 2.1 Dodaj pola _sources/_labels/_activeIndex/_pendingNotice oraz StartCompare i SwitchSource
    - Dodaj pola `_sources` (AppSource[]?), `_labels` (string[]?), `_activeIndex` (int), `_pendingNotice` (string?).
    - Dodaj `ApplyActiveSource()`, które ustawia aktywne `_appDirectory`/`_page`/`_origin`/`_startPath` z `_sources[_activeIndex]` (przez `NormalizeStartPath`).
    - Dodaj `StartCompare(root, AppSource a, AppSource b, string labelA, string labelB, profile, pointerEnabled, debug, videoComposition)`: ustaw `_sources`/`_labels`, `_activeIndex = 0`, wywołaj `ApplyActiveSource()` i zainicjuj resztę jak w `Start` (shim, Avplay trace, Bind, wątek).
    - Dodaj `SwitchSource(int index)`: no-op gdy `index == _activeIndex`; w przeciwnym razie ustaw `_activeIndex`, `ApplyActiveSource()`, `_remoteAppInfo = null`.
    - Pozostaw pojedynczy `Start(...)` niezmieniony: ustawia `_sources = null`, `_labels = null`, `_activeIndex = 0`, `_pendingNotice = null` i bezpośrednio pola aktywne, tak by kod istniejący działał jak dotąd.
    - Zintegruj: `Proxy()` i serwowanie lokalne nadal czytają aktywne pola — bez zmian logiki.
    - _Requirements: 4.2, 7.1, 7.3, 7.4_

  - [x]* 2.2 Napisz testy property dla przełączania źródeł
    - **Property 2: SwitchSource przełącza serwowaną treść na źródło docelowe**
    - **Validates: Requirements 4.2, 7.1, 7.3, 7.4**
    - Testy w `EmulatorTests.cs`: dwa upstreamy przez `HttpListener` (wzorzec z `ProxyInjectsShimAndForwardsTvUserAgent`) → `SwitchSource(1)` zmienia serwowaną treść na drugi upstream; dwa lokalne katalogi `index.html` → przełączenie serwuje pliki drugiego katalogu; mieszanka folder+URL.
    - **Property 5: SwitchSource to no-op gdy cel już aktywny**
    - **Validates: Requirements 6.1, 6.2**
    - Test: `SwitchSource(activeIndex)` nie zmienia treści.
    - _Requirements: 4.2, 6.1, 6.2, 7.1, 7.3, 7.4_

- [x] 3. AppServer — endpoint porównawczy i payload profilu
  - [x] 3.1 Dodaj POST /__emulator/compare oraz pola compareMode/compareActive/compareNotice w Inject
    - W `Handle` dodaj gałąź: `path == "/__emulator/compare" && POST` → `SwitchSource(target)`, `_pendingNotice = _labels?[target]`, zwróć `{ok:true}`; przy niepoprawnym `target`/null `_sources` → `{ok:false}` bez zmiany.
    - W `Inject` dodaj do payloadu: `compareMode = (_sources is { Length: 2 })`, `compareActive = _activeIndex`, `compareNotice = Interlocked.Exchange(ref _pendingNotice, null) ?? ""` (konsumpcja raz).
    - Zintegruj: gałąź `compare` w `Handle` przed istniejącymi endpointami `/__emulator/*`.
    - _Requirements: 9.1, 9.2, 9.3, 10.1, 10.2, 10.3, 10.4_

  - [x]* 3.2 Napisz testy property dla endpointu i payloadu
    - **Property 3: compareNotice konsumowany raz po przełączeniu**
    - **Validates: Requirements 10.1, 10.2, 10.3**
    - **Property 4: Payload profilu odzwierciedla liczbę i indeks źródeł**
    - **Validates: Requirements 9.1, 9.2, 9.3**
    - Testy w `EmulatorTests.cs`: `POST compare?target=1` → `GET` HTML zawiera `compareNotice=label[1]` i `compareActive:1`; drugi `GET` → pusty `compareNotice`; `compareMode:false` dla `Start` jednoźródłowego; `target` poza zakresem → `{ok:false}` bez zmiany aktywnej apki.
    - _Requirements: 9.1, 9.2, 9.3, 10.1, 10.2, 10.3, 10.4_

- [x] 4. platform.js — przechwyt F8/F9, compareSwitch i snackbar (ES5)
  - [x] 4.1 Dodaj obsługę F8(119)/F9(120), compareSwitch i snackbar z compareNotice
    - W `rewriteKey(e)`: gdy `profile.compareMode` i `e.keyCode === 119` → `preventDefault()` + `stopImmediatePropagation()` + `compareSwitch(0)`; `120` → `compareSwitch(1)`; bez `dispatchKey` (połknięcie). Poza trybem F8/F9 przechodzą niezmienione.
    - Dodaj `compareSwitch(target)`: no-op gdy `profile.compareActive === target`; w przeciwnym razie `POST /__emulator/compare?target=<target>`, `onload`/`onerror` → `global.location.reload()`.
    - Dodaj `installCompareNotice()`: gdy `profile.compareNotice` niepuste → utwórz `div` (ES5: `var`/`function`/concat), `position:fixed`, `z-index:2147483647`, `pointer-events:none`, ciemny motyw (#1B1D22/#F3F1EA), tekst „Jesteś teraz na: " + compareNotice, `setTimeout` ~3000ms → `removeChild` (strzeżone `bar.parentNode`).
    - Wywołaj `installCompareNotice()` w `boot()` po instalatorach platformy.
    - Zintegruj: bez `let`/`const`/`class`/arrow/template-literals — plik musi parsować się w Chromium 47.
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 5.1, 5.2, 5.3, 5.4, 6.1_

  - [x]* 4.2 Napisz asercję integracyjną dla treści platform.js
    - Test w `EmulatorTests.cs`: `GET /__emulator/platform.js` zawiera `compareSwitch`, obsługę `119` i `120`, oraz `compareNotice`.
    - Nota o testowalności: brak runnera JS — przełączanie klawiaturą i snackbar weryfikuje się demo ręcznie (Task 6/7). Udokumentuj to uczciwie w teście.
    - _Requirements: 3.1, 3.2, 5.1_

- [x] 5. MainWindow — UI checkboxa i drugiego pola aplikacji
  - [x] 5.1 Dodaj CompareBox, DirectoryBoxB + drugi Przeglądaj, przełączanie widoczności, pre-fill i zapis
    - W `MainWindow.xaml`: dodaj `CheckBox x:Name="CompareBox"` z treścią „Tryb porównywania" nad polem aplikacji; dodaj drugie pole `DirectoryBoxB` z własnym przyciskiem „Przeglądaj" (początkowo ukryte).
    - W `MainWindow.xaml.cs`: obsługa `Checked`/`Unchecked` `CompareBox` → przełącz `Visibility` drugiego pola; pre-fill `CompareBox.IsChecked` z `_userSettings.CompareMode` i `DirectoryBoxB.Text` z `_userSettings.CompareAppB` (a `DirectoryBox.Text` z `DefaultApp` przez istniejący `StartupApp`); obsługa `BrowseClick` dla drugiego pola.
    - Zintegruj: przy starcie okna wszystkie powiązania ustawione; przełączanie nie psuje istniejącego layoutu.
    - _Requirements: 1.1, 1.2, 1.3_

  - [ ]* 5.2 Zweryfikuj ręcznie przełączanie UI i zapamiętywanie stanu
    - Brak runnera UI w testach — demo ręczne: zaznacz/odznacz checkbox przełącza jedno/dwa pola; po restarcie checkbox i drugie pole pamiętają stan.
    - _Requirements: 1.1, 1.2, 1.3, 8.3_

- [x] 6. MainWindow — ścieżka startu w trybie porównywania
  - [x] 6.1 Dodaj ValidateAppSource i ścieżkę BeginSession dla dwóch aplikacji
    - Wyciągnij `ValidateAppSource(string text)` (prywatny statyczny, testowalny): `AppSource.Parse` → jeśli zdalne, OK; jeśli katalog, sprawdź `index.html`; w przeciwnym razie rzuć `InvalidOperationException`. Walidacja folder bez `index.html` i protokół nie `http(s)` odrzucone.
    - W `BeginSession`: gdy `CompareBox.IsChecked == true`, parsuj oba pola przez `ValidateAppSource`, wywołaj `_server.StartCompare(root, a, b, labelA, labelB, profile, pointer, debug, "colorKey")` (label = surowy wpisany tekst), zapisz `CompareMode`/`CompareAppB` do `_userSettings` + `UserSettingsStore.Save`. Gdy nie zaznaczono — istniejąca ścieżka `Start(...)` bez zmian.
    - Reszta sesji (silnik, płaszczyzna wideo, debug, attach) bez zmian.
    - Zintegruj: `StartClick` → `BeginSession` obsługuje obie ścieżki; błąd walidacji pokazuje komunikat w `StatusText` bez startu.
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 7.1, 7.2, 8.1, 8.2_

  - [x]* 6.2 Napisz testy jednostkowe dla ValidateAppSource
    - **Property 6: ValidateAppSource przyjmuje poprawne i odrzuca niepoprawne wejścia**
    - **Validates: Requirements 2.1, 2.2, 2.3, 2.4**
    - Testy w nowym `CompareModeTests.cs`: katalog z `index.html` → zwraca AppSource; `http`/`https` → zwraca AppSource; katalog bez `index.html` → rzuca; `ftp://` → rzuca; pusty ciąg → rzuca. Udostępnij helper jako `internal` z atrybutem widoczności dla testów.
    - _Requirements: 2.1, 2.2, 2.3, 2.4_

- [x] 7. Integracja, testy i dokumentacja
  - [x] 7.1 Napisz test integracyjny pełnego przełączenia i zaktualizuj README/changelog
    - Test w `CompareModeTests.cs`: dwa upstreamy `HttpListener` + `POST /__emulator/compare?target=1` + `GET` potwierdza treść drugiej aplikacji i `compareNotice` w payloaddzie.
    - _Requirements: 4.2, 7.1, 10.1, 10.2_

  - [x] 7.2 Zaktualizuj README i changelog.json
    - Dodaj sekcję sterowania (F8 = app1, F9 = app2 w trybie porównywania) oraz opis checkboxa w ustawieniach.
    - Dodaj wpis w `docs/changelog.json` dla trybu porównywania.
    - _Requirements: 3.1, 3.2, 8.1_

- [x] 8. Checkpoint — wszystkie testy zielone
  - Upewnij się, że `dotnet test` przechodzi w całości, a asercje integracyjne platform.js są zielone. Zapytaj użytkownika, jeśli pojawią się pytania.

## Notes

- Zadania oznaczone `*` są opcjonalne i można je pominąć dla szybszego MVP, ALE testy property (1.2, 2.2, 3.2, 6.2) są zalecane — weryfikują poprawność uniwersalną.
- Każde zadanie odnosi się do konkretnych wymagań dla śledzenia.
- `platform.js` nie ma runnera JS — weryfikacja klawiatury/snackbara to asercja treści + demo ręczne (uczciwie udokumentowane).
- Testy property ze specyfikacji są pokryte przykładami xUnit (bez generatora 100 iteracji — w repozytorium nie ma runnera property-based). Tag: `Feature: compare-mode, Property {number}: {text}`.
- Testy jednostkowe i property są uzupełniające: jednostkowe dla przykładów/brzegów, property dla uniwersalnych własności.
- Znane ograniczenie: obie aplikacje muszą dzielić strukturę tras; mieszanie folder/url z różnymi start-path może dać 404 po przeładowaniu.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "2.1"] },
    { "id": 1, "tasks": ["3.1", "5.1"] },
    { "id": 2, "tasks": ["4.1", "6.1"] },
    { "id": 3, "tasks": ["1.2", "2.2"] },
    { "id": 4, "tasks": ["3.2", "6.2"] },
    { "id": 5, "tasks": ["4.2", "7.1"] },
    { "id": 6, "tasks": ["7.2"] }
  ]
}
```
