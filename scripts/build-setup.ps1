param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appDirectory = Join-Path $projectRoot 'artifacts\app'
$nativeDirectory = Join-Path $projectRoot 'artifacts\native'
$toolsDirectory = Join-Path $projectRoot '.tools'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\releases'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')).TrimEnd('\') + '\'
if (!$OutputDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Setup output must stay inside artifacts: $OutputDirectory"
}

$panel = Join-Path $appDirectory 'app\0Accel.Panel.dll'
$core = Join-Path $appDirectory 'app\coreclr.dll'
$icon = Join-Path $nativeDirectory '0Accel.ico'
foreach ($required in @((Join-Path $appDirectory '0Accel.exe'), $panel, $core, $icon)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Self-contained application build is incomplete: $required"
    }
}
$version = ([Version][Diagnostics.FileVersionInfo]::GetVersionInfo($panel).FileVersion).ToString(3)

$rawAccelVersion = '1.7.1'
$rawAccelArchiveHash = '770fe3ae0919ca3c4d412f58c985eb27f5434decad809f7e8206de4e8852eec4'
$driverHash = '8a62c4deef2774b43a7363b352eda79897533a1080c9c26ffeff0559e43358d7'
$rawAccelArchive = Join-Path $toolsDirectory "RawAccel_v$rawAccelVersion.zip"
$rawAccelDirectory = Join-Path $toolsDirectory "RawAccel_v$rawAccelVersion"
$driver = Join-Path $rawAccelDirectory 'RawAccel\driver\rawaccel.sys'
$rawAccelUrl = "https://github.com/RawAccelOfficial/rawaccel/releases/download/v$rawAccelVersion/RawAccel_v$rawAccelVersion.zip"

New-Item -ItemType Directory -Force -Path $toolsDirectory, $nativeDirectory, $OutputDirectory | Out-Null
if (!(Test-Path -LiteralPath $rawAccelArchive -PathType Leaf)) {
    Write-Host "Downloading pinned Raw Accel $rawAccelVersion payload..."
    Invoke-WebRequest -Uri $rawAccelUrl -OutFile $rawAccelArchive
}
if ((Get-FileHash -LiteralPath $rawAccelArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $rawAccelArchiveHash) {
    throw 'Raw Accel release archive SHA-256 mismatch.'
}
if (!(Test-Path -LiteralPath $driver -PathType Leaf)) {
    Expand-Archive -LiteralPath $rawAccelArchive -DestinationPath $rawAccelDirectory -Force
}
if ((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant() -ne $driverHash) {
    throw 'Raw Accel driver SHA-256 mismatch.'
}
$driverSignature = Get-AuthenticodeSignature -LiteralPath $driver
if ($driverSignature.Status -ne 'Valid' -or
    $driverSignature.SignerCertificate.Subject -notlike '*Microsoft Windows Hardware Compatibility Publisher*') {
    throw "Raw Accel driver signature validation failed: $($driverSignature.Status)"
}

$zig = Join-Path $toolsDirectory 'zig-x86_64-windows-0.15.2\zig.exe'
if (!(Test-Path -LiteralPath $zig -PathType Leaf)) {
    $zig = (Get-Command zig -ErrorAction Stop).Source
}
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $toolsDirectory 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $projectRoot 'artifacts\zig-cache'
New-Item -ItemType Directory -Force -Path $env:ZIG_GLOBAL_CACHE_DIR,$env:ZIG_LOCAL_CACHE_DIR | Out-Null
$helperResource = Join-Path $nativeDirectory 'driver_helper.res'
$helper = Join-Path $nativeDirectory '0Accel.DriverSetup.exe'
& $zig rc /fo $helperResource (Join-Path $projectRoot 'setup\driver_helper.rc') | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'Driver helper resource build failed.' }
& $zig c++ -std=c++20 -O2 -target x86_64-windows-gnu -mcpu=baseline -Wall -Wextra -Werror -municode `
    (Join-Path $projectRoot 'setup\driver_helper.cpp') $helperResource `
    -lsetupapi -lbcrypt -lwintrust -lcrypt32 -ladvapi32 -lshell32 -lole32 -o $helper |
    ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'Driver helper build failed.' }

