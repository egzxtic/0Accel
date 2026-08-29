param([string]$ReleaseDirectory = '.tools/rawaccel-v1.7.1/release/RawAccel')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    # Optional integration reference; pure managed/native math only. Never execute
    # installer.exe/writer.exe or load rawaccel.sys. Release binaries stay local.
    $zip = Join-Path $projectRoot '.tools/rawaccel-v1.7.1/RawAccel_v1.7.1.zip'
    if (!(Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip).Hash -ne '770fe3ae0919ca3c4d412f58c985eb27f5434decad809f7e8206de4e8852eec4') { throw 'Download the pinned official release ZIP first (see root README.md).' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($name in @('wrapper.dll','Newtonsoft.Json.dll')) {
            $entry = @($archive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq ('RawAccel/'+$name) })[0]
            if (!$entry) { throw "Missing reference: $name" }
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $expected = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory $name)).Hash -ne $expected) { throw "Reference changed: $name" }
        }
    } finally { $archive.Dispose() }
    $out = New-Item -ItemType Directory -Force -Path artifacts/tests/rawaccel-reference
    Copy-Item -LiteralPath (Join-Path $ReleaseDirectory 'wrapper.dll'),(Join-Path $ReleaseDirectory 'Newtonsoft.Json.dll'),artifacts/native/0Accel.RawAccel.dll -Destination $out.FullName
    $compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /platform:x64 /optimize+ /unsafe ('/out:'+(Join-Path $out.FullName 'RawAccelReference.exe')) ('/reference:'+(Join-Path $out.FullName 'wrapper.dll')) ('/reference:'+(Join-Path $out.FullName 'Newtonsoft.Json.dll')) (Join-Path $projectRoot 'tests\RawAccelReference.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Reference harness build failed.' }
    & (Join-Path $out.FullName 'RawAccelReference.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Official Raw Accel reference mismatch.' }
} finally { Pop-Location }
