### AI Disclaimer 🔥🤖🔥🤖🔥🤖🔥

Aplikacja jest w 100% vibe-code'owana. Nie mam zielonego pojęcia co jest w kodzie. Wiedziałem czego wymagam od aplikacji, rozumiałem napotkane problemy i próbowałem jasno agentom tłumaczyć co jest źle, dlaczego ma być tak a nie inaczej i żeby to ogarnęli. **Nie jest to mój projekt do portfolio**. Jest to zabawka zrobiona na zajawce którą chcę się podzielić, gdyby ktoś potrzebował czegoś takiego. 

---

# TV Emulator

Program na Windowsie otwiera stronę tak, jak wybrany telewizor Samsung Tizen albo LG webOS. Po przycisku **Uruchom** obraz zajmuje ekran 16:9. Sterujesz pilotem: klawiaturą albo padem. Mysz w tym widoku nie służy do obsługi strony.

## Czego potrzebujesz

- Windows 10 albo Windows 11.
- Kilkanaście GB wolnego miejsca na dysku. Przy pierwszym razie program dociąga stare przeglądarki.
- Internet przy pierwszej instalacji i przy oglądaniu filmu z próbki.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Wystarczy wersja 8.0.426 albo nowszy .NET 8.

WebView2, potrzebny do obrazu wideo, jest zwykle już w Windowsie. Instalator jest podany niżej, przy problemach z filmem.

## Instalacja

1. Rozpakuj pobrane archiwum albo sklonuj repozytorium.
2. Otwórz PowerShell w folderze, w którym leży plik `TvEmulator.sln`. W Eksploratorze plików możesz kliknąć pasek adresu, wpisać `powershell` i nacisnąć Enter.
3. Sprawdź, że .NET jest widoczny:

```powershell
dotnet --version
```

Ma się pokazać numer zaczynający się od `8.`. Jeśli PowerShell pisze, że nie rozpoznaje `dotnet`, zainstaluj SDK z linku wyżej i otwórz PowerShell od nowa.

4. Pobierz przeglądarki dopasowane do telewizorów:

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-engines.ps1
```

To trwa. Skrypt wypisuje `GET`, gdy coś pobiera, `UNZIP`, gdy rozpakowuje, i `READY`, gdy dana przeglądarka już jest na dysku. Na końcu ma być linia `FETCH_DONE`.

Profile telewizorów używają tylko przeglądarek `chromium-*`.

5. Uruchom program:

```powershell
dotnet run --project src\TvEmulator\TvEmulator.csproj
```

Pierwsze budowanie też chwilę trwa. Potem otwiera się okno **TV Emulator**.

Zostaw program w tym folderze. Szuka pliku `TvEmulator.sln` obok siebie. Sam plik `TvEmulator.exe` przeniesiony na pulpit nie wystartuje poprawnie.

Kolejne razy: otwórz PowerShell w tym samym folderze i powtórz tylko ostatnie polecenie, `dotnet run`.

## Okno ustawień

- **Profil telewizora.** Nazwa i rok są w pierwszej linii. Pod spodem jest wersja Chromium oraz metoda odtwarzania: `avplay` na Tizenie, `edge` na webOS.
- **Aplikacja.** Folder, w którym jest `index.html`, albo adres zaczynający się od `http://` albo `https://`. Przycisk **Przeglądaj** wybiera folder.
- **Monitor.** Ekran, na którym ma się pojawić obraz 16:9. Przy dwóch monitorach wybierz ten, na który patrzysz.
- **Wskaźnik magic remote.** Po zaznaczeniu widać kursor.
- **Zapis diagnostyczny.** Jest włączony na starcie. Program zapisuje plik `logs\session-….log`. Po błędzie pasek na dole okna pokazuje ścieżkę do tego pliku.

Napis o RAM i CPU tylko informuje, jaki limit jest zapisany w profilu. Program go nie włącza.

### Co otworzyć na początek

Po starcie w polu aplikacji jest już `samples\tizen-hello`. To prosta strona z napisem i kodami klawiszy, bez filmu. Wybierz profil Tizen i naciśnij **Uruchom**.

Żeby zobaczyć obraz wideo, wskaż folder `samples\tizen-avplay`. Potrzebny jest internet. Enter albo klawisz `1` puszcza film bez zabezpieczeń.

Dla profilu webOS wskaż folder `samples\webos-hello`.

Własna aplikacja to folder z plikiem `index.html` albo wklejony adres strony.

## Sterowanie

- Strzałki i Enter działają jak na pilocie.
- Esc to Back.
- F1, F2, F3 i F4 to przyciski kolorów.
- Pad: krzyżak, A = Enter, B = Back, LB, RB, X i Y = kolory.
- **Ctrl+Shift+Q** wraca do okna ustawień.

Sesję kończ tym skrótem albo zamknięciem okna ustawień. Kończenie Chromium z Menedżera zadań gubi zapamiętaną sesję strony.

## Gdy coś nie działa

**PowerShell nie rozpoznaje `dotnet`.** Zainstaluj .NET 8 SDK i otwórz okno PowerShella od nowa. `dotnet --version` ma pokazać numer `8.…`.

**Skrypt nie chce się uruchomić albo pisze o zasadach wykonywania.** Wpisz całe polecenie z `ExecutionPolicy Bypass` z sekcji Instalacja.

**Pobieranie urwało się w połowie.** Uruchom skrypt jeszcze raz. Linie `READY` oznaczają, że ten silnik już jest i nie będzie pobierany drugi raz. Czekaj na `FETCH_DONE`.

**Na dole okna: `Brak pliku silnika dla chromium-…`.** Skrypt pobierania nie doszedł do `FETCH_DONE`. Uruchom go ponownie z folderu, w którym jest `TvEmulator.sln`.

**Windows albo antywirus blokuje `chrome.exe`.** Pozwól na uruchomienie. To oficjalne, stare kompilacje Chromium dopasowane do telewizorów, a nie instalator telewizora.

**`Nie znaleziono TvEmulator.sln`.** Program został odpalony spoza folderu projektu. Wróć do tego folderu i użyj `dotnet run` jak wyżej.

**`Podaj katalog z index.html albo adres http(s)`.** Wybrany folder nie zawiera `index.html`, albo adres nie zaczyna się od `http://` lub `https://`.

**Widać napis „Nie widać obrazu silnika”.** Obraz jest na monitorze wybranym na liście. Sprawdź drugi ekran. **Ctrl+Shift+Q** wraca do ustawień.

**W logu jest błąd WebView2 albo zdanie „Płaszczyzna wideo (WebView2) nie wystartowała”.** Doinstaluj [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) i uruchom program jeszcze raz. Próbka `tizen-hello` celowo nie pokazuje filmu. Do obrazu użyj `samples\tizen-avplay`.

**`Nie udało się uruchomić Chromium` albo `Brak wolnego portu localhost`.** Poprzednia sesja jeszcze działa. Wróć do niej skrótem **Ctrl+Shift+Q** albo zamknij okno programu i spróbuj ponownie.

**Chcesz zalogować się od nowa.** Zamknij program, potem usuń folder `%LOCALAPPDATA%\TvEmulator\userdata`. W pasku adresu Eksploratora możesz wkleić `%LOCALAPPDATA%\TvEmulator`.

## Testy

Z tego samego folderu projektu: `dotnet test`.