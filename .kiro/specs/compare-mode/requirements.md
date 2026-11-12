# Requirements Document

## Introduction

Tryb porównywania (Compare Mode) pozwala otworzyć dwie wersje tej samej aplikacji telewizyjnej w jednej sesji silnika TV Emulator i przełączać się między nimi klawiszami F8 (aplikacja 1) i F9 (aplikacja 2). Przełączenie zachowuje bieżącą trasę (ścieżka + hash), a po przeładowaniu strony pojawia się krótki snackbar z adresem aplikacji, na którą wskoczono. Tryb działa zarówno dla lokalnych katalogów z `index.html`, jak i dla zdalnych URL `http(s)`. Stan trybu i adres drugiej aplikacji zapisują się w `settings.json`.

## Glossary

- **MainWindow**: główne okno startowe aplikacji TV Emulator (WPF), z polami wyboru profilu, monitora i aplikacji.
- **AppServer**: serwer HTTP (HttpListener) serwujący stronę z lokalnego katalogu lub proxyujący zdalny URL `http(s)`, przepisujący origin do `http://127.0.0.1:{port}/...`.
- **AppSource**: record struct rozróżniający źródło aplikacji — katalog lokalny (`Directory`) lub zdalny URI (`Page`); `Parse(text)` rozpoznaje folder vs `http(s)`.
- **Shim**: skrypt `shims/platform.js` (ES5, parsowany przez Chromium 47) wstrzykiwany w każdą odpowiedź HTML; przejmuje klawiaturę na etapie capture i mapuje klawisze pilota.
- **UserSettingsStore**: statyczny sklep ustawień odczytujący/zapisujący `settings.json` w `RepoPaths.DataRoot()`.
- **Silnik**: przeglądarka uruchamiająca stronę (przypięty Chromium/Firefox/Electron).
- **compareMode**: flaga logiczna w payloaddzie profilu (`window.__TV_EMULATOR_PROFILE__`), `true` gdy skonfigurowano dwa źródła.
- **compareActive**: indeks aktywnego źródła (0 lub 1) w payloaddzie profilu.
- **compareNotice**: komunikat snackbara ustawiany po przełączeniu, konsumowany raz przy następnej odpowiedzi HTML.
- **Trasa**: kombinacja ścieżki i hasha w adresie strony (np. `/#/video-123`).

## Requirements

### Requirement 1: Checkbox trybu porównywania

**User Story:** Jako tester aplikacji TV, chcę włączyć tryb porównywania jednym checkboxem, tak by pole aplikacji rozdzieliło się na dwa osobne wejścia.

#### Acceptance Criteria

1. WHEN użytkownik zaznaczy checkbox „Tryb porównywania" w MainWindow, THE MainWindow SHALL wyświetlić dwa pola wejściowe aplikacji (app1, app2) zamiast jednego.
2. WHEN użytkownik odznaczy checkbox „Tryb porównywania", THE MainWindow SHALL przywrócić pojedyncze pole wejściowe aplikacji.
3. WHEN MainWindow się startuje, THE MainWindow SHALL odczytać pole `CompareMode` z `settings.json` i ustawić stan checkboxa zgodnie z zapisaną wartością.

### Requirement 2: Walidacja wejść folder/URL

**User Story:** Jako tester, chcę wpisać w każde pole katalog z `index.html` albo URL `http(s)`, tak by móc porównywać aplikacje lokalne i zdalne w dowolnej kombinacji.

#### Acceptance Criteria

1. WHEN pole aplikacji zawiera ścieżkę katalogu z plikiem `index.html`, THE AppSource SHALL rozwiązać je do źródła katalogowego.
2. WHEN pole aplikacji zawiera URL z protokołem `http` lub `https`, THE AppSource SHALL rozwiązać je do źródła zdalnego.
3. WHEN pole aplikacji zawiera katalog bez pliku `index.html`, THE MainWindow SHALL odrzucić start sesji i pokazać komunikat błędu w pasku statusu.
4. WHEN pole aplikacji zawiera URL z protokołem innym niż `http`/`https` (np. `ftp`), THE MainWindow SHALL odrzucić start sesji.
5. WHERE tryb porównywania jest włączony, THE MainWindow SHALL dopuścić mieszane kombinacje folder/URL między app1 a app2.

### Requirement 3: Przełączanie F8/F9

**User Story:** Jako tester, chcę przełączać się między app1 a app2 klawiszami F8 i F9, tak by szybko porównać to samo miejsce w obu wersjach.

#### Acceptance Criteria

1. WHEN tryb porównywania jest włączony i użytkownik naciska F8, THE Shim SHALL przełączyć aktywną aplikację na app1 (indeks 0).
2. WHEN tryb porównywania jest włączony i użytkownik naciska F9, THE Shim SHALL przełączyć aktywną aplikację na app2 (indeks 1).
3. WHEN tryb porównywania jest wyłączony, THE Shim SHALL przekazywać F8 i F9 do strony bez modyfikacji.
4. WHEN F8 lub F9 jest przechwycone w trybie porównywania, THE Shim SHALL wywołać `preventDefault()` i `stopImmediatePropagation()` bez dysponowania zmapowanego klawisza (połknięcie).

### Requirement 4: Zachowanie trasy przy przełączeniu

**User Story:** Jako tester, chcę by przełączenie zachowało bieżącą trasę (ścieżka + hash), tak by SPA inicjowało się na tym samym ekranie w obu aplikacjach.

#### Acceptance Criteria

