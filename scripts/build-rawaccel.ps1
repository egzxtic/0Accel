param([switch]$SkipTests, [switch]$Sanitize)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $zigExe = Join-Path $projectRoot '.tools/zig-x86_64-windows-0.15.2/zig.exe'
    if (!(Test-Path -LiteralPath $zigExe)) { $zigExe = (Get-Command zig -ErrorAction Stop).Source }
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $projectRoot '.tools/zig-global-cache'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $projectRoot 'artifacts/zig-cache'
    New-Item -ItemType Directory -Force -Path $env:ZIG_GLOBAL_CACHE_DIR,$env:ZIG_LOCAL_CACHE_DIR | Out-Null
    $dotnetExe = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
    $nugetConfig = Join-Path $PSScriptRoot 'NuGet.Config'
    $nugetPackages = Join-Path $projectRoot '.tools/nuget-packages'
    $restoreProperties = @("-p:RestoreConfigFile=$nugetConfig", "-p:RestorePackagesPath=$nugetPackages")
    $pin = Get-Content -LiteralPath tools/RawAccelBridge/upstream-hashes.json -Raw | ConvertFrom-Json
    foreach ($file in $pin.files.PSObject.Properties) {
        $path = Join-Path 'tools/RawAccelBridge/upstream' $file.Name
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Value) { throw "Upstream header changed: $($file.Name)" }
    }
    if (@(Get-ChildItem -LiteralPath tools/RawAccelBridge/upstream -File).Count -ne @($pin.files.PSObject.Properties).Count) { throw 'Unexpected upstream files.' }
    New-Item -ItemType Directory -Force -Path artifacts/native | Out-Null
    $compilerArgs = @('-std=c++17','-target','x86_64-windows-gnu','-mcpu=baseline','-Wall','-Wextra','-Werror','-fno-strict-aliasing','-isystem','tools/RawAccelBridge/upstream')
    & $zigExe c++ @compilerArgs -O2 -shared tools/RawAccelBridge/rawaccel_bridge.cpp -o artifacts/native/0Accel.RawAccel.dll
    if ($LASTEXITCODE -ne 0) { throw 'Raw Accel user-mode bridge build failed.' }
    if (!$SkipTests) {
        & $zigExe c++ @compilerArgs -O2 tests/rawaccel_bridge_tests.cpp -o artifacts/native/rawaccel_bridge_tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Raw Accel offline test build failed.' }
        & .\artifacts\native\rawaccel_bridge_tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Raw Accel offline tests failed.' }
        if ($Sanitize) {
            & $zigExe c++ @compilerArgs -O1 -g -fsanitize=undefined -fno-sanitize-recover=all tests/rawaccel_bridge_tests.cpp -o artifacts/native/rawaccel_bridge_ubsan.exe
            if ($LASTEXITCODE -ne 0) { throw 'Raw Accel sanitizer build failed.' }
            & .\artifacts\native\rawaccel_bridge_ubsan.exe
            if ($LASTEXITCODE -ne 0) { throw 'Raw Accel sanitizer tests failed.' }
        }
        & $dotnetExe run --project tests/RawAccelTests/RawAccelTests.csproj -c Release @restoreProperties
        if ($LASTEXITCODE -ne 0) { throw 'Raw Accel managed offline tests failed.' }
    }
    Write-Host 'Raw Accel bridge ready. No driver installation, loading or device I/O.'
} finally { Pop-Location }
