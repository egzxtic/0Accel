using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ZeroAccel;

public partial class MainWindow
{
    private RawAccelClient? rawAccelClient;
    private RawAccelStatus? driverStatus;
    private bool driverBusy, driverClosed, driverTimedOut;
    private string SelectedInstance => devices.Count>deviceIndex ? devices[deviceIndex].InstanceId : "";
    private string SelectedRawId => RawAccelProtocol.DeviceId(SelectedInstance);
    private bool UniqueRawTarget => SelectedRawId.Length>0 && devices.Count(d =>
        RawAccelProtocol.DeviceId(d.InstanceId).Equals(SelectedRawId,StringComparison.OrdinalIgnoreCase))==1;
    private RawAccelSelection? SelectedRawProfile => driverStatus is not null && SelectedRawId.Length>0
        ? RawAccelProtocol.Inspect(driverStatus,SelectedRawId) : null;
    private void InitializeDriver()
    {
        Activated += async (_,_) => await RefreshDriverAsync();
        Closed += (_,_) => driverClosed=true;
    }
    private RawAccelClient Client => rawAccelClient ??= new RawAccelClient(
        () => new RawAccelTransport(),RequirePresent,BackupRawConfiguration);
    private static void RequirePresent(string id)
    {
        if (MouseProbe.Enumerate().Count(d => RawAccelProtocol.DeviceId(d.InstanceId).Equals(id,StringComparison.OrdinalIgnoreCase))!=1)
            throw new InvalidOperationException("The selected Raw Accel device ID is missing or ambiguous. Refresh devices.");
    }
    private static void BackupRawConfiguration(byte[] bytes)
    {
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"0Accel","rawaccel-backups");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new IOException("Backup directory must not be a link.");
        string path=Path.Combine(root,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".rawaccel-1.7.0.bin");
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
    private async Task RefreshDriverAsync()
    {
        if (app.TestMode || driverBusy || driverClosed || driverTimedOut) return;
        driverBusy=true;
        try {
            var status=await Client.ReadAsync();
            if (!driverClosed) SetDriverStatus(status);
        }
        catch (Exception e) when (IsDriverError(e)) { if (!driverClosed) DriverFailed(e,false); }
        finally { driverBusy=false; if (!driverClosed) UpdateDriverButtons(); }
    }
    private static bool IsDriverError(Exception e) => e is Win32Exception or IOException or InvalidDataException or TimeoutException
        or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException or UnauthorizedAccessException or System.Security.SecurityException;
    private void DriverFailed(Exception error,bool action)
    {
        if (error is TimeoutException) driverTimedOut=true;
        driverStatus=null;
        bool absent=error is Win32Exception w && w.NativeErrorCode is 2 or 3;
        StatusLabel.Text=app.T(absent ? "S_Preview" : "S_DriverUnknown");
        DriverActions.Visibility=Visibility.Collapsed;
        if (action || !absent) StatusDetail.Text=error switch {
            TimeoutException => app.T("M_DriverTimeout"),
            Win32Exception win when win.NativeErrorCode==5 => app.T("M_DriverAdmin"),
            _ => app.F("M_DriverError",error.Message)
        };
        else StatusDetail.Text=app.T("M_RawUnavailable");
        UpdateMotionTracking();
    }
    internal void SetDriverStatus(RawAccelStatus status)
    {
        driverStatus=status;
        DriverActions.Visibility=Visibility.Visible;
        DriverVersionLabel.Text="Raw Accel "+RawAccelProtocol.DriverVersion;
        UpdateDriverButtons(); UpdateMotionTracking();
    }
    private void UpdateDriverButtons()
    {
        DriverApplyButton.IsEnabled=!driverBusy && driverStatus is not null && UniqueRawTarget;
        DriverReadButton.IsEnabled=!driverBusy && SelectedRawProfile?.Settings is not null && UniqueRawTarget;
        DriverApplyButton.ToolTip=app.T(UniqueRawTarget ? "S_ApplyTooltip" : "M_RawTargetAmbiguous");
        SettingsScroll.IsEnabled=CurveModeCombo.IsEnabled=ResetButton.IsEnabled=ImportButton.IsEnabled=!driverBusy;
        DeviceButton.IsEnabled=RefreshDevicesButton.IsEnabled=!driverBusy;
        ShowLastMouseMoveCheck.IsEnabled=devices.Count>0;
        ShowLastMouseMoveCheck.ToolTip=app.T("S_LastMoveTooltip");
        UpdateDriverLabel();
    }
    private void UpdateDriverLabel()
    {
        if (driverStatus is null) return;
        var active=SelectedRawProfile;
        StatusLabel.Text=app.T(active is null ? "S_RawConnected" : !active.Enabled ? "S_DriverDisabled"
            : active.Settings is not null && RawAccelProtocol.Equivalent(ReadSettings(),active.Settings)
            ? "S_DriverActive" : "S_DriverDraft");
    }
    private async void DriverApplyClicked(object sender,RoutedEventArgs e)
    {
        if (app.TestMode || driverBusy || driverClosed || driverStatus is null || !UniqueRawTarget
            || !TryCollectSettings(out Settings settings)) return;
        if (SelectedRawProfile is { Enabled:true,Settings:null }
            && MessageBox.Show(this,app.T("M_RawReplaceAdvanced"),"0Accel",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK) return;
        await ApplyRawAsync(settings);
    }
    private async Task ApplyRawAsync(Settings settings)
    {
        var expected=driverStatus!;
        string id=SelectedRawId;
        driverBusy=true; UpdateDriverButtons();
        StatusDetail.Text=app.T("M_RawApplying");
        try {
            var status=await Client.ApplyAsync(expected,settings,id);
            if (driverClosed) return;
            SetDriverStatus(status);
            StatusDetail.Text=app.T("M_RawApplied");
        }
        catch (Exception error) when (IsDriverError(error)) { if (!driverClosed) DriverFailed(error,true); }
        finally { driverBusy=false; if (!driverClosed) UpdateDriverButtons(); }
    }
    private async void DriverReadClicked(object sender,RoutedEventArgs e)
    {
        if (app.TestMode || driverBusy || driverClosed || !UniqueRawTarget) return;
        await RefreshDriverAsync();
        if (driverClosed || SelectedRawProfile?.Settings is not Settings s) return;
        // Explicit read into the draft; preserve local DPI/theme/startup preferences.
        ApplySettings(app.Settings with {
            Sensitivity=s.Sensitivity,YxRatio=s.YxRatio,Rotation=s.Rotation,CurveMode=s.CurveMode,
            GainEnabled=s.GainEnabled,Acceleration=s.Acceleration,CapType=s.CapType,CapInput=s.CapInput,
            CapOutput=s.CapOutput,InputOffset=s.InputOffset,Power=s.Power,DecayRate=s.DecayRate,Limit=s.Limit
        },syncStartup:false);
        dirty=true;
        StatusDetail.Text=app.T("M_RawRead");
    }
}
