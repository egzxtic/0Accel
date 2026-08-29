# 0Accel

Minimalny panel konfiguracji akceleracji myszy dla Windows.

0Accel łączy własny, lekki interfejs z oryginalnym sterownikiem
[Raw Accel 1.7.1](https://github.com/RawAccelOfficial/rawaccel/releases/tag/v1.7.1).
Projekt nie zawiera własnego sterownika jądra i nie modyfikuje `rawaccel.sys`.

**Status:** `0.4.0 Preview` · Windows x64 · MIT

[Pobierz najnowsze wydanie](https://github.com/egzxtic/0Accel/releases)

## Funkcje

- tryby `OFF`, `Linear`, `Classic` i `Natural`;
- czułość, proporcja Y/X, rotacja, Gain, offset oraz limity Input/Output;
- wykres odpowiedzi zgodny z obliczeniami Raw Accel;
- automatyczne wykrywanie myszy i szacowanie polling rate;
- profile JSON: import, eksport i zapis lokalny;
- Dark Mode i Light Mode pobierane z ustawień aplikacji;
- opcjonalny autostart oraz lekki host w zasobniku;
- brak kont, reklam, WebView, telemetrii i połączeń sieciowych.

## Jak to działa

`0Accel.exe` jest małym natywnym hostem zasobnika. Otwiera proces
`0Accel.Panel.exe` tylko wtedy, gdy panel jest widoczny. Po zamknięciu okna panel
kończy działanie, a w tle zostaje wyłącznie host.

`0Accel.RawAccel.dll` działa w user mode. Przygotowuje i odczytuje konfigurację,
ale nie jest sterownikiem. Właściwe przetwarzanie ruchu wykonuje osobno
zainstalowany, oryginalny `rawaccel.sys`.

- **Odczytaj** — pobiera aktywny profil myszy do panelu.
- **Zastosuj** — zapisuje bieżące ustawienia do Raw Accel.
- **Importuj / Eksportuj** — wymienia profile 0Accel w formacie JSON.
- **Zapisz** — zapisuje lokalny szkic bez aktywowania sterownika.

Ustawienia użytkownika znajdują się w
`%LOCALAPPDATA%\0Accel\settings.json`. Przed zapisem do sterownika tworzona jest
kopia konfiguracji w `%LOCALAPPDATA%\0Accel\rawaccel-backups\`.

## Uruchomienie

1. Zainstaluj oficjalny Raw Accel 1.7.1 i wykonaj wymagany przez niego restart.
2. Pobierz ZIP z GitHub Releases, rozpakuj cały folder i uruchom `0Accel.exe`.
3. Wybierz mysz, kliknij **Odczytaj**, ustaw profil i kliknij **Zastosuj**.

Do udostępniania służy ZIP z `artifacts/releases/`. Nie wysyłaj całego katalogu
repozytorium. Domyślny build wymaga .NET Desktop Runtime 8 x64.

## Build i testy

Wymagane są .NET SDK 8 oraz Zig 0.15.2. EWDK nie jest potrzebny.

```powershell
.\scripts\build.ps1 -Sanitize
.\scripts\test-ui.ps1
.\scripts\test-lifecycle.ps1
.\scripts\test-rawaccel-reference.ps1
.\scripts\package.ps1 -Channel Preview
```

Build nie instaluje sterownika i nie zmienia konfiguracji rozruchu. Testy offline
obejmują walidację buforów, sanitizer, 10 000 mutacji, testy ustawień oraz
porównanie 240 wektorów z oficjalnym `wrapper.dll` Raw Accel.

## Struktura repozytorium

- `src/` — panel WPF i integracja;
- `host/` — natywny host zasobnika;
- `tools/` — mostek Raw Accel i generator ikon;
- `tests/` — testy automatyczne;
- `assets/` — grafiki osadzane podczas builda;
- `scripts/` — build, testy i pakowanie;
- `artifacts/app/` — aktualna aplikacja;
- `artifacts/releases/` — gotowa paczka ZIP;
- `.tools/` — lokalne SDK i cache, ignorowane przez Git.

## Raw Accel i bezpieczeństwo

Integracja jest przypięta do Raw Accel `v1.7.1`, commit
`53a721345617a1e29f3a16750cbdf807040cf44e`. Obsługiwany sterownik raportuje
wersję `1.7.0`; jego oczekiwany SHA-256 to
`8a62c4deef2774b43a7363b352eda79897533a1080c9c26ffeff0559e43358d7`.

0Accel nie korzysta z pamięci gry, nie wstrzykuje wejścia i nie zawiera funkcji
makr ani recoil compensation. Projekt jest niezależny i nie jest zatwierdzony
przez Riot Games. Open source i podpis sterownika nie stanowią gwarancji
zgodności z każdym systemem anti-cheat.

Kanał `Production` pozostaje zablokowany do zakończenia testów na fizycznych
myszach, testów restartu/uśpienia i przeglądu wydania. Aktualne paczki są
oznaczone jako `Preview`.

## Licencja

Kod 0Accel jest udostępniany na licencji MIT. Przypięte fragmenty Raw Accel
zachowują oryginalną licencję w `tools/RawAccelBridge/upstream/LICENSE` oraz w
paczce aplikacji jako `app/licenses/RawAccel-MIT.txt`.
