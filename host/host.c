#ifndef UNICODE
#define UNICODE 1
#endif
#ifndef _UNICODE
#define _UNICODE 1
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <wchar.h>

/* Event-driven tray host: no timer, raw input, runtime, network or render loop. */
#define WM_TRAY (WM_APP + 1)
#define WM_OPEN_PANEL (WM_APP + 2)
#define ID_OPEN 1001
#define ID_EXIT 1002
static const wchar_t CLASS_NAME[] = L"0Accel.Tray.v1";
static HANDLE panel_process;
static DWORD panel_pid;
static HWND host_window;
static HICON tray_icon;
static UINT taskbar_created;
static NOTIFYICONDATAW icon_data;
static wchar_t panel_path[32768];

static BOOL CALLBACK find_panel(HWND window, LPARAM parameter) {
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    if (pid == panel_pid && IsWindowVisible(window) && GetWindow(window, GW_OWNER) == NULL) {
        *(HWND *)parameter = window;
        return FALSE;
    }
    return TRUE;
}

static HWND panel_window(void) {
    DWORD code = 0;
    if (!panel_process || !GetExitCodeProcess(panel_process, &code) || code != STILL_ACTIVE) return NULL;
    HWND found = NULL;
    EnumWindows(find_panel, (LPARAM)&found);
    return found;
}

static void open_panel(void) {
    if (panel_process) {
        DWORD code = 0;
        if (GetExitCodeProcess(panel_process, &code) && code == STILL_ACTIVE) {
            HWND window = panel_window();
            if (window) { ShowWindow(window, IsIconic(window) ? SW_RESTORE : SW_SHOW); SetForegroundWindow(window); }
            return;
        }
        CloseHandle(panel_process); panel_process = NULL;
    }
    wchar_t command[32768];
    if (wcslen(panel_path) + 16 >= 32768) return;
    command[0] = L'"'; wcscpy(command+1, panel_path); wcscat(command, L"\" --hosted");
    STARTUPINFOW startup = {0}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(panel_path, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &process)) {
        MessageBoxW(NULL, L"Nie można otworzyć panelu. Uruchom 0Accel z kompletnego folderu wydania.\nWersja framework-dependent wymaga .NET Desktop Runtime 8 x64.", L"0Accel", MB_OK | MB_ICONERROR);
        return;
    }
    panel_process = process.hProcess; panel_pid = process.dwProcessId;
    CloseHandle(process.hThread);
}

static void add_icon(void) {
    Shell_NotifyIconW(NIM_ADD, &icon_data);
    icon_data.uVersion = NOTIFYICON_VERSION_4;
    Shell_NotifyIconW(NIM_SETVERSION, &icon_data);
}

static void show_menu(void) {
    HMENU menu = CreatePopupMenu();
    if (!menu) return;
    AppendMenuW(menu, MF_STRING, ID_OPEN, L"Otwórz 0Accel");
    AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"Podgląd · brak sterownika");
    AppendMenuW(menu, MF_SEPARATOR, 0, NULL);
    AppendMenuW(menu, MF_STRING, ID_EXIT, L"Zakończ");
    SetMenuDefaultItem(menu, ID_OPEN, FALSE);
    POINT point; GetCursorPos(&point);
    SetForegroundWindow(host_window);
    UINT command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
        point.x, point.y, 0, host_window, NULL);
    DestroyMenu(menu);
    PostMessageW(host_window, WM_NULL, 0, 0);
    if (command) SendMessageW(host_window, WM_COMMAND, command, 0);
}

