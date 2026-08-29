using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using System.Windows.Threading;

namespace ZeroAccel;

public partial class MainWindow : Window
{
    private readonly App app;
    private bool ready, dirty;
    private bool showVelocity, showGain;
    private MouseProbe? probe;
    private List<MouseDevice> devices = new();
    private int deviceIndex;
    private readonly DispatcherTimer measurementTimer;
    internal MainWindow(App owner)
    {
        app = owner;
        InitializeComponent();
        InitializeDriver();
        measurementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        measurementTimer.Tick += MeasurementFinished;
        SensitivitySlider.Value = app.Settings.Sensitivity;
        AccelerationSlider.Value = app.Settings.Acceleration;
        LimitSlider.Value = app.Settings.Limit;
        DpiBox.Text = app.Settings.Dpi?.ToString(CultureInfo.InvariantCulture) ?? "";
        PowerBox.Text = app.Settings.Power.ToString("0.###", CultureInfo.InvariantCulture);
        DecayBox.Text = app.Settings.DecayRate.ToString("0.###", CultureInfo.InvariantCulture);
        YxRatioBox.Text = app.Settings.YxRatio.ToString("0.00", CultureInfo.InvariantCulture);
        InputOffsetBox.Text = app.Settings.InputOffset.ToString("0.###", CultureInfo.InvariantCulture);
        CapInputBox.Text = app.Settings.CapInput.ToString("0.###", CultureInfo.InvariantCulture);
        CapOutputBox.Text = app.Settings.CapOutput.ToString("0.00", CultureInfo.InvariantCulture);
        StartInTrayBox.IsChecked = app.Settings.StartInTray;
        GainCheck.IsChecked = app.Settings.GainEnabled;
        ShowLastMouseMoveCheck.IsChecked = app.Settings.ShowLastMouseMove;
        showVelocity = app.Settings.ShowVelocity;
        showGain = app.Settings.ShowGain;
        SetComboByTag(RotationCombo, app.Settings.Rotation.ToString());
        SetComboByTag(CurveModeCombo, app.Settings.CurveMode);
        SetComboByTag(CapTypeCombo, app.Settings.CapType);
        UpdateCapTypeVisibility();
        if (!app.TestMode)
        {
            try { AutoStartBox.IsChecked = StartupService.Enabled; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { StatusDetail.Text = app.T("M_LoadAutostartFailed"); }
        }
        ready = true;
        RefreshTheme(); RefreshCurve();
        SizeChanged += (_, _) => UpdateSize();
        DpiChanged += (_, _) => UpdateSize();
        SourceInitialized += (_, _) =>
        {
            probe = new MouseProbe(new WindowInteropHelper(this).Handle);
            probe.MotionSampled += Curve.SetLastMove;
            UpdateSize();
        };
        Loaded += (_, _) =>
        {
            RefreshDevices();
            if (app.Store.LastError is string err) StatusDetail.Text = app.T(err);
            else StatusDetail.Text = app.F("M_SettingsPath", app.Store.SettingsPath);
        };
        Activated += (_, _) => UpdateMotionTracking();
        Deactivated += (_, _) => { CancelMeasurement(); probe?.StopTracking(); };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { CancelMeasurement(); probe?.StopTracking(); Close(); }
        };
        Closing += OnClosing;
        Closed += (_, _) => { measurementTimer.Stop(); probe?.Dispose(); probe = null; };
    }

