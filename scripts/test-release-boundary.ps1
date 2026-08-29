param([switch]$SkipPackaging)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = Join-Path $PSScriptRoot 'package.ps1'
$rejected = $false
try { & $package -Channel Production | Out-Null }
catch {
    if ($_.Exception.Message -notlike 'Production packaging is disabled:*') { throw }
    $rejected = $true
}
if (-not $rejected) { throw 'Incomplete product could be packaged as production.' }
Write-Output 'PASS: production packaging remains closed until physical input and lifecycle acceptance is complete'
$guard = Join-Path $PSScriptRoot 'assert-preview-files.ps1'
$testRoot = New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts\tests\preview')
$fixtures = New-Item -ItemType Directory -Path (Join-Path $testRoot.FullName ('preview-boundary-' + [Guid]::NewGuid().ToString('N')))
& $guard -Directory $fixtures.FullName
foreach ($extension in @('sys','SYS','inf','cat','cer','pfx')) {
    $case = New-Item -ItemType Directory -Path (Join-Path $fixtures.FullName ([Guid]::NewGuid().ToString('N')))
    [IO.File]::WriteAllBytes((Join-Path $case.FullName ('forbidden.' + $extension)), [byte[]]::new(0))
    $rejected = $false
    try { & $guard -Directory $case.FullName }
    catch {
        if ($_.Exception.Message -notlike 'Driver or signing artifacts*') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw "Preview guard accepted .$extension" }
}
Write-Output 'PASS: preview rejects SYS/INF/CAT and signing artifacts, including nested/mixed-case files'
if ($SkipPackaging) {
    Write-Output 'Archive creation skipped; existing preview packages left unchanged.'
    return
}
$result = & $package -Channel Preview
if ($result.Channel -ne 'Preview' -or $result.PortablePath -notlike '*-preview-portable-win-x64.zip') { throw 'Preview package is mislabeled.' }
$actualHash = (Get-FileHash -LiteralPath $result.PortablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $result.PortableSHA256) { throw 'Package checksum mismatch.' }
$sidecar = [IO.File]::ReadAllText($result.PortablePath + '.sha256')
if ($sidecar -ne "$actualHash  $([IO.Path]::GetFileName($result.PortablePath))`n") { throw 'Checksum file is invalid.' }
if (!(Test-Path -LiteralPath $result.SetupPath -PathType Leaf)) { throw 'Setup package is missing.' }
$setupHash = (Get-FileHash -LiteralPath $result.SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($setupHash -ne $result.SetupSHA256) { throw 'Setup checksum mismatch.' }
$releaseFiles = @(Get-ChildItem -LiteralPath (Split-Path $result.PortablePath) -File)
if ($releaseFiles | Where-Object Extension -in '.sys','.inf','.cat','.pdb') { throw 'Loose driver/debug files escaped into releases.' }
Write-Output "PASS: portable archive and one-file setup verified: $($result.PortablePath), $($result.SetupPath)"
