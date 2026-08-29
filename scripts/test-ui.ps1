$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $projectRoot 'artifacts\app\app\0Accel.Panel.exe'
$output = Join-Path $projectRoot 'artifacts\ui'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run scripts/build.ps1 first.' }
$process = Start-Process -FilePath $executable -ArgumentList @('--ui-test', ('"' + $output + '"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'UI test timed out.' }
if ($process.ExitCode -ne 0) { throw "UI tests failed. See $output\result.txt" }
Get-Content -LiteralPath (Join-Path $output 'result.txt')
