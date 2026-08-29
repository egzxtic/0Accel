param([switch]$SkipTests,[switch]$SelfContained,[switch]$Sanitize)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $dotnetExe=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnetExe)) { $dotnetExe=(Get-Command dotnet -ErrorAction Stop).Source }
    $zigExe=Join-Path $projectRoot '.tools/zig-x86_64-windows-0.15.2/zig.exe'
    if (!(Test-Path -LiteralPath $zigExe)) { $zigExe=(Get-Command zig -ErrorAction Stop).Source }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
    $appDirectory=Join-Path $projectRoot 'artifacts/app'
    $running=Get-Process -Name '0Accel','0Accel.Panel' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($appDirectory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
    }
    if ($running) { throw 'Close this build of 0Accel before rebuilding. No running process was terminated.' }

    & (Join-Path $PSScriptRoot 'build-rawaccel.ps1') -SkipTests:$SkipTests -Sanitize:$Sanitize
    if (!$SkipTests) {
        & $dotnetExe run --project tests/SettingsTests/SettingsTests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Settings tests failed.' }
    }

    New-Item -ItemType Directory -Force -Path artifacts/native,artifacts/staging | Out-Null
    & $dotnetExe run --project tools/IconGenerator/IconGenerator.csproj -c Release -- artifacts/native/0Accel.ico --source assets/branding/icon-white.png
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    & $zigExe rc /fo artifacts/native/host.res host/0Accel.rc
    if ($LASTEXITCODE -ne 0) { throw 'Host resource build failed.' }
    & $zigExe cc -std=c11 -O2 -target x86_64-windows-gnu -mcpu=baseline -Wall -Wextra -Werror '-Wl,--subsystem,windows' -municode host/host.c artifacts/native/host.res -lshell32 -ladvapi32 -luser32 -o artifacts/native/0Accel.exe
    if ($LASTEXITCODE -ne 0) { throw 'Native tray host build failed.' }

    $buildId=[Guid]::NewGuid().ToString('N')
    $staging=Join-Path $projectRoot "artifacts/staging/$buildId"
    $panelDirectory=Join-Path $staging 'app'
    $contained=if($SelfContained){'true'}else{'false'}
    & $dotnetExe publish src/ZeroAccel/ZeroAccel.csproj -c Release -r win-x64 --self-contained $contained -o $panelDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    Copy-Item -LiteralPath artifacts/native/0Accel.exe -Destination (Join-Path $staging '0Accel.exe')
    Copy-Item -LiteralPath LICENSE -Destination $staging
    foreach($binary in @((Join-Path $staging '0Accel.exe'),(Join-Path $panelDirectory '0Accel.Panel.exe'))){
        & $dotnetExe run --no-build --project tools/IconGenerator/IconGenerator.csproj -c Release -- --verify $binary artifacts/native/0Accel.ico
        if($LASTEXITCODE -ne 0){throw "Embedded icon validation failed: $binary"}
    }
    & (Join-Path $PSScriptRoot 'assert-preview-files.ps1') -Directory $staging

    $artifactsRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
    $backup=Join-Path $projectRoot "artifacts/app.previous-$buildId"
    foreach($target in @($appDirectory,$staging,$backup)){
        if(![IO.Path]::GetFullPath($target).StartsWith($artifactsRoot,[StringComparison]::OrdinalIgnoreCase)){throw "Build path outside artifacts: $target"}
    }
    if(Test-Path -LiteralPath $appDirectory){Move-Item -LiteralPath $appDirectory -Destination $backup}
    try{Move-Item -LiteralPath $staging -Destination $appDirectory}
    catch{
        if((Test-Path -LiteralPath $backup)-and !(Test-Path -LiteralPath $appDirectory)){Move-Item -LiteralPath $backup -Destination $appDirectory}
        throw
    }
    if(Test-Path -LiteralPath $backup){Remove-Item -LiteralPath $backup -Recurse -Force}
    & (Join-Path $PSScriptRoot 'refresh-icons.ps1')
    Write-Host 'Build ready: artifacts\app\0Accel.exe'
}finally{Pop-Location}
