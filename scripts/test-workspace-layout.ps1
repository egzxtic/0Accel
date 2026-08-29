# Read-only structure checks; does not compile, launch the app or install a driver.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$count = 0
foreach ($relative in @('README.md', 'host/host.c',
    'tools/RawAccelBridge/rawaccel_bridge.cpp', 'tools/RawAccelBridge/upstream/LICENSE',
    'src/ZeroAccel/ZeroAccel.csproj', 'setup/0Accel.iss', 'setup/driver_helper.cpp',
    'setup/driver_helper.manifest', 'setup/driver_helper.rc', 'setup/install-info.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) {
        throw "Missing project file: $relative"
    }
    $count++
}
$projectFile = Join-Path $projectRoot 'src/ZeroAccel/ZeroAccel.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$resources = @($project.SelectNodes('/Project/ItemGroup/Resource'))
$expected = @('Brand/wordmark-black.png', 'Brand/wordmark-white.png', 'Brand/icon-black.png', 'Brand/icon-white.png')
if ($resources.Count -ne $expected.Count) { throw 'Unexpected branding resource count.' }
foreach ($resource in $resources) {
    $path = [IO.Path]::GetFullPath((Join-Path (Split-Path $projectFile) $resource.Include))
    if ($resource.Link -cnotin $expected -or !(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing or renamed branding resource: $($resource.Include)"
    }
    $count++
}
foreach ($scriptFile in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)) {
    $tokens = $null
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Syntax error in $($scriptFile.Name): $($parseErrors[0].Message)" }
    $count++
}
$appDirectory = Join-Path $projectRoot 'artifacts/app'
if (Test-Path -LiteralPath $appDirectory) {
    foreach ($relative in @('0Accel.exe', 'app/0Accel.Panel.exe', 'app/0Accel.Panel.dll', 'app/0Accel.RawAccel.dll', 'app/licenses/RawAccel-MIT.txt', 'LICENSE')) {
        if (!(Test-Path -LiteralPath (Join-Path $appDirectory $relative) -PathType Leaf)) {
            throw "Incomplete existing app build: $relative"
        }
        $count++
    }
    & (Join-Path $PSScriptRoot 'assert-preview-files.ps1') -Directory $appDirectory
}
Write-Output "PASS: $count layout/syntax checks. No build, installation or hardware stability claim."