static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == taskbar_created && taskbar_created) { add_icon(); return 0; }
    switch (message) {
    case WM_OPEN_PANEL: open_panel(); return 0;
    case WM_TRAY:
        switch (LOWORD(lparam)) {
        case NIN_SELECT: case NIN_KEYSELECT: open_panel(); break;
        case WM_CONTEXTMENU: show_menu(); break;
        }
        return 0;
    case WM_COMMAND:
        if (LOWORD(wparam) == ID_OPEN) open_panel();
        else if (LOWORD(wparam) == ID_EXIT) SendMessageW(window, WM_CLOSE, 0, 0);
        return 0;
    case WM_CLOSE: {
        HWND panel = panel_window();
        if (panel) {
            DWORD_PTR result;
            if (!SendMessageTimeoutW(panel, WM_CLOSE, 0, 0, SMTO_ABORTIFHUNG, 1000, &result)
                || IsWindow(panel)) { SetForegroundWindow(panel); return 0; }
        }
        DestroyWindow(window); return 0;
    }
    case WM_DESTROY:
        Shell_NotifyIconW(NIM_DELETE, &icon_data);
        PostQuitMessage(0); return 0;
    default: return DefWindowProcW(window, message, wparam, lparam);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command_line, int show) {
    (void)previous; (void)show; (void)command_line;
    wchar_t user[257]; DWORD user_length = 257;
    if (!GetUserNameW(user, &user_length)) return 1;
    wchar_t mutex_name[300] = L"Local\\0Accel.Tray.";
    wcscat(mutex_name, user);
    HANDLE mutex = CreateMutexW(NULL, TRUE, mutex_name);
    if (!mutex) return 1;
    BOOL existing_instance = GetLastError() == ERROR_ALREADY_EXISTS;
    wchar_t ready_name[320]; wcscpy(ready_name, mutex_name); wcscat(ready_name, L".Ready");
    HANDLE ready_event = CreateEventW(NULL, TRUE, FALSE, ready_name);
    if (!ready_event) { CloseHandle(mutex); return 1; }
    if (existing_instance) {
        // Only the short-lived second launcher waits; the resident host never polls.
        WaitForSingleObject(ready_event, 2000);
        HWND existing = FindWindowW(CLASS_NAME, L"0Accel host");
        if (existing) {
            DWORD pid; GetWindowThreadProcessId(existing, &pid);
            AllowSetForegroundWindow(pid);
            PostMessageW(existing, WM_OPEN_PANEL, 0, 0);
        }
        CloseHandle(ready_event); CloseHandle(mutex); return 0;
    }
    ResetEvent(ready_event);
    SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
    DWORD length = GetModuleFileNameW(NULL, panel_path, 32768);
    if (!length || length >= 32736) { CloseHandle(ready_event); CloseHandle(mutex); return 1; }
    wchar_t *slash = wcsrchr(panel_path, L'\\');
    if (!slash) { CloseHandle(ready_event); CloseHandle(mutex); return 1; }
    wcscpy(slash+1, L"app\\0Accel.Panel.exe");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    taskbar_created = RegisterWindowMessageW(L"TaskbarCreated");
    tray_icon = (HICON)LoadImageW(instance, MAKEINTRESOURCEW(101), IMAGE_ICON, 32, 32, LR_DEFAULTCOLOR);
    if (!tray_icon) tray_icon = LoadIconW(NULL, IDI_APPLICATION);
    WNDCLASSW cls = {0}; cls.lpfnWndProc = window_proc; cls.hInstance = instance;
    cls.lpszClassName = CLASS_NAME; cls.hIcon = tray_icon;
    if (!RegisterClassW(&cls)) { CloseHandle(ready_event); CloseHandle(mutex); return 1; }
    // Hidden top-level window receives Explorer restart broadcasts.
    host_window = CreateWindowExW(WS_EX_TOOLWINDOW, CLASS_NAME, L"0Accel host", 0, 0, 0, 0, 0, NULL, NULL, instance, NULL);
    if (!host_window) { CloseHandle(ready_event); CloseHandle(mutex); return 1; }
    icon_data.cbSize = sizeof(icon_data); icon_data.hWnd = host_window; icon_data.uID = 1;
    icon_data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
    icon_data.uCallbackMessage = WM_TRAY; icon_data.hIcon = tray_icon;
    wcscpy(icon_data.szTip, L"0Accel");
    add_icon();
    SetEvent(ready_event);
    int argc = 0;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    BOOL tray = FALSE;
    if (argv) {
        for (int i=1; i<argc; i++) if (wcscmp(argv[i], L"--tray") == 0) tray = TRUE;
        LocalFree(argv);
    }
    if (!tray) open_panel();
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    if (panel_process) CloseHandle(panel_process);
    DestroyIcon(tray_icon); CloseHandle(ready_event); ReleaseMutex(mutex); CloseHandle(mutex);
    return 0;
}
