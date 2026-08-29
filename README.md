# 0Accel

Minimalny panel akceleracji myszy dla Windows, zbudowany wokół oryginalnego
sterownika [Raw Accel 1.7.1](https://github.com/RawAccelOfficial/rawaccel/releases/tag/v1.7.1).

**Status:** `0.4.0 Preview` · Windows 10/11 x64 · MIT

[Pobierz najnowsze wydanie](https://github.com/egzxtic/0Accel/releases)

## Instalacja

Rekomendowany plik to `0Accel-Setup-0.4.0-preview-win-x64.exe`. Jeden instalator
zawiera panel oraz niezmieniony, oficjalnie podpisany `rawaccel.sys`; nie trzeba
wcześniej instalować Raw Accel ani środowiska .NET.

1. Uruchom instalator jako administrator.
2. Zapoznaj się z informacją o sterowniku i zakończ instalację.
3. Uruchom ponownie Windows, jeśli instalator o to poprosi.
4. Otwórz 0Accel, wybierz mysz, kliknij **Odczytaj**, ustaw profil i kliknij
   **Zastosuj**.

Alternatywny ZIP `0Accel-0.4.0-preview-portable-win-x64.zip` zawiera samą,
self-contained aplikację. Wariant portable nie instaluje sterownika — jest
przeznaczony dla osób, które mają już zgodny Raw Accel 1.7.1.

Instalator i aplikacja 0Accel nie mają jeszcze komercyjnego certyfikatu
Authenticode, dlatego Microsoft Defender SmartScreen może wyświetlić ostrzeżenie.
Wbudowany sterownik jądra zachowuje ważny podpis Microsoft Windows Hardware
Compatibility Publisher. Każdy artefakt release ma plik `.sha256`.

## Funkcje

- tryby `OFF`, `Linear`, `Classic` i `Natural`;
- czułość, proporcja Y/X, rotacja, Gain, offset oraz limity Input/Output;
- wykres odpowiedzi i znacznik ostatniego ruchu;
- automatyczne wykrywanie myszy i szacowanie polling rate;
- profile JSON: import, eksport i zapis lokalny;
- Dark Mode i Light Mode;
- opcjonalny autostart i lekki natywny host w zasobniku;
- brak kont, reklam, WebView, telemetrii i połączeń sieciowych aplikacji.

## Jak to działa

`0Accel.exe` jest małym natywnym hostem zasobnika. Proces WPF
`0Accel.Panel.exe` działa tylko wtedy, gdy panel jest otwarty. Po zamknięciu
okna w tle pozostaje wyłącznie host.

`0Accel.RawAccel.dll` przygotowuje i odczytuje konfigurację w user mode.
Rzeczywiste przetwarzanie ruchu wykonuje podpisany `rawaccel.sys`.

- **Odczytaj** — pobiera aktywną konfigurację sterownika do panelu.
- **Zastosuj** — aktywuje ustawienia widoczne w panelu.
- **Importuj / Eksportuj** — wymienia profile 0Accel w formacie JSON.
- **Zapisz** — zapisuje lokalny szkic bez aktywowania sterownika.

Ustawienia są przechowywane w `%LOCALAPPDATA%\0Accel\settings.json`, a kopie
konfiguracji przed zastosowaniem w `%LOCALAPPDATA%\0Accel\rawaccel-backups\`.
Log instalatora sterownika znajduje się w `%ProgramData%\0Accel\setup.log`.

Deinstalator pyta, czy usunąć również współdzielony sterownik Raw Accel.
Domyślna odpowiedź to **Nie**, aby nie zepsuć osobnej instalacji oryginalnego
panelu Raw Accel. Usunięcie sterownika wymaga restartu.

## Integralność sterownika

Build pobiera przypięte oficjalne wydanie Raw Accel `v1.7.1`. Instalator przed
jakąkolwiek zmianą systemu sprawdza hash i podpis sterownika, a po konfiguracji
weryfikuje usługę oraz filtr myszy. Poprawna istniejąca instalacja nie jest
nadpisywana.

- archiwum `RawAccel_v1.7.1.zip` SHA-256:
  `770fe3ae0919ca3c4d412f58c985eb27f5434decad809f7e8206de4e8852eec4`;
- `rawaccel.sys` SHA-256:
  `8a62c4deef2774b43a7363b352eda79897533a1080c9c26ffeff0559e43358d7`;
- przypięty commit źródeł integracji:
  `53a721345617a1e29f3a16750cbdf807040cf44e`.

0Accel nie czyta pamięci gry, nie wstrzykuje wejścia i nie zawiera makr ani
recoil compensation. Projekt jest niezależny i nie jest zatwierdzony przez
autorów Raw Accel ani Riot Games. Podpis sterownika i otwarty kod nie stanowią
gwarancji zgodności z każdym systemem anti-cheat.

## Build i testy

Wymagane są .NET SDK 8 i Zig 0.15.2. Skrypt instalatora pobiera przypięte,
zweryfikowane kopie Raw Accel 1.7.1 oraz Inno Setup 6.7.3. EWDK nie jest
potrzebny.

```powershell
.\scripts\build.ps1 -Sanitize -SelfContained
.\scripts\test-ui.ps1
.\scripts\test-lifecycle.ps1
.\scripts\test-rawaccel-reference.ps1
.\scripts\test-release-boundary.ps1 -SkipPackaging
.\scripts\package.ps1 -Channel Preview -Clean
```

Automatyczne testy nie instalują i nie usuwają sterownika. Obejmują walidację
buforów, sanitizer, mutacje, testy ustawień, porównanie z oficjalnym wrapperem
Raw Accel oraz odrzucenie zmodyfikowanego payloadu sterownika.

## Struktura repozytorium

- `src/` — panel WPF i integracja;
- `host/` — natywny host zasobnika;
- `setup/` — instalator i zweryfikowany helper sterownika;
- `tools/` — mostek Raw Accel i generator ikon;
- `tests/` — testy automatyczne;
- `assets/` — grafiki osadzane podczas builda;
- `scripts/` — build, testy i pakowanie;
- `artifacts/app/` — aktualna aplikacja;
- `artifacts/releases/` — gotowy Setup, portable ZIP i sumy SHA-256;
- `.tools/` — lokalne SDK i cache, ignorowane przez Git.

## Licencja

Kod 0Accel jest udostępniany na licencji MIT. Fragmenty integracyjne i
redystrybuowany sterownik Raw Accel zachowują oryginalną licencję MIT w
`tools/RawAccelBridge/upstream/LICENSE` oraz w paczce jako
`app/licenses/RawAccel-MIT.txt`.