& $helper verify $driver
if ($LASTEXITCODE -ne 0) { throw 'Driver helper rejected the pinned Raw Accel payload.' }
$testDirectory = Join-Path $projectRoot ('artifacts\tests\setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
try {
    $mutatedDriver = Join-Path $testDirectory 'rawaccel-mutated.sys'
    Copy-Item -LiteralPath $driver -Destination $mutatedDriver
    $bytes = [IO.File]::ReadAllBytes($mutatedDriver)
    $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($mutatedDriver, $bytes)
    & $helper verify $mutatedDriver
    if ($LASTEXITCODE -eq 0) { throw 'Driver helper accepted a modified payload.' }
} finally {
    if (Test-Path -LiteralPath $testDirectory) {
        Remove-Item -LiteralPath $testDirectory -Recurse -Force
    }
}

$innoVersion = '6.7.3'
$innoInstallerHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$innoDirectory = Join-Path $toolsDirectory "InnoSetup-$innoVersion"
$innoCompiler = Join-Path $innoDirectory 'ISCC.exe'
if (!(Test-Path -LiteralPath $innoCompiler -PathType Leaf)) {
    $innoInstaller = Join-Path $toolsDirectory "innosetup-$innoVersion.exe"
    if (!(Test-Path -LiteralPath $innoInstaller -PathType Leaf)) {
        Write-Host "Downloading pinned Inno Setup $innoVersion compiler..."
        Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$innoVersion.exe" -OutFile $innoInstaller
    }
    if ((Get-FileHash -LiteralPath $innoInstaller -Algorithm SHA256).Hash.ToLowerInvariant() -ne $innoInstallerHash) {
        throw 'Inno Setup installer SHA-256 mismatch.'
    }
    $innoSignature = Get-AuthenticodeSignature -LiteralPath $innoInstaller
    if ($innoSignature.Status -ne 'Valid' -or $innoSignature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') {
        throw "Inno Setup signature validation failed: $($innoSignature.Status)"
    }
    $process = Start-Process -FilePath $innoInstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',"/DIR=$innoDirectory") -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $innoCompiler -PathType Leaf)) {
        throw "Inno Setup compiler installation failed: $($process.ExitCode)"
    }
}

$arguments = @(
    '/Qp',
    "/DAppVersion=$version",
    "/DAppSource=$appDirectory",
    "/DDriverSource=$driver",
    "/DDriverHelper=$helper",
    "/DOutputDirectory=$OutputDirectory",
    "/DSetupIcon=$icon",
    (Join-Path $projectRoot 'setup\0Accel.iss')
)
& $innoCompiler $arguments | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw '0Accel setup compilation failed.' }

$setup = Join-Path $OutputDirectory "0Accel-Setup-$version-preview-win-x64.exe"
if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Setup output is missing.' }
$setupVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($setup).FileVersion
if ($setupVersion.ToString(3) -ne $version) { throw "Setup version mismatch: $setupVersion" }
$setupSignature = Get-AuthenticodeSignature -LiteralPath $setup
if ($setupSignature.Status -notin @('NotSigned','Valid')) {
    throw "Setup Authenticode state is invalid: $($setupSignature.Status)"
}
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($setup + '.sha256', "$setupHash  $([IO.Path]::GetFileName($setup))`n", [Text.UTF8Encoding]::new($false))

[PSCustomObject]@{
    Version = $version
    Path = $setup
    SHA256 = $setupHash
    DriverVersion = $rawAccelVersion
    DriverSHA256 = $driverHash
    SetupSignature = $setupSignature.Status.ToString()
}