    private void UpdateSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(ActualWidth*dpi.DpiScaleX), height = (int)Math.Round(ActualHeight*dpi.DpiScaleY);
        WindowSizeLabel.Text = $"({width}×{height})";
        Title = $"0Accel ({width}×{height})";
    }

    internal void RefreshTheme()
    {
        ThemeGlyph.Text = app.Settings.Theme == "Dark" ? "☀" : "☾";
        ThemeButton.ToolTip = app.Settings.Theme == "Dark" ? app.T("S_ThemeToLight") : app.T("S_ThemeToDark");
        ThemeButton.SetValue(AutomationProperties.NameProperty, ThemeButton.ToolTip);
        Icon = app.WindowIconImage ?? Icon;
        LogoImage.Source = app.LogoImage;
        if (LogoImage.Source is null)
        {
            LogoImage.Visibility = Visibility.Collapsed;
            LogoText.Visibility = Visibility.Visible;
        }
        else
        {
            LogoImage.Visibility = Visibility.Visible;
            LogoText.Visibility = Visibility.Collapsed;
        }
        Curve.InvalidateVisual();
    }

    private void RefreshCurve()
    {
        if (!ready) return;
        SensitivityBox.Text = SensitivitySlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        AccelerationBox.Text = AccelerationSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
        LimitBox.Text = LimitSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        UpdateCurveModeVisibility();
        Curve.Config = CurveConfig.From(ReadSettings());
        if (Curve.Config.Mode is 1 or 2 && Curve.Config.CapType is 1 or 2 && Curve.Config.CapInput <= Curve.Config.Offset)
            CurveHint.Text = app.T("M_CapBelowOffset");
        else if (StatusDetail.Text == app.T("M_CapBelowOffset"))
            StatusDetail.Text = app.F("M_SettingsPath", app.Store.SettingsPath);
        Curve.ShowLastMove = ShowLastMouseMoveCheck.IsChecked == true;
        if (!Curve.ShowLastMove) Curve.ClearLastMove();
        Curve.InvalidateVisual();
        UpdateMotionTracking();
        UpdateDriverLabel();
    }

    internal bool IsMotionTracking => probe?.IsTracking == true;
    internal bool IsRawInputRegistered => probe?.IsRegistered == true;

    private void UpdateMotionTracking()
    {
        if (probe is null) return;
        if (!Curve.ShowLastMove || !IsActive || !IsVisible || WindowState == WindowState.Minimized || devices.Count == 0)
        { probe.StopTracking(); return; }
        if (!probe.StartTracking(devices[deviceIndex].Handle)) StatusDetail.Text = app.T("M_MotionStartError");
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!ready) return; dirty = true; RefreshCurve(); }
    private void SettingChanged(object sender, TextChangedEventArgs e) { if (ready) { dirty = true; } }
    private void NumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == PowerBox || sender == DecayBox || sender == YxRatioBox
            || sender == CapInputBox || sender == CapOutputBox || sender == InputOffsetBox)
        {
            if (CommitAdvancedNumbers()) RefreshCurve();
            e.Handled = true;
            return;
        }
        CommitNumber((TextBox)sender);
        e.Handled = true;
    }
    private void AdvancedNumberKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { if (CommitAdvancedNumbers()) RefreshCurve(); e.Handled = true; } }
    private void NumberCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitNumber((TextBox)sender);
    private void AdvancedNumberCommitted(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!CommitAdvancedNumbers()) return;
        RefreshCurve();
    }

    private bool CommitNumber(TextBox box)
    {
        if (!ready) return true;
        Slider slider = box == SensitivityBox ? SensitivitySlider : box == AccelerationBox ? AccelerationSlider : LimitSlider;
        if (!double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < slider.Minimum || value > slider.Maximum)
        {
            StatusDetail.Text = app.F("M_NoDataInput", slider.Minimum, slider.Maximum);
            box.Text = slider.Value.ToString(box == AccelerationBox ? "0.000" : "0.00", CultureInfo.InvariantCulture);
            return false;
        }
        slider.Value = Math.Round(value/slider.TickFrequency)*slider.TickFrequency;
        dirty = true;
        return true;
    }

    private bool CommitAdvancedNumber(TextBox box, double min, double max, string fallback, bool showDetail = true)
    {
        if (!ready) return true;
        if (!double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed) || parsed < min || parsed > max)
        {
            if (showDetail) StatusDetail.Text = app.F("M_NoDataInput", min, max);
            box.Text = fallback;
            return false;
        }
        box.Text = parsed.ToString("0.###", CultureInfo.InvariantCulture);
        dirty = true;
        return true;
    }

    private bool CommitAdvancedNumbers()
    {
        foreach (var (box, min, max, fallback) in new[] {
            (YxRatioBox, .25, 8.0, app.Settings.YxRatio),
            (CapInputBox, .1, 1600.0, app.Settings.CapInput),
            (CapOutputBox, 1.0, 16.0, app.Settings.CapOutput),
            (InputOffsetBox, 0.0, 200.0, app.Settings.InputOffset),
            (PowerBox, 1.01, 5.0, app.Settings.Power),
            (DecayBox, .001, 10.0, app.Settings.DecayRate) })
        {
            if (box.IsVisible && !CommitAdvancedNumber(box, min, max, fallback.ToString("0.###", CultureInfo.InvariantCulture))) return false;
        }
        return true;
    }

    private static double GetNumeric(TextBox box, double min, double max, double fallback) =>
        double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? Math.Clamp(value, min, max) : fallback;

    private string GetComboTag(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem selected && selected.Tag is string value) return value;
        return fallback;
    }

    private void SetComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals((item.Tag as string)?.ToLowerInvariant(), tag.ToLowerInvariant()))
            {
                combo.SelectedItem = item;
                if (combo == CapTypeCombo) UpdateCapTypeVisibility();
                return;
            }
        }
        combo.SelectedIndex = 0;
        if (combo == CapTypeCombo) UpdateCapTypeVisibility();
    }
    private void UpdateCapTypeVisibility()
    {
        bool capped = GetComboTag(CurveModeCombo, "off") is "linear" or "classic";
        string type = GetComboTag(CapTypeCombo, "Output");
        CapInputRow.Visibility = capped && type is "Input" or "Both" ? Visibility.Visible : Visibility.Collapsed;
        CapOutputRow.Visibility = capped && type is "Output" or "Both" ? Visibility.Visible : Visibility.Collapsed;
        bool derived = capped && type == "Both";
        AccelerationBox.IsEnabled = AccelerationSlider.IsEnabled = !derived;
        AccelerationRow.ToolTip = derived ? app.T("S_DerivedAcceleration") : null;
        AccelerationRow.Opacity = derived ? .45 : 1;
    }
    private void UpdateCurveModeVisibility()
    {
        string mode = GetComboTag(CurveModeCombo, "off");
        bool active = mode != "off", natural = mode == "natural";
        GainRow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        AccelerationRow.Visibility = active && !natural ? Visibility.Visible : Visibility.Collapsed;
        CapTypeRow.Visibility = active && !natural ? Visibility.Visible : Visibility.Collapsed;
        InputOffsetRow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PowerRow.Visibility = mode == "classic" ? Visibility.Visible : Visibility.Collapsed;
        DecayRow.Visibility = natural ? Visibility.Visible : Visibility.Collapsed;
        LimitRow.Visibility = natural ? Visibility.Visible : Visibility.Collapsed;
        UpdateCapTypeVisibility();
        CurveHint.SetResourceReference(TextBlock.TextProperty, active ? "S_CurveHint" : "S_CurveOffHint");
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (sender == CapTypeCombo) UpdateCapTypeVisibility();
        dirty = true;
        RefreshCurve();
        if (sender == CurveModeCombo) SettingsScroll.ScrollToTop();
    }
    private void CheckChanged(object sender, RoutedEventArgs e)
    { if (ready) { dirty = true; RefreshCurve(); } }

    internal bool TrySave()
    {
        if (!TryCollectSettings(out var settings)) return false;
        if (!app.Store.Save(settings))
        {
            StatusDetail.Text = app.T(app.Store.LastError ?? "M_SaveSettingsFailed");
            return false;
        }
        app.Settings = settings;
        dirty = false;
        StatusDetail.Text = app.T("M_SaveOk");
        return true;
    }

    private bool TryCollectSettings(out Settings settings)
    {
        int? dpi = null;
        settings = default!;
        if (!CommitNumber(SensitivityBox)) return false;
        if (AccelerationRow.Visibility == Visibility.Visible && !CommitNumber(AccelerationBox)) return false;
        if (LimitRow.Visibility == Visibility.Visible && !CommitNumber(LimitBox)) return false;
        if (!CommitAdvancedNumbers()) return false;
        if (!int.TryParse(GetComboTag(RotationCombo, "0"), out var rotation)
            || rotation is not 0 and not 90 and not 180 and not 270)
        {
            rotation = 0;
        }
        if (!string.IsNullOrWhiteSpace(DpiBox.Text))
        {
            if (!int.TryParse(DpiBox.Text, out int value) || value < 50 || value > 100000)
            { StatusDetail.Text = app.F("M_DpiInvalid", 50, 100000); return false; }
            dpi = value;
        }
        settings = ReadSettings() with { Dpi = dpi, Rotation = rotation };
        if (settings.CurveMode is "linear" or "classic" && settings.CapType is "Input" or "Both"
            && settings.CapInput <= settings.InputOffset)
        { StatusDetail.Text = app.T("M_CapBelowOffset"); return false; }
        return true;
    }

    private Settings ReadSettings() => app.Settings with
    {
        Version = 2, Sensitivity = SensitivitySlider.Value, SensitivityMultiplier = 1,
        Acceleration = AccelerationSlider.Value, Limit = LimitSlider.Value,
        YxRatio = GetNumeric(YxRatioBox, .25, 8, app.Settings.YxRatio),
        Rotation = int.TryParse(GetComboTag(RotationCombo, "0"), out var rotation) ? rotation : 0,
        CurveMode = GetComboTag(CurveModeCombo, "off"), GainEnabled = GainCheck.IsChecked == true,
        CapType = GetComboTag(CapTypeCombo, "Output"),
        CapInput = GetNumeric(CapInputBox, .1, 1600, app.Settings.CapInput),
        CapOutput = GetNumeric(CapOutputBox, 1, 16, app.Settings.CapOutput),
        InputOffset = GetNumeric(InputOffsetBox, 0, 200, app.Settings.InputOffset),
        Power = GetNumeric(PowerBox, 1.01, 5, app.Settings.Power),
        DecayRate = GetNumeric(DecayBox, .001, 10, app.Settings.DecayRate),
        ShowLastMouseMove = ShowLastMouseMoveCheck.IsChecked == true,
        ShowVelocity = showVelocity, ShowGain = showGain,
        StartInTray = StartInTrayBox.IsChecked == true
    };

    private void SaveClicked(object sender, RoutedEventArgs e) => TrySave();
    private string DialogDirectory
    {
        get
        {
            var directory = Path.GetDirectoryName(app.Store.SettingsPath);
            return directory is not null && Directory.Exists(directory)
                ? directory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
    private void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = app.T("S_Import"),
            FileName = "0Accel-settings.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = DialogDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!app.Store.TryLoadFrom(dialog.FileName, out var settings))
        {
            StatusDetail.Text = app.T(app.Store.LastError ?? "M_ImportFailed");
            return;
        }
        ApplySettings(settings);
        dirty = false;
        StatusDetail.Text = app.T("M_ImportOk");
    }
    private void ApplySettings(Settings settings, bool syncStartup = true)
    {
        ready = false;
        var previousTheme = app.Settings.Theme;
        SensitivitySlider.Value = settings.Sensitivity;
        SensitivityBox.Text = settings.Sensitivity.ToString("0.00", CultureInfo.InvariantCulture);
        AccelerationSlider.Value = settings.Acceleration;
        AccelerationBox.Text = settings.Acceleration.ToString("0.000", CultureInfo.InvariantCulture);
        LimitSlider.Value = settings.Limit;
        LimitBox.Text = settings.Limit.ToString("0.00", CultureInfo.InvariantCulture);
        PowerBox.Text = settings.Power.ToString("0.###", CultureInfo.InvariantCulture);
        DecayBox.Text = settings.DecayRate.ToString("0.###", CultureInfo.InvariantCulture);
        YxRatioBox.Text = settings.YxRatio.ToString("0.00", CultureInfo.InvariantCulture);
        CapInputBox.Text = settings.CapInput.ToString("0.00", CultureInfo.InvariantCulture);
        CapOutputBox.Text = settings.CapOutput.ToString("0.00", CultureInfo.InvariantCulture);
        InputOffsetBox.Text = settings.InputOffset.ToString("0.###", CultureInfo.InvariantCulture);
        GainCheck.IsChecked = settings.GainEnabled;
        ShowLastMouseMoveCheck.IsChecked = settings.ShowLastMouseMove;
        showVelocity = settings.ShowVelocity;
        showGain = settings.ShowGain;
        DpiBox.Text = settings.Dpi?.ToString(CultureInfo.InvariantCulture) ?? "";
        StartInTrayBox.IsChecked = settings.StartInTray;
        SetComboByTag(RotationCombo, settings.Rotation.ToString());
        SetComboByTag(CurveModeCombo, settings.CurveMode);
        SetComboByTag(CapTypeCombo, settings.CapType);
        app.Settings = settings;
        ready = true;
        RefreshCurve();
        if (!Equals(previousTheme, settings.Theme))
            app.ApplyTheme(settings.Theme);
        if (syncStartup && !app.TestMode && AutoStartBox.IsChecked == true)
        {
            if (!StartupService.SetEnabled(true, settings.StartInTray, out var error))
            {
                StatusDetail.Text = error;
                return;
            }
        }
        RefreshCurve();
    }
    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (!TryCollectSettings(out var settings)) return;
        var dialog = new SaveFileDialog
        {
            Title = app.T("S_Export"),
            FileName = "0Accel-settings.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = DialogDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!app.Store.SaveToFile(settings, dialog.FileName))
        {
            StatusDetail.Text = app.T(app.Store.LastError ?? "M_ExportFailed");
            return;
        }
        StatusDetail.Text = app.T("M_ExportOk");
    }
    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        SensitivitySlider.Value = 1;
        AccelerationSlider.Value = .02;
        LimitSlider.Value = 1.5;
        PowerBox.Text = "2";
        DecayBox.Text = "0.1";
        YxRatioBox.Text = "1";
        CapInputBox.Text = "120";
        CapOutputBox.Text = "1.2";
        InputOffsetBox.Text = "15";
        GainCheck.IsChecked = true;
        showVelocity = false;
        showGain = true;
        ShowLastMouseMoveCheck.IsChecked = false;
        SetComboByTag(RotationCombo, "0");
        SetComboByTag(CurveModeCombo, "off");
        SetComboByTag(CapTypeCombo, "Output");
        dirty = true;
        RefreshCurve();
    }
    private void AboutClicked(object sender, RoutedEventArgs e) => new AboutWindow(app, driverStatus) { Owner = this }.ShowDialog();

    private void ThemeClicked(object sender, RoutedEventArgs e)
    {
        app.Settings = app.Settings with { Theme = app.Settings.Theme == "Dark" ? "Light" : "Dark" };
        app.ApplyTheme(app.Settings.Theme); dirty = true;
    }
    private void AutoStartClicked(object sender, RoutedEventArgs e)
    {
        if (app.TestMode) { AutoStartBox.IsChecked = false; return; }
        bool requested = AutoStartBox.IsChecked == true;
        if (!StartupService.SetEnabled(requested, StartInTrayBox.IsChecked == true, out var error))
        { AutoStartBox.IsChecked = !requested; StatusDetail.Text = error; }
        else StatusDetail.Text = requested ? app.T("M_AutoStartOn") : app.T("M_AutoStartOff");
    }
    private void TrayOptionClicked(object sender, RoutedEventArgs e)
    {
        dirty = true;
        if (!app.TestMode && AutoStartBox.IsChecked == true
            && !StartupService.SetEnabled(true, StartInTrayBox.IsChecked == true, out var error))
        { StartInTrayBox.IsChecked = !StartInTrayBox.IsChecked; StatusDetail.Text = error; return; }
        TrySave();
    }
    private void MinimizeClicked(object sender, RoutedEventArgs e) => Close();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (!app.Exiting && dirty && !TrySave()) e.Cancel = true; }

    private void RefreshDevices()
    {
        CancelMeasurement();
        probe?.StopTracking(); Curve.ClearLastMove();
        devices = MouseProbe.Enumerate(); deviceIndex = 0;
        DeviceButton.Content = devices.Count == 0 ? app.T("S_DeviceNotFound") : devices[0].Name;
        MeasureButton.IsEnabled = devices.Count > 0;
        PollingLabel.Text = app.T("S_Unknown");
        ShowLastMouseMoveCheck.IsEnabled = devices.Count > 0;
        UpdateDriverButtons();
        UpdateMotionTracking();
    }
    private async void RefreshDevicesClicked(object sender, RoutedEventArgs e) { RefreshDevices(); await RefreshDriverAsync(); }
    private void ChooseDeviceClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = (Brush)FindResource("SurfaceBrush"), Foreground = (Brush)FindResource("TextBrush") };
        for (int i = 0; i < devices.Count; i++)
        {
            int index = i;
            var item = new MenuItem { Header = devices[i].Name, IsCheckable = true, IsChecked = i == deviceIndex };
            item.Click += (_, _) =>
            {
                CancelMeasurement(); probe?.StopTracking(); Curve.ClearLastMove();
                deviceIndex = index; DeviceButton.Content = devices[index].Name; PollingLabel.Text = app.T("S_Unknown");
                UpdateDriverButtons();
                UpdateMotionTracking();
            };
            menu.Items.Add(item);
        }
        DeviceButton.ContextMenu = menu; menu.PlacementTarget = DeviceButton; menu.IsOpen = true;
    }
    private void MeasureClicked(object sender, RoutedEventArgs e)
    {
        if (probe is null || devices.Count == 0) return;
        if (probe.IsMeasuring) { CancelMeasurement(); return; }
        if (!probe.Start(devices[deviceIndex].Handle)) { StatusDetail.Text = app.T("M_MeasureStartError"); return; }
        PollingLabel.Text = app.T("S_Measuring");
        MeasureButton.Content = app.T("S_Cancel");
        StatusDetail.Text = app.T("M_MeasureHint");
        measurementTimer.Start();
    }
    private void MeasurementFinished(object? sender, EventArgs e)
    {
        measurementTimer.Stop();
        var hz = probe?.Stop();
        PollingLabel.Text = hz.HasValue ? $"~{Math.Round(hz.Value):0} Hz" : app.T("S_Unknown");
        MeasureButton.Content = app.T("S_Measure");
        StatusDetail.Text = hz.HasValue ? app.T("M_MeasureStatus") : app.T("M_MeasureStatusTooShort");
    }
    private void CancelMeasurement()
    {
        if (probe?.IsMeasuring != true) return;
        measurementTimer.Stop(); probe.Stop(); PollingLabel.Text = app.T("S_Unknown"); MeasureButton.Content = app.T("S_Measure");
        StatusDetail.Text = app.T("M_MeasureCancel");
    }
}
