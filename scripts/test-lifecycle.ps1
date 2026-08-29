$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $projectRoot 'artifacts\app\0Accel.exe'
if (Get-Process -Name 0Accel,0Accel.Panel -ErrorAction SilentlyContinue) { throw 'Close 0Accel before the lifecycle test. Existing processes will not be touched.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ZeroAccelTestWindow {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
}
'@
$primary = $null
$panel = $null
try {
    $primary = Start-Process -FilePath $executable -ArgumentList '--tray' -WorkingDirectory ([IO.Path]::GetTempPath()) -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    $primary.Refresh()
    if ($primary.HasExited -or $primary.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Tray startup created a visible window or exited.' }
    Write-Output 'PASS: tray startup without a panel'
    $before = $primary.TotalProcessorTime.TotalMilliseconds
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 10
    $primary.Refresh()
    $elapsed = $timer.Elapsed.TotalMilliseconds
    $cpu = $primary.TotalProcessorTime.TotalMilliseconds - $before
    Write-Output ('Tray-only sample: {0:N2} ms CPU / {1:N0} ms wall ({2:N4}% of one logical core); working set {3:N1} MiB, private {4:N1} MiB' -f $cpu, $elapsed, (100*$cpu/$elapsed), ($primary.WorkingSet64/1MB), ($primary.PrivateMemorySize64/1MB))
    $secondary = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    if (-not $secondary.WaitForExit(10000)) { throw 'Second instance did not exit.' }
    Start-Sleep -Seconds 2
    $panel = Get-Process -Name 0Accel.Panel -ErrorAction Stop
    if ($panel.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Second launch did not reopen the panel.' }
    if (@(Get-Process -Name 0Accel).Count -ne 1) { throw 'More than one instance remains.' }
    Write-Output 'PASS: second launch opens the first instance; no duplicate process'
    [ZeroAccelTestWindow]::PostMessage($panel.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2
    $primary.Refresh()
    if ($primary.HasExited -or $primary.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Closing the panel did not return to tray.' }
    if (-not $panel.WaitForExit(5000)) { throw 'Panel runtime is still running after closing the window.' }
    Write-Output 'PASS: closing the panel terminates CLR/WPF and keeps the native tray alive'
    $before = $primary.TotalProcessorTime.TotalMilliseconds
    $timer.Restart()
    Start-Sleep -Seconds 10
    $primary.Refresh()
    $elapsed = $timer.Elapsed.TotalMilliseconds
    $cpu = $primary.TotalProcessorTime.TotalMilliseconds - $before
    Write-Output ('After closing panel: {0:N2} ms CPU / {1:N0} ms wall ({2:N4}% of one logical core); working set {3:N1} MiB, private {4:N1} MiB' -f $cpu, $elapsed, (100*$cpu/$elapsed), ($primary.WorkingSet64/1MB), ($primary.PrivateMemorySize64/1MB))
    $reopen = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    if (-not $reopen.WaitForExit(10000)) { throw 'Reopen launcher did not exit.' }
    Start-Sleep -Seconds 2
    $panel = Get-Process -Name 0Accel.Panel -ErrorAction Stop
    if ($panel.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Panel could not be reopened after termination.' }
    Write-Output 'PASS: panel can be restarted after its CLR process terminates'
    $direct = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/app/app/0Accel.Panel.exe') -WorkingDirectory ([IO.Path]::GetTempPath()) -WindowStyle Hidden -PassThru
    if (-not $direct.WaitForExit(10000)) { throw 'Direct panel launch did not hand off to the root launcher.' }
    if ($direct.ExitCode -ne 0 -or @(Get-Process -Name 0Accel.Panel).Count -ne 1) { throw 'Direct panel launch failed or duplicated the panel.' }
    Write-Output 'PASS: relocated panel hands off to the launcher; paths do not depend on the working directory'
    $hostWindow = [ZeroAccelTestWindow]::FindWindow('0Accel.Tray.v1', '0Accel host')
    [ZeroAccelTestWindow]::PostMessage($hostWindow, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (-not $primary.WaitForExit(5000)) { throw 'Host did not exit cleanly.' }
    if (-not $panel.WaitForExit(5000)) { throw 'Host shutdown left a panel process behind.' }
    Write-Output 'PASS: graceful host shutdown also closes an open panel'
} finally {
    # Terminate only the test process started here, never another user's instance.
    if ($primary -and -not $primary.HasExited) { $primary.Kill(); $primary.WaitForExit(5000) | Out-Null }
    if ($panel -and -not $panel.HasExited) { $panel.Kill(); $panel.WaitForExit(5000) | Out-Null }
}