1. WHEN przełączenie porównawcze następuje, THE Shim SHALL wywołać `location.reload()`, które zachowuje ścieżkę i hash w przeglądarce.
2. WHEN strona przeładowuje się po przełączeniu, THE AppServer SHALL serwować lub proxyować z aktywnego źródła ustawionego przez `POST /__emulator/compare?target=<target>`.
3. IF obie aplikacje nie dzielą struktury tras (różne start-path), THEN THE System SHALL udokumentować to jako znane ograniczenie bez automatycznej translacji ścieżek.

### Requirement 5: Snackbar po przełączeniu

**User Story:** Jako tester, chcę zobaczyć krótki snackbar z adresem aplikacji po przełączeniu, tak by wiedzieć, na której wersji teraz jestem.

#### Acceptance Criteria

1. WHEN strona przeładowuje się po przełączeniu, THE Shim SHALL wyświetlić snackbar z tekstem „Jesteś teraz na: <adres>", gdzie `<adres>` to surowy ciąg wpisany dla aplikacji docelowej.
2. WHEN snackbar się wyświetli, THE Shim SHALL automatycznie go usunąć po około 3 sekundach.
3. THE Snackbar SHALL być stylizowany na ciemny motyw (tło #1B1D22, tekst #F3F1EA), pozycjonowany `fixed`, nad treścią, z `pointer-events:none`.
4. THE Snackbar SHALL być zaimplementowany w ES5 (bez `let`/`const`/`class`/arrow/template-literals), aby parsował się w Chromium 47.

### Requirement 6: Brak akcji gdy już na aplikacji docelowej

**User Story:** Jako tester, nie chcę zbędnego migania snackbara, gdy naciskam F8/F9 będąc już na aplikacji docelowej.

#### Acceptance Criteria

1. WHEN naciśnięty klawisz (F8/F9) wskazuje aplikację, która jest już aktywna (`compareActive` == target), THE Shim SHALL nie wysyłać żądania przełączenia, nie przeładowywać strony i nie pokazywać snackbara.
2. WHEN przełączenie jest no-op, THE AppServer SHALL nie ustawiać `_pendingNotice` dla tej aplikacji.

### Requirement 7: Obsługa lokalnych katalogów

**User Story:** Jako tester, chcę by tryb porównywania działał też dla lokalnych katalogów z `index.html`, a nie tylko dla zdalnych URL.

#### Acceptance Criteria

1. THE AppServer SHALL obsługiwać tryb porównywania dla dwóch lokalnych katalogów zawierających `index.html`.
2. THE AppServer SHALL obsługiwać tryb porównywania dla mieszanki katalogu lokalnego i zdalnego URL.
3. WHEN aktywne źródło jest katalogiem lokalnym, THE AppServer SHALL serwować pliki z tego katalogu przez `AppFiles.Resolve`.
4. WHEN aktywne źródło jest URL zdalnym, THE AppServer SHALL proxyować żądania z aktywnego origin.

### Requirement 8: Trwałość ustawień

**User Story:** Jako tester, chcę by stan checkboxa i adres drugiej aplikacji zapisywały się i przeżywały restart programu.

#### Acceptance Criteria

1. WHEN sesja startuje w trybie porównywania, THE MainWindow SHALL zapisać `CompareMode` = true oraz `CompareAppB` do `settings.json`.
2. WHEN sesja startuje poza trybem porównywania, THE MainWindow SHALL zapisać `CompareMode` = false do `settings.json`.
3. WHEN aplikacja się restartuje, THE MainWindow SHALL przywrócić stan checkboxa z `CompareMode` i adres drugiej aplikacji z `CompareAppB`.
4. WHEN `settings.json` nie zawiera pól `CompareMode`/`CompareAppB` (stary plik), THE UserSettingsStore SHALL przywrócić domyślne wartości (`CompareMode` = false, `CompareAppB` = "").
5. THE UserSettingsStore SHALL zachować pozostałe pola ustawień przy zapisie/odczycie `CompareMode` i `CompareAppB`.

### Requirement 9: Flaga compareMode w payloaddzie

**User Story:** Jako deweloper shimu, chcę by payload profilu sygnalizował tryb porównywania i aktywne źródło, tak by platform.js wiedział, czy reagować na F8/F9.

#### Acceptance Criteria

1. WHEN skonfigurowano dwa źródła, THE AppServer SHALL ustawić `compareMode` = true w payloaddzie profilu.
2. WHEN skonfigurowano jedno źródło, THE AppServer SHALL ustawić `compareMode` = false w payloaddzie profilu.
3. THE AppServer SHALL dołączyć `compareActive` (indeks aktywnego źródła) do payloadu profilu.

### Requirement 10: Jednorazowa konsumpcja compareNotice

**User Story:** Jako tester, chcę by snackbar pojawiał się tylko raz po przełączeniu, a nie powtarzał się przy każdym kolejnym żądaniu HTML.

#### Acceptance Criteria

1. WHEN endpoint `POST /__emulator/compare?target=<target>` przełącza źródło, THE AppServer SHALL ustawić `_pendingNotice` na etykietę aplikacji docelowej.
2. WHEN odpowiedź HTML jest wysyłana z niepustym `_pendingNotice`, THE AppServer SHALL dołączyć go do `compareNotice` w payloaddzie i wyczyścić `_pendingNotice`.
3. WHEN kolejna odpowiedź HTML jest wysyłana po konsumpcji, THE AppServer SHALL wysłać pusty `compareNotice` (brak powtórzenia snackbara).
4. IF `target` jest poza zakresem lub `_sources` nie jest skonfigurowane, THEN THE AppServer SHALL odpowiedzieć `{ok:false}` bez zmiany aktywnej aplikacji.
