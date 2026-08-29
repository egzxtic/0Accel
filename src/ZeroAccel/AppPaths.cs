using System;
using System.IO;

namespace ZeroAccel;

internal static class AppPaths
{
    // The panel and its runtime live in app/; the launcher stays at the package root.
    internal static string Launcher => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "0Accel.exe"));
}
