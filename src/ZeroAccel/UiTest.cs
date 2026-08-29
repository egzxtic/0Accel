using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZeroAccel;

/* Explicit offline UI test mode; uses an isolated settings directory, no tray,
 * no autostart writes and no synthetic input. Captures the actual WPF visual. */
internal static class UiTest
{
    internal static async void Run(App app, MainWindow window, string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            window.DpiBox.Text = "1600";
            await AssertActionButtons(app, window, output);
            await AssertThemeSwitches(app, window, output);
            AssertCurveModes(app, window, output);
            AssertChartRanges(app, window, output);
            Capture(window, Path.Combine(output, "dark.png"));
            AssertNoFocusOutline(window, output, "dark");
            window.SensitivitySlider.Value = 1.25;
            window.AccelerationSlider.Value = .035;
            window.LimitSlider.Value = 2;
            if (!window.TrySave()) throw new Exception("Settings save failed");
            var read = app.Store.Load();
            if (Math.Abs(read.Sensitivity-1.25) > .0001 || Math.Abs(read.Acceleration-.035) > .0001) throw new Exception("Settings round trip failed");
            window.SensitivitySlider.Value = 1;
            window.AccelerationSlider.Value = .02;
            window.LimitSlider.Value = 1.5;
            app.Settings = app.Settings with { Theme = "Light" };
            app.ApplyTheme("Light");
            Capture(window, Path.Combine(output, "light.png"));
            AssertNoFocusOutline(window, output, "light");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            window.DpiBox.Text = "100000";
            Capture(window, Path.Combine(output, "compact.png"));
            if (!window.Title.StartsWith("0Accel (")) throw new Exception("Live size title missing");
            window.Width = 1280; window.Height = 720;
            window.DpiBox.Text = "1600";
            app.Settings = app.Settings with { Theme = "Dark" }; app.ApplyTheme("Dark");
            Capture(window, Path.Combine(output, "wide.png"));
            window.DpiBox.Text = "wrong";
            if (window.TrySave()) throw new Exception("Invalid DPI was accepted");
            window.DpiBox.Text = "1600";
            if (!window.TrySave() || app.Store.Load().Dpi != 1600) throw new Exception("DPI save failed");
            window.Width = 900; window.Height = 720;
            window.ShowLastMouseMoveCheck.IsChecked = true;
            window.ShowLastMouseMoveCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            // Deterministic rendering fixtures only; no OS mouse movement is injected.
            window.Curve.SetLastMove(40);
            Capture(window, Path.Combine(output, "motion-dark.png"));
            AssertMarker(window.Curve, 40);
            var lastPoint = window.Curve.LastMovePoint;
            window.Curve.SetLastMove(double.NaN); window.Curve.SetLastMove(-1);
            if (window.Curve.LastMovePoint != lastPoint) throw new Exception("Invalid speed changed the marker");
            app.Settings = app.Settings with { Theme = "Light" }; app.ApplyTheme("Light");
            Capture(window, Path.Combine(output, "motion-light.png"));
            AssertMarker(window.Curve, 40);
            window.Curve.SetLastMove(1000); AssertMarker(window.Curve, window.Curve.XMaximum);
            window.Curve.SetLastMove(0); AssertMarker(window.Curve, 0);
            window.ShowLastMouseMoveCheck.IsChecked = false;
            window.ShowLastMouseMoveCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (window.Curve.LastMovePoint is not null || window.IsMotionTracking || window.IsRawInputRegistered)
                throw new Exception("Disabling the marker did not clear it and stop Raw Input");
            await AssertSettingsLayout(app, window, output);
            AssertAboutDialog(app, window, output);
            if (window.MeasureButton.IsEnabled)
            {
                window.Activate();
                window.ShowLastMouseMoveCheck.IsChecked = true;
                window.ShowLastMouseMoveCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (!window.IsMotionTracking || !window.IsRawInputRegistered) throw new Exception("Live marker registration failed");
                window.MeasureButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if ((string)window.MeasureButton.Content != app.T("S_Cancel")) throw new Exception("Raw Input registration failed");
                await Task.Delay(3500);
                if ((string)window.MeasureButton.Content != app.T("S_Measure")) throw new Exception("Measurement did not stop");
                if (!window.IsMotionTracking || !window.IsRawInputRegistered)
                    throw new Exception($"Ending Hz measurement stopped the live marker: active={window.IsActive}, enabled={window.IsEnabled}, checked={window.ShowLastMouseMoveCheck.IsChecked}, registered={window.IsRawInputRegistered}, status={window.StatusDetail.Text}");
                // A second test window causes real deactivation without input simulation.
                var other = new Window { Width = 100, Height = 100, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
                try
                {
                    other.Show(); other.Activate();
                    await Task.Delay(100);
                    if (window.IsActive || window.IsMotionTracking || window.IsRawInputRegistered)
                        throw new Exception("Leaving the panel kept Raw Input active");
                }
                finally { other.Close(); }
                window.Activate();
                if (!window.IsMotionTracking) throw new Exception("Returning to the panel did not resume the live marker");
                window.MeasureButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.ShowLastMouseMoveCheck.IsChecked = false;
                window.ShowLastMouseMoveCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (window.IsMotionTracking || !window.IsRawInputRegistered || (string)window.MeasureButton.Content != app.T("S_Cancel"))
                    throw new Exception("Disabling the marker interrupted the Hz measurement");
                window.MeasureButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (window.IsRawInputRegistered) throw new Exception("Raw Input remained after both consumers stopped");
            }
            if (!File.Exists(AppPaths.Launcher)) throw new Exception("Packaged launcher missing");
            AssertDriverPanel(app,window,output);
            File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: Raw Accel user-mode bridge load, embedded branding, packaged launcher, dark/light rendering, 32px action buttons and padded hover bounds, long device names in PL/EN at 780/900px, focus borders, aligned/non-overlapping settings, mode-specific fields and scroll reachability at 780x650 and 900x720, Gain native bridge, themed dropdowns and selection persistence, settings round trip, unclipped DPI, live marker and Raw Input lifecycle (if device present), Raw Accel action/read-active fixtures in PL/EN and Dark/Light at 780x650. TestMode blocks driver I/O; motion/status fixtures are not kernel tests.\n");
            app.Shutdown(0);
        }
        catch (Exception e)
        { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "result.txt"), "FAIL: " + e); app.Shutdown(1); }
    }

    private static void AssertDriverPanel(App app, MainWindow window, string output)
    {
        // Fixtures only. TestMode blocks every native read/write driver action.
        var snapshot = RawAccelProtocol.Decode(RawAccelProtocol.Default());
        foreach (string language in new[] { "pl-PL", "en-US" })
        foreach (string theme in new[] { "Dark", "Light" })
        {
            app.ApplyLocale(Locale.Detect(CultureInfo.GetCultureInfo(language)));
            app.Settings = app.Settings with { Theme = theme }; app.ApplyTheme(theme);
            window.Width = 780; window.Height = 650;
            window.SetDriverStatus(snapshot);
            window.UpdateLayout();
            if (window.DriverActions.Visibility != Visibility.Visible)
                throw new Exception("Connected driver actions missing");
            window.DriverApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            foreach (var button in new[] { window.DriverApplyButton, window.DriverReadButton })
            {
                var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                if (bounds.Left < 0 || bounds.Right > window.ActualWidth || bounds.Bottom > window.ActualHeight || button.ActualHeight < 28)
                    throw new Exception("Driver action clipped: " + button.Name);
            }
            Capture(window,Path.Combine(output,$"driver-{language}-{theme}.png"));
            if (!window.SettingsScroll.IsEnabled || !window.ImportButton.IsEnabled)
                throw new Exception("Idle Raw Accel panel should allow editing");
        }
    }

    private static void Capture(MainWindow window, string path)
    {
        window.UpdateLayout();
        if (window.LogoImage.Source is not BitmapSource logo || logo.PixelWidth == 0 || window.Icon is not BitmapSource icon || icon.PixelWidth == 0)
            throw new Exception("Embedded branding missing");
        bool light = ((App)Application.Current).Settings.Theme == "Light";
        foreach (var brand in new[] { logo, icon })
        {
            var pixels = new FormatConvertedBitmap(brand, PixelFormats.Bgra32, null, 0);
            var buffer = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
            pixels.CopyPixels(buffer, pixels.PixelWidth * 4, 0);
            long brightness = 0, samples = 0;
            for (int i = 0; i < buffer.Length; i += 4)
                if (buffer[i + 3] > 200) { brightness += buffer[i] + buffer[i + 1] + buffer[i + 2]; samples++; }
            if (samples == 0 || (brightness / (samples * 3) < 128) != light)
                throw new Exception("Branding has incorrect theme contrast");
        }
        var dpi = VisualTreeHelper.GetDpi(window);
        string expectedTitle = $"0Accel ({(int)Math.Round(window.ActualWidth*dpi.DpiScaleX)}×{(int)Math.Round(window.ActualHeight*dpi.DpiScaleY)})";
        if (window.Title != expectedTitle) throw new Exception("Incorrect size title: " + window.Title);
        foreach (var control in new FrameworkElement[] { window.ResetButton, window.SaveButton, window.ImportButton, window.ExportButton, window.AboutButton, window.MeasureButton, window.DpiBox, window.ShowLastMouseMoveCheck, window.CurveModeCombo })
        {
            var point = control.TranslatePoint(new Point(), window);
            if (point.X < 0 || point.Y < 0 || point.X+control.ActualWidth > window.ActualWidth || point.Y+control.ActualHeight > window.ActualHeight)
                throw new Exception("Control outside window: " + control.Name);
        }
        foreach (var button in new[] { window.AboutButton, window.ImportButton, window.ExportButton, window.SaveButton })
        {
            if (string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)))
                throw new Exception("Missing accessible button name: " + button.Name);
            var content = (FrameworkElement)button.Content;
            var bounds = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
            if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > button.ActualWidth || bounds.Bottom > button.ActualHeight)
                throw new Exception("Clipped icon/button content: " + button.Name);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        AssertThemePalette(window, light ? "Light" : "Dark");
        AssertThemePixels(window, bitmap, light);
        AssertDpiTextVisible(window.DpiBox);
    }

    private static async Task AssertActionButtons(App app, MainWindow window, string output)
    {
        var originalLocale = app.Locale;
        string originalTheme = app.Settings.Theme;
        object originalDevice = window.DeviceButton.Content;
        double originalWidth = window.Width, originalHeight = window.Height;
        var buttons = new[] { window.AboutButton, window.ThemeButton, window.MinimizeButton,
            window.CloseButton, window.RefreshDevicesButton, window.DeviceButton, window.ResetButton, window.MeasureButton };
        try
        {
            foreach (string language in new[] { "pl-PL", "en-US" })
            foreach (string theme in new[] { "Dark", "Light" })
            foreach (int width in new[] { 900, 780 })
            {
                app.ApplyLocale(Locale.Detect(CultureInfo.GetCultureInfo(language)));
                app.Settings = app.Settings with { Theme = theme }; app.ApplyTheme(theme);
                window.Width = width; window.Height = width == 780 ? 650 : 720;
                foreach (bool longName in new[] { false, true })
                {
                    window.DeviceButton.Content = longName
                        ? "WLMouse receiver / " + new string('W', 140)
                        : "WLMOUSE BEAST X PRO 8K RECEIVER";
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    AssertActionGeometry(window, buttons);
                    // Preview the same hover fill without moving/clicking the OS mouse.
                    var backgrounds = buttons.Select(button => button.Background).ToArray();
                    try
                    {
                        foreach (var button in buttons)
                            button.SetCurrentValue(Control.BackgroundProperty, app.FindResource("HoverBrush"));
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertActionGeometry(window, buttons);
                        Capture(window, Path.Combine(output, $"button-bounds-{language}-{theme}-{width}-{(longName ? "long" : "normal")}.png"));
                    }
                    finally
                    {
                        for (int i = 0; i < buttons.Length; i++)
                            buttons[i].SetCurrentValue(Control.BackgroundProperty, backgrounds[i]);
                    }
                }
            }
        }
        finally
        {
            window.DeviceButton.Content = originalDevice;
            window.Width = originalWidth; window.Height = originalHeight;
            app.ApplyLocale(originalLocale);
            app.Settings = app.Settings with { Theme = originalTheme }; app.ApplyTheme(originalTheme);
            window.UpdateLayout();
        }
    }

    private static void AssertActionGeometry(MainWindow window, Button[] buttons)
    {
        var icons = new[] { window.AboutButton, window.ThemeButton, window.MinimizeButton,
            window.CloseButton, window.RefreshDevicesButton };
        foreach (var button in buttons)
        {
            if (Math.Abs(button.ActualHeight - 32) > .5 ||
                (icons.Contains(button) && Math.Abs(button.ActualWidth - 32) > .5))
                throw new Exception("Inconsistent action-button dimensions: " + button.Name);
            var frame = (Border)button.Template.FindName("Frame", button);
            var frameBounds = frame.TransformToAncestor(button).TransformBounds(new Rect(frame.RenderSize));
            if (Math.Abs(frameBounds.Left) > .5 || Math.Abs(frameBounds.Top) > .5 ||
                Math.Abs(frameBounds.Width - button.ActualWidth) > .5 || Math.Abs(frameBounds.Height - 32) > .5)
                throw new Exception("Hover frame does not fill button: " + button.Name);
            var content = FindVisual<ContentPresenter>(button) ?? throw new Exception("Button content missing");
            var bounds = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
            double horizontalInset = icons.Contains(button) ? 5 : 10;
            if (bounds.Left < horizontalInset - .5 || bounds.Right > button.ActualWidth - horizontalInset + .5 ||
                bounds.Top < 3 || bounds.Bottom > button.ActualHeight - 3)
                throw new Exception($"Cramped button content: {button.Name}, {bounds}, size={button.RenderSize}");
            var screenBounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            if (screenBounds.Left < 0 || screenBounds.Right > window.ActualWidth ||
                screenBounds.Top < 0 || screenBounds.Bottom > window.ActualHeight)
                throw new Exception("Action button outside window: " + button.Name);
        }
        double previousRight = -1, headerTop = -1;
        foreach (var button in icons.Take(4))
        {
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            if (previousRight >= 0 && (Math.Abs(bounds.Left - previousRight - 8) > .5 || Math.Abs(bounds.Top - headerTop) > .5))
                throw new Exception("Unequal header icon spacing/alignment");
            previousRight = bounds.Right; headerTop = bounds.Top;
        }
        var dpi = window.DpiBox.TransformToAncestor(window).TransformBounds(new Rect(window.DpiBox.RenderSize));
        foreach (var control in new[] { window.DeviceButton, window.RefreshDevicesButton, window.MeasureButton })
        {
            var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
            if (Math.Abs(bounds.Top + bounds.Height / 2 - dpi.Top - dpi.Height / 2) > .5)
                throw new Exception("Misaligned device toolbar: " + control.Name);
        }
        var name = FindVisual<TextBlock>(window.DeviceButton) ?? throw new Exception("Device label missing");
        if (name.Text != (string)window.DeviceButton.Content || name.TextTrimming != TextTrimming.CharacterEllipsis || name.TextWrapping != TextWrapping.NoWrap)
            throw new Exception("Device label must preserve its value and trim long display text");
        var label = name.TransformToAncestor(window.DeviceButton).TransformBounds(new Rect(name.RenderSize));
        if (label.Left < 9.5 || label.Right > window.DeviceButton.ActualWidth - 25.5 || name.ActualHeight > 26)
            throw new Exception("Device text intrudes on padding/dropdown arrow");
        var device = window.DeviceButton.TransformToAncestor(window).TransformBounds(new Rect(window.DeviceButton.RenderSize));
        var refresh = window.RefreshDevicesButton.TransformToAncestor(window).TransformBounds(new Rect(window.RefreshDevicesButton.RenderSize));
        if (Math.Abs(refresh.Left - device.Right - 8) > .5 || refresh.Right > dpi.Left - 15.5)
            throw new Exception("Device name displaced refresh/DPI controls");
    }

    private static async Task AssertThemeSwitches(App app, MainWindow window, string output)
    {
        // Exercise the same button handler as the user, not just ApplyTheme.
        // A valid logo and readable text do not prove the requested palette loaded.
        foreach (string mode in new[] { "off", "classic", "natural", "linear" })
        {
            SelectCurveMode(window, mode);
            for (int i = 0; i < 2; i++)
            {
                string expected = app.Settings.Theme == "Dark" ? "Light" : "Dark";
                window.ThemeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (app.Settings.Theme != expected || window.ThemeGlyph.Text != (expected == "Light" ? "☾" : "☀"))
                    throw new Exception("Theme button/settings disagree");
                Capture(window, Path.Combine(output, $"theme-toggle-{mode}-{expected}.png"));
                if (!window.TrySave() || app.Store.Load().Theme != expected)
                    throw new Exception("Theme selection did not persist");
            }
        }
        window.ResetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static void AssertThemePalette(MainWindow window, string theme)
    {
        string[] keys = { "Background", "Surface", "Hover", "Border", "Text", "Muted", "Subtle" };
        string[] expected = theme == "Light"
            ? new[] { "#EDEDED", "#F7F7F7", "#E1E1E1", "#CDCDCD", "#171717", "#626262", "#707070" }
            : new[] { "#050505", "#111111", "#1D1D1D", "#292929", "#EDEDED", "#A0A0A0", "#787878" };
        for (int i = 0; i < keys.Length; i++)
            if (window.FindResource(keys[i] + "Brush") is not SolidColorBrush brush
                || brush.Color != (Color)ColorConverter.ConvertFromString(expected[i]))
                throw new Exception($"Wrong {theme} palette: {keys[i]} should be {expected[i]}");
    }

    private static void AssertThemePixels(MainWindow window, RenderTargetBitmap bitmap, bool light)
    {
        var field = window.DpiBox.TransformToAncestor(window).TransformBounds(new Rect(window.DpiBox.RenderSize));
        var samples = new[] { (X: 10, Y: 90, Color: light ? 237 : 5),
            (X: (int)field.Left + 6, Y: (int)field.Top + 6, Color: light ? 247 : 17) };
        foreach (var sample in samples)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(sample.X, sample.Y, 1, 1), pixel, 4, 0);
            if (pixel[3] != 255 || pixel.Take(3).Any(value => Math.Abs(value - sample.Color) > 2))
                throw new Exception($"Rendered theme mismatch at {sample.X},{sample.Y}; expected {sample.Color}, got {pixel[2]},{pixel[1]},{pixel[0]}");
        }
    }

    private static void AssertAboutDialog(App app, MainWindow window, string output)
    {
        var originalLocale = app.Locale;
        string originalTheme = app.Settings.Theme;
        try
        {
            foreach (string language in new[] { "pl-PL", "en-US" })
            foreach (string theme in new[] { "Dark", "Light" })
            {
                app.ApplyLocale(Locale.Detect(CultureInfo.GetCultureInfo(language)));
                app.Settings = app.Settings with { Theme = theme }; app.ApplyTheme(theme);
                Capture(window, Path.Combine(output, $"actions-{language}-{theme}.png"));
                Exception? failure = null;
                bool opened = false;
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = app.Windows.OfType<AboutWindow>().SingleOrDefault();
                    try
                    {
                        if (dialog is null || dialog.Owner != window || !dialog.IsVisible)
                            throw new Exception("About button did not open an owned modal dialog");
                        opened = true;
                        if (!dialog.VersionLabel.Text.Contains(typeof(App).Assembly.GetName().Version!.ToString(3)) ||
                            dialog.Title != app.T("S_About") || !dialog.DismissButton.IsCancel)
                            throw new Exception("About version, translation or Escape action missing");
                        dialog.UpdateLayout();
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(RenderControl(dialog)));
                        using var stream = File.Create(Path.Combine(output, $"about-{language}-{theme}.png"));
                        encoder.Save(stream);
                    }
                    catch (Exception error) { failure = error; }
                    finally { dialog?.DismissButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
                }), DispatcherPriority.ApplicationIdle);
                window.AboutButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (failure is not null) throw failure;
                if (!opened || app.Windows.OfType<AboutWindow>().Any() || !window.IsEnabled)
                    throw new Exception("About dialog did not close cleanly");
            }
        }
        finally
        {
            app.ApplyLocale(originalLocale);
            app.Settings = app.Settings with { Theme = originalTheme }; app.ApplyTheme(originalTheme);
        }
    }

    private static void SelectCurveMode(MainWindow window, string mode) =>
        window.CurveModeCombo.SelectedItem = window.CurveModeCombo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == mode);

    private static void AssertCurveModes(App app, MainWindow window, string output)
    {
        if ((string)((ComboBoxItem)window.CurveModeCombo.SelectedItem).Tag != "off")
            throw new Exception("New panel must start in OFF");
        if (!window.CurveModeCombo.Items.Cast<ComboBoxItem>().Select(i => (string)i.Tag).SequenceEqual(new[] { "off", "linear", "classic", "natural" }))
            throw new Exception("Unexpected curve modes");
        window.SensitivitySlider.Value = 1.5;
        window.YxRatioBox.Text = "2";
        window.AccelerationSlider.Value = .05;
        window.LimitSlider.Value = 3;
        window.CapOutputBox.Text = "10";
        window.CapInputBox.Text = "80";
        window.PowerBox.Text = "2.5";
        window.DecayBox.Text = "0.15";
        foreach (string theme in new[] { "Dark", "Light" })
        foreach (int width in new[] { 900, 780 })
        {
            app.Settings = app.Settings with { Theme = theme }; app.ApplyTheme(theme);
            window.Width = width; window.Height = width == 780 ? 650 : 720;
            foreach (string mode in new[] { "linear", "classic", "natural", "off" })
            {
                SelectCurveMode(window, mode);
                window.UpdateLayout();
                bool off = mode == "off", natural = mode == "natural", capped = !off && !natural;
                foreach (var (row, visible) in new[] {
                    (window.GainRow, !off), (window.AccelerationRow, capped), (window.CapTypeRow, capped),
                    (window.InputOffsetRow, !off), (window.LimitRow, natural), (window.DecayRow, natural),
                    (window.PowerRow, mode == "classic"), (window.RotationRow, true), (window.YxRatioRow, true) })
                    if (row.IsVisible != visible) throw new Exception("Wrong visibility: " + mode + "/" + row.Name);
                if (!window.CurveModeCombo.IsVisible || !window.SensitivityBox.IsVisible)
                    throw new Exception("Base settings unavailable");
                if (window.CurveModeLabel.Text != (string)((ComboBoxItem)window.CurveModeCombo.SelectedItem).Content)
                    throw new Exception("Chart label does not match mode");
                if (!window.TrySave()) throw new Exception("Mode save failed");
                var saved = app.Store.Load();
                if (saved.CurveMode != mode || saved.Acceleration != .05 || saved.CapOutput != 10 || saved.Power != 2.5 || saved.DecayRate != .15)
                    throw new Exception("Mode switch lost hidden values");
                if (off)
                {
                    foreach (double speed in new[] { 0, 1, 15, 40, 100, 65535 })
                        if (Math.Abs(window.Curve.EvaluateGain(speed) - 1.5) > .0001)
                            throw new Exception("OFF is not constant or Y/X incorrectly affects X");
                }
                else
                {
                    if (window.Curve.EvaluateGain(0) >= window.Curve.EvaluateGain(100))
                        throw new Exception("Active curve does not respond to velocity");
                    // Linear/Classic only differ once their cap is reached.
                    double gainOn = window.Curve.EvaluateGain(1000);
                    window.GainCheck.IsChecked = false;
                    window.GainCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    if (window.Curve.Config.Gain != 0 || Math.Abs(gainOn - window.Curve.EvaluateGain(1000)) < .000001)
                        throw new Exception("Gain switch did not reach native curve");
                    window.GainCheck.IsChecked = true;
                    window.GainCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                Capture(window, Path.Combine(output, $"mode-{mode}-{theme}-{width}.png"));
            }
        }
        SelectCurveMode(window, "classic");
        foreach (ComboBoxItem cap in window.CapTypeCombo.Items)
        {
            window.CapTypeCombo.SelectedItem = cap;
            string type = (string)cap.Tag;
            if (window.CapInputRow.IsVisible != (type is "Input" or "Both") ||
                window.CapOutputRow.IsVisible != (type is "Output" or "Both"))
                throw new Exception("Wrong cap rows");
            if (window.AccelerationBox.IsEnabled != (type != "Both") || window.AccelerationSlider.IsEnabled != (type != "Both"))
                throw new Exception("Both must not accept manual acceleration");
        }
        window.ResetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (window.Curve.Config.Mode != 0 || window.AccelerationRow.IsVisible || !window.RotationRow.IsVisible || !window.TrySave() || app.Store.Load().CurveMode != "off")
            throw new Exception("Reset defaults does not persist OFF");
        SelectCurveMode(window, "linear");
        window.Width = 900; window.Height = 720;
        app.Settings = app.Settings with { Theme = "Dark" }; app.ApplyTheme("Dark");
    }

    private static void AssertChartRanges(App app, MainWindow window, string output)
    {
        window.SensitivitySlider.Value = 1;
        window.AccelerationSlider.Value = .05;
        window.CapOutputBox.Text = "1.5";
        window.InputOffsetBox.Text = "15";
        window.GainCheck.IsChecked = true;
        window.GainCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.ShowLastMouseMoveCheck.IsChecked = true;
        window.ShowLastMouseMoveCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        foreach (string theme in new[] { "Dark", "Light" })
        {
            app.Settings = app.Settings with { Theme = theme }; app.ApplyTheme(theme);
            window.Curve.SetLastMove(40);
            Capture(window, Path.Combine(output, $"curve-reference-{theme}.png"));
            var curve = window.Curve;
            if (curve.XMaximum != 80 || curve.YMinimum >= 1 || curve.YMaximum <= 1.390625 || curve.YMaximum - curve.YMinimum > 1)
                throw new Exception("FPS multiplier range is not readable");
            if (Math.Abs(curve.EvaluateGain(40) - 1.28125) > 1e-12)
                throw new Exception("Screenshot reference settings diverged from the curve contract");
            var axes = (curve.XMaximum, curve.YMinimum, curve.YMaximum);
            foreach (double speed in new[] { 0, 15, 20, 40, 80, 65535 })
            {
                curve.SetLastMove(speed); AssertMarker(curve, Math.Min(speed, curve.XMaximum));
                if (axes != (curve.XMaximum, curve.YMinimum, curve.YMaximum))
                    throw new Exception("Live motion rescaled the chart");
            }
        }
        window.InputOffsetBox.Text = "200";
        window.CapInputBox.Text = "1600";
        window.CapTypeCombo.SelectedItem = window.CapTypeCombo.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == "Both");
        Capture(window, Path.Combine(output, "curve-high-offset.png"));
        if (window.Curve.XMaximum != 1760 || window.Curve.EvaluateGain(200) != 1 || window.Curve.EvaluateGain(1600) <= 1)
            throw new Exception("High offset/input cap is hidden by the chart");
        double derived = window.Curve.EvaluateGain(1600);
        window.AccelerationSlider.Value = .17;
        if (window.Curve.EvaluateGain(1600) != derived) throw new Exception("Both uses manual acceleration");
        window.CapInputBox.Text = "200";
        if (window.TrySave() || window.StatusDetail.Text != app.T("M_CapBelowOffset"))
            throw new Exception("Invalid input cap was accepted");
        window.ResetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (window.StatusDetail.Text == app.T("M_CapBelowOffset")) throw new Exception("Reset kept a stale cap warning");
        foreach (double sensitivity in new[] { .1, 24 })
        {
            window.SensitivitySlider.Value = sensitivity;
            Capture(window, Path.Combine(output, $"curve-off-{sensitivity.ToString(CultureInfo.InvariantCulture)}.png"));
            if (window.Curve.XMaximum != 80 || window.Curve.YMinimum >= sensitivity || window.Curve.YMaximum <= sensitivity)
                throw new Exception("Constant curve has no axis padding");
        }
        window.ResetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        SelectCurveMode(window, "linear");
        app.Settings = app.Settings with { Theme = "Dark" }; app.ApplyTheme("Dark");
    }

    private static async Task AssertSettingsLayout(App app, MainWindow window, string output)
    {
        foreach (string theme in new[] { "Dark", "Light" })
        foreach (int width in new[] { 900, 780 })
        foreach (string mode in new[] { "off", "linear", "classic", "natural" })
        {
            app.Settings = app.Settings with { Theme = theme }; app.ApplyTheme(theme);
            window.Width = width; window.Height = width == 780 ? 650 : 720;
            SelectCurveMode(window, mode);
            window.SettingsScroll.ScrollToTop();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window.SettingsScroll.ScrollableWidth > .5) throw new Exception("Horizontal settings overflow");
            int originalCap = window.CapTypeCombo.SelectedIndex;
            foreach (ComboBoxItem cap in window.CapTypeCombo.Items)
            {
                window.CapTypeCombo.SelectedItem = cap;
                window.UpdateLayout();
                AssertUniformSettingsSpacing(window);
            }
            window.CapTypeCombo.SelectedIndex = originalCap;
            window.UpdateLayout();
            double right = -1, previousBottom = -1;
            foreach (var field in new FrameworkElement[] { window.SensitivityBox, window.YxRatioBox, window.RotationCombo,
                window.AccelerationBox, window.DecayBox, window.CapTypeCombo, window.CapOutputBox, window.CapInputBox,
                window.InputOffsetBox, window.LimitBox, window.PowerBox })
            {
                if (!field.IsVisible) continue;
                var rect = field.TransformToAncestor(window.SettingsContent).TransformBounds(new Rect(field.RenderSize));
                if (right >= 0 && Math.Abs(rect.Right - right) > .5) throw new Exception("Unaligned field: " + field.Name);
                right = rect.Right;
                if (rect.Top < previousBottom) throw new Exception("Overlapping field: " + field.Name);
                previousBottom = rect.Bottom;
                field.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var viewport = (ScrollContentPresenter)window.SettingsScroll.Template.FindName("PART_ScrollContentPresenter", window.SettingsScroll);
                var visible = field.TransformToAncestor(viewport).TransformBounds(new Rect(field.RenderSize));
                if (visible.Left < -.5 || visible.Top < -.5 || visible.Right > viewport.ActualWidth + .5 || visible.Bottom > viewport.ActualHeight + .5)
                    throw new Exception("Unreachable setting: " + field.Name);
                if (field is TextBox box)
                {
                    AssertTextFits(box);
                    // The geometry query can invalidate the text drawing. Check
                    // actual window pixels after render, while this field is in view.
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (box.IsEnabled) AssertTextContrast(window, box);
                }
            }
            // Character-rectangle queries above can invalidate WPF's text view.
            // Let its render pass finish before capturing the final field.
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // Capture the bottom of the two longest forms; mode remains pinned above scrolling.
            if (mode is "classic" or "natural") Capture(window, Path.Combine(output, $"fields-{mode}-{theme}-{width}.png"));
        }
        window.Width = 900; window.Height = 720;
        SelectCurveMode(window, "classic");
        foreach (var combo in new[] { window.RotationCombo, window.CurveModeCombo, window.CapTypeCombo })
        {
            combo.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            int original = combo.SelectedIndex;
            ToggleTemplateButton((ToggleButton)combo.Template.FindName("DropDownToggle", combo));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            if (!popup.IsOpen || popup.Child is not Border dropdown || !dropdown.IsVisible ||
                dropdown.Background is not SolidColorBrush fill || fill.Color != ((SolidColorBrush)app.FindResource("SurfaceBrush")).Color)
                throw new Exception("Dropdown unavailable or wrong theme");
            combo.SelectedIndex = combo.Items.Count - 1;
            combo.IsDropDownOpen = false;
            if (!window.TrySave()) throw new Exception("Dropdown save failed");
            var saved = app.Store.Load();
            string actual = combo == window.RotationCombo ? saved.Rotation.ToString() : combo == window.CurveModeCombo ? saved.CurveMode : saved.CapType;
            if (actual != (string)((ComboBoxItem)combo.SelectedItem).Tag) throw new Exception("Dropdown selection not persisted");
            combo.SelectedIndex = original;
        }
        SelectCurveMode(window, "linear");
        window.SettingsScroll.ScrollToTop();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void AssertUniformSettingsSpacing(MainWindow window)
    {
        double previousBottom = -1;
        foreach (var field in new FrameworkElement[] { window.CurveModeCombo, window.SensitivityBox,
            window.YxRatioBox, window.RotationCombo, window.GainCheck, window.AccelerationBox,
            window.DecayBox, window.CapTypeCombo, window.CapOutputBox, window.CapInputBox,
            window.InputOffsetBox, window.LimitBox, window.PowerBox })
        {
            if (!field.IsVisible) continue;
            var bounds = field.TransformToAncestor(window.SettingsPanel).TransformBounds(new Rect(field.RenderSize));
            double expectedHeight = field == window.GainCheck ? 18 : 30;
            if (Math.Abs(bounds.Height - expectedHeight) > .5) throw new Exception("Unequal field height: " + field.Name);
            if (previousBottom >= 0 && Math.Abs(bounds.Top - previousBottom - 6) > .5)
                throw new Exception($"Unequal settings gap before {field.Name}: {bounds.Top - previousBottom} instead of 6");
            previousBottom = bounds.Bottom;
        }
        foreach (var slider in new[] { window.SensitivitySlider, window.AccelerationSlider, window.LimitSlider })
        {
            if (!slider.IsVisible) continue;
            var stack = (StackPanel)slider.Parent;
            var row = (Grid)stack.Parent;
            var bounds = stack.TransformToAncestor(row).TransformBounds(new Rect(stack.RenderSize));
            if (bounds.Top < -.5 || bounds.Bottom > row.ActualHeight + .5)
                throw new Exception("Slider label/track exceeds its row: " + slider.Name);
        }
    }

    private static void AssertTextFits(TextBox box)
    {
        var viewport = FindVisual<ScrollContentPresenter>(box) ?? throw new Exception("Text viewport missing: " + box.Name);
        var bounds = viewport.TransformToAncestor(box).TransformBounds(new Rect(viewport.RenderSize));
        for (int i = 0; i < box.Text.Length; i++)
        {
            var first = box.GetRectFromCharacterIndex(i, false);
            var last = box.GetRectFromCharacterIndex(i, true);
            if (first.IsEmpty || first.Top < bounds.Top - .5 || first.Bottom > bounds.Bottom + .5 || first.Left < bounds.Left - .5 || last.Right > bounds.Right + .5)
                throw new Exception("Clipped settings value: " + box.Name);
        }
    }

    private static void AssertTextContrast(MainWindow window, TextBox box)
    {
        if (string.IsNullOrWhiteSpace(box.Text) || box.Background is not SolidColorBrush background)
            throw new Exception("Missing numeric value/background: " + box.Name);
        var bitmap = RenderControl(window);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var bounds = box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize));
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        int contrasting = 0;
        // Ignore borders. Look for actual glyph pixels, not just a Foreground
        // property whose cached text drawing might still use the old theme.
        for (int y = (int)bounds.Top + 5; y < (int)bounds.Bottom - 5; y++)
            for (int x = (int)bounds.Left + 5; x < (int)bounds.Right - 5; x++)
            {
                int i = (y * width + x) * 4;
                int delta = Math.Abs(pixels[i] - background.Color.B)
                    + Math.Abs(pixels[i + 1] - background.Color.G)
                    + Math.Abs(pixels[i + 2] - background.Color.R);
                if (pixels[i + 3] > 200 && delta > 300) contrasting++;
            }
        if (contrasting < 6) throw new Exception($"Unreadable numeric text: {box.Name}, theme={((App)Application.Current).Settings.Theme}, width={window.Width}, mode={window.CurveModeCombo.Text}, bounds={bounds}, pixels={contrasting}, text='{box.Text}', fg={box.Foreground}, bg={box.Background}, opacity={box.Opacity}, enabled={box.IsEnabled}, selection={box.SelectionLength}, themeFrozen={((Brush)window.FindResource("TextBrush")).IsFrozen}");
    }

    private static void ToggleTemplateButton(ToggleButton button)
    {
        var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(button);
        var provider = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
        provider.Toggle();
    }

    private static void AssertMarker(CurveView curve, double speed)
    {
        var bounds = curve.PlotBounds;
        var expected = new Point(bounds.Left + bounds.Width * speed / curve.XMaximum,
            bounds.Bottom - (curve.EvaluateGain(speed) - curve.YMinimum) / (curve.YMaximum - curve.YMinimum) * bounds.Height);
        if (curve.LastMovePoint is not Point point || (point - expected).Length > .01)
            throw new Exception($"Marker is not on the preview curve: expected {expected}, got {curve.LastMovePoint}");
    }

    private static void AssertNoFocusOutline(MainWindow window, string output, string theme)
    {
        window.Activate();
        foreach (var control in new Control[] { window.StartInTrayBox, window.AutoStartBox, window.ShowLastMouseMoveCheck,
            window.DpiBox, window.SensitivityBox, window.AccelerationBox, window.LimitBox, window.YxRatioBox,
            window.YxRatioBox, window.SaveButton, window.ImportButton, window.ExportButton,
            window.SensitivitySlider, window.AccelerationSlider, window.LimitSlider })
        {
            if (!control.IsEnabled || !control.IsVisible) continue;
            control.BringIntoView();
            window.UpdateLayout();
            // Blur the target without changing its value or toggling a user setting.
            (control == window.DpiBox ? window.SensitivityBox : window.DpiBox).Focus();
            window.UpdateLayout();
            var before = RenderControl(control);
            if (!control.Focus() || !control.IsKeyboardFocused) throw new Exception("Keyboard focus unavailable: " + control.Name);
            window.UpdateLayout();
            if (control.FocusVisualStyle is not null) throw new Exception("System focus rectangle enabled: " + control.Name);
            var after = RenderControl(control);
            if (before.PixelWidth != after.PixelWidth || before.PixelHeight != after.PixelHeight)
                throw new Exception("Focus changed control size: " + control.Name);
            int width = after.PixelWidth, height = after.PixelHeight;
            var oldPixels = new byte[width * height * 4];
            var newPixels = new byte[oldPixels.Length];
            before.CopyPixels(oldPixels, width * 4, 0); after.CopyPixels(newPixels, width * 4, 0);
            // The caret/selection may change inside a text field, but the outer rim must not.
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (x < 3 || y < 3 || x >= width - 3 || y >= height - 3)
                        for (int channel = 0; channel < 4; channel++)
                            if (oldPixels[(y * width + x) * 4 + channel] != newPixels[(y * width + x) * 4 + channel])
                                throw new Exception($"Focus changed the border of {control.Name} in {theme} at {x},{y}");
            if (control == window.StartInTrayBox || control == window.YxRatioBox || control == window.SaveButton || control == window.SensitivitySlider)
                Capture(window, Path.Combine(output, $"focus-{control.Name}-{theme}.png"));
        }
    }

    private static RenderTargetBitmap RenderControl(FrameworkElement control)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(control.ActualWidth), (int)Math.Ceiling(control.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        return bitmap;
    }

    private static void AssertDpiTextVisible(TextBox box)
    {
        string original = box.Text;
        try
        {
            foreach (string sample in new[] { "50", "800", "1600", "32000", "100000" })
            {
                box.Text = sample;
                box.CaretIndex = sample.Length;
                box.UpdateLayout();
                var viewport = FindVisual<ScrollContentPresenter>(box) ?? throw new Exception("DPI text viewport missing");
                var bounds = viewport.TransformToAncestor(box).TransformBounds(new Rect(viewport.RenderSize));
                for (int i = 0; i < sample.Length; i++)
                {
                    var leading = box.GetRectFromCharacterIndex(i, false);
                    var trailing = box.GetRectFromCharacterIndex(i, true);
                    if (leading.IsEmpty || trailing.IsEmpty || leading.Top < bounds.Top - .5 || leading.Bottom > bounds.Bottom + .5 ||
                        leading.Left < bounds.Left - .5 || trailing.Right > bounds.Right + .5)
                        throw new Exception($"DPI text clipped: {sample}, char {i}, glyph {leading}, viewport {bounds}");
                }
            }
        }
        finally { box.Text = original; box.UpdateLayout(); }
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is T descendant) return descendant;
        }
        return null;
    }
}
