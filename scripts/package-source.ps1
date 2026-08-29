param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rootPrefix = $projectRoot.TrimEnd('\') + '\'
Push-Location $projectRoot
try {
    # Git's candidate list excludes private local artifacts before we read them.
    # It includes untracked source so an initial, uncommitted project is supported.
    $candidates = @(git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or !$candidates.Count) { throw 'Cannot enumerate public source candidates.' }
    $source = @()
    $extensions = @('.c','.h','.cpp','.hpp','.cs','.csproj','.xaml','.manifest','.ps1','.cmd','.yml','.yaml','.json','.rc','.vcxproj','.inx','.iss','.config','.md','.txt','.xml')
    $secretPatterns = @('ghp_[A-Za-z0-9]{30,}', 'github_pat_[A-Za-z0-9_]{40,}', '-----BEGIN [A-Z ]*PRIVATE KEY-----')
    foreach ($candidate in ($candidates | Sort-Object -Unique)) {
        $relative = $candidate.Replace('\','/')
        $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        if (!$path.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escapes workspace.' }
        # Tracked deletions remain in `git ls-files --cached` until committed.
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if ($relative -match '(^|/)\.\.(/|$)' -or $relative -match '[\r\n]' -or
            $relative -notmatch '^(?:\.github/|assets/branding/|host/|scripts/|setup/|src/|tests/|tools/|[^/]+\.md$|LICENSE$|\.gitignore$)') {
            throw "Unexpected source candidate (not archived): $relative"
        }
        if ($relative -match '(^|/)(?:bin|obj|\.git|\.tools)(/|$)' -or
            $relative.StartsWith('artifacts/')) {
            throw "Private/generated path in source candidates: $relative"
        }
        $item = Get-Item -LiteralPath $path
        $parent = $item
        while ($parent -and $parent.FullName -ne $projectRoot) {
            if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in source path: $relative" }
            $parent = if ($parent -is [IO.FileInfo]) { $parent.Directory } else { $parent.Parent }
        }
        if ($item.PSIsContainer -or $item.Length -gt 5MB) { throw "Unexpected source file size/type: $relative" }
        $branding = $relative -match '^assets/branding/(?:icon|wordmark)-(?:black|white)\.png$'
        if (!$branding -and $relative -notin @('LICENSE','.gitignore','tools/RawAccelBridge/upstream/LICENSE') -and $item.Extension -notin $extensions) {
            throw "Unapproved source extension: $relative"
        }
        if (!$branding) {
            $content = [IO.File]::ReadAllText($path)
            foreach ($pattern in $secretPatterns) {
                if ($content -cmatch $pattern) { throw "Possible secret in $relative. Archive stopped; secret value is not printed." }
            }
        }
        $source += [PSCustomObject]@{ Path=$path; Relative=$relative; Bytes=$item.Length; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    if ($VerifyOnly) {
        Write-Output "PASS: $($source.Count) source candidates; generated/private paths rejected; narrow token/key scan passed (not a full secret audit)."
        return
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $output = New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts/source')
    $sourceVersion = ([xml](Get-Content -LiteralPath src/ZeroAccel/ZeroAccel.csproj -Raw)).Project.PropertyGroup.Version
    $name = "0Accel-$sourceVersion-source-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $zip = Join-Path $output.FullName ($name + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $source) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.Path,$file.Relative,[IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    # Verify every archived file's content, not just the number of entries.
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        if ($archive.Entries.Count -ne $source.Count) { throw 'Source archive count mismatch.' }
        foreach ($file in $source) {
            $entry = $archive.GetEntry($file.Relative)
            if (!$entry -or $entry.Length -ne $file.Bytes) { throw "Source archive mismatch: $($file.Relative)" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($actual -ne $file.SHA256) { throw "Source hash mismatch: $($file.Relative)" }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip+'.sha256',"$hash  $name.zip`n",[Text.UTF8Encoding]::new($false))
    $source | Select-Object Relative,Bytes,SHA256 | ConvertTo-Json | Set-Content -LiteralPath ($zip+'.manifest.json') -Encoding utf8
    [PSCustomObject]@{Path=$zip; Files=$source.Count; SHA256=$hash; Status='Source only; not published to GitHub'}
} finally { Pop-Location }
