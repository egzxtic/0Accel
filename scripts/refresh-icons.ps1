$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appDirectory = Join-Path $projectRoot 'artifacts/app'
if (-not ('ZeroAccelShellRefresh' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ZeroAccelShellRefresh {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern void SHChangeNotify(uint change, uint flags, string item1, IntPtr item2);
}
'@
}
# Notify the shell without deleting the global icon cache or restarting Explorer.
foreach ($relative in @('0Accel.exe', 'app/0Accel.Panel.exe')) {
    [ZeroAccelShellRefresh]::SHChangeNotify(0x2000, 0x1005, (Join-Path $appDirectory $relative), [IntPtr]::Zero)
}
[ZeroAccelShellRefresh]::SHChangeNotify(0x1000, 0x1005, $appDirectory, [IntPtr]::Zero)
