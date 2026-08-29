param(
    [ValidateSet('Preview', 'Production')][string]$Channel = 'Preview',
    [switch]$SkipSetup,
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
# Intentionally closed until physical input, lifecycle and release review are complete.
if ($Channel -eq 'Production') {
    throw 'Production packaging is disabled: Raw Accel integration still needs physical input/lifecycle validation. See README.md.'
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appDirectory = Join-Path $projectRoot 'artifacts\app'
if (-not (Test-Path -LiteralPath (Join-Path $appDirectory '0Accel.exe'))) { throw 'Build first.' }
if (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'app/0Accel.Panel.exe'))) { throw 'Rebuild using the current package layout first.' }
& (Join-Path $PSScriptRoot 'assert-preview-files.ps1') -Directory $appDirectory
$files = Get-ChildItem -LiteralPath $appDirectory
$assembly = Join-Path $appDirectory 'app/0Accel.Panel.dll'
$binaryVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).FileVersion
$version = $binaryVersion.ToString(3)
$releases = New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts/releases')
$releaseRoot = [IO.Path]::GetFullPath($releases.FullName).TrimEnd('\') + '\'
if ($Clean) {
    foreach ($item in (Get-ChildItem -LiteralPath $releases.FullName -File)) {
        if (!$item.FullName.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release cleanup escaped artifacts: $($item.FullName)"
        }
        Remove-Item -LiteralPath $item.FullName -Force
    }
}
$destination = Join-Path $releases.FullName "0Accel-$version-preview-portable-win-x64.zip"
Compress-Archive -LiteralPath $files.FullName -DestinationPath $destination -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($destination)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('0Accel.exe','app/0Accel.Panel.exe','app/0Accel.RawAccel.dll','app/licenses/RawAccel-MIT.txt','LICENSE')) {
        if ($required -notin $entries) { throw "Package is missing $required" }
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($destination + '.sha256', "$hash  $([IO.Path]::GetFileName($destination))`n", [Text.UTF8Encoding]::new($false))
$setupResult = $null
if (!$SkipSetup) {
    $setupResult = & (Join-Path $PSScriptRoot 'build-setup.ps1') -OutputDirectory $releases.FullName
}
[PSCustomObject]@{
    Channel = 'Preview'
    Version = $version
    PortablePath = $destination
    PortableSHA256 = $hash
    SetupPath = if ($setupResult) { $setupResult.Path } else { $null }
    SetupSHA256 = if ($setupResult) { $setupResult.SHA256 } else { $null }
}
