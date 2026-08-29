param([ValidateSet('Preview', 'Production')][string]$Channel = 'Preview')
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
$buildStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$destination = Join-Path $releases.FullName "0Accel-$version-preview-win-x64-$buildStamp.zip"
# Preserve existing packages. Every archive gets a distinct name and checksum.
Compress-Archive -LiteralPath $files.FullName -DestinationPath $destination
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
[PSCustomObject]@{ Channel = 'Preview'; Version = $version; Path = $destination; SHA256 = $hash }
