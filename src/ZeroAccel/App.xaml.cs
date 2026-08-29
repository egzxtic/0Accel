using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZeroAccel;

/* The native host owns the tray. Closing this panel terminates CLR/WPF. */
public partial class App : Application
{
    private MainWindow? panel;
    internal SettingsStore Store { get; private set; } = new();
    internal Settings Settings { get; set; } = new();
    internal ImageSource? LogoImage { get; private set; }
    internal ImageSource? WindowIconImage { get; private set; }
    internal Locale Locale { get; private set; } = Locale.SetForCurrentCulture();
    internal bool TestMode { get; private set; }
    internal bool Exiting { get; private set; }
    internal string T(string key) => Locale.T(key);
    internal string F(string key, params object[] args) => Locale.F(key, args);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        TestMode = e.Args.Contains("--ui-test");
        bool requestTray = e.Args.Contains("--tray");
        bool startInTray = requestTray;
        Locale.SetForCurrentCulture();
        if (!TestMode && !e.Args.Contains("--hosted"))
        {
            try
            {
                if (!requestTray) startInTray = new SettingsStore().Load().StartInTray;
            }
            catch { }
            var host = AppPaths.Launcher;
            if (File.Exists(host))
            {
                var startInfo = new ProcessStartInfo(host)
                {
                    UseShellExecute = false,
                    Arguments = startInTray ? "--tray" : ""
                };
                Process.Start(startInfo);
            }
            else MessageBox.Show(T("M_WindowHostMissing"), "0Accel");
            Shutdown(); return;
        }
        if (TestMode)
        {
            Store = new SettingsStore(Path.Combine(Path.GetTempPath(), "0Accel-ui-test-" + Environment.ProcessId));
            Settings = new Settings();
        }
        else Settings = Store.Load();
        try
        {
            if (RawAccelProtocol.Abi != 1) throw new InvalidOperationException(T("M_EngineVersionMismatch"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            MessageBox.Show(F("M_EngineLoadFailed", ex.Message), "0Accel");
            Shutdown(1); return;
        }
        ApplyTheme(Settings.Theme);
        ApplyLocale(Locale);
        panel = new MainWindow(this);
        MainWindow = panel;
        panel.Closed += (_, _) => Shutdown();
        panel.Show();
        if (TestMode)
        {
            int index = Array.IndexOf(e.Args, "--ui-test");
            var output = index + 1 < e.Args.Length ? e.Args[index + 1] : "artifacts/ui";
            Dispatcher.BeginInvoke(new Action(() => UiTest.Run(this, panel, Path.GetFullPath(output))), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    internal void ApplyTheme(string name)
    {
        bool light = name == "Light";
        string[] keys = { "Background", "Surface", "Hover", "Border", "Text", "Muted", "Subtle" };
        string[] colors = light
            ? new[] { "#EDEDED", "#F7F7F7", "#E1E1E1", "#CDCDCD", "#171717", "#626262", "#707070" }
            : new[] { "#050505", "#111111", "#1D1D1D", "#292929", "#EDEDED", "#A0A0A0", "#787878" };
        for (int i = 0; i < keys.Length; i++)
        {
            // Update the stable binding source, not the resource dictionary entry.
            // This notifies both visible and retained/hidden brush consumers.
            ((ThemeColor)Resources[keys[i] + "Color"]).Value = (Color)ColorConverter.ConvertFromString(colors[i]);
        }
        LogoImage = LoadBrandImage(light ? "wordmark-black.png" : "wordmark-white.png");
        WindowIconImage = LoadBrandImage(light ? "icon-black.png" : "icon-white.png");
        panel?.RefreshTheme();
    }

    internal void ApplyLocale(Locale locale)
    {
        Locale = locale;
        foreach (var item in locale.Values)
            Resources[item.Key] = item.Value;
    }

    private static ImageSource LoadBrandImage(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Brand/" + fileName, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = fileName.StartsWith("wordmark", StringComparison.Ordinal) ? 512 : 64;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal void ExitApp()
    {
        if (panel is not null && !panel.TrySave()) return;
        Exiting = true; Shutdown();
    }
}
