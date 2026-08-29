param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$files = Get-ChildItem -LiteralPath $Directory -Recurse -File
if ($files | Where-Object Extension -in '.png','.ico','.pdb') {
    throw 'Loose branding or debug files found. Run a clean build first.'
}
if ($files | Where-Object Extension -in '.sys','.inf','.cat','.cer','.pfx') {
    throw 'Driver or signing artifacts cannot be included; Raw Accel is installed separately.'
}
