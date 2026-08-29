using System;
using System.IO;
using Microsoft.Win32;

namespace ZeroAccel;

internal static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue("0Accel") is string; }
    }
    internal static bool SetEnabled(bool enabled, bool startInTray, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (enabled)
            {
                var exe = AppPaths.Launcher;
                if (!File.Exists(exe)) throw new IOException("Brakuje pliku 0Accel.exe w folderze aplikacji.");
                key.SetValue("0Accel", $"\"{exe}\"" + (startInTray ? " --tray" : ""), RegistryValueKind.String);
            }
            else key.DeleteValue("0Accel", false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { error = "Nie udało się zmienić autostartu: " + e.Message; return false; }
    }
}
