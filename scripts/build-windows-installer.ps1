param(
    [string]$Version = '0.1.0-beta.7',
    [switch]$SkipPayloadBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $root "artifacts\windows-beta\CodexBridge-$Version-win-x64"
if (-not $SkipPayloadBuild) {
    & (Join-Path $root 'scripts\build-windows-beta.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Windows payload build failed.' }
}
if (-not (Test-Path -LiteralPath $bundle -PathType Container)) { throw "Bundle not found: $bundle" }

& (Join-Path $root 'scripts\bootstrap-nsis.ps1')
if ($LASTEXITCODE -ne 0) { throw 'NSIS bootstrap failed.' }

$outputRoot = Join-Path $root 'artifacts\windows-installer'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$output = Join-Path $outputRoot 'CodexBridge-Setup.exe'
$productVersion = '0.1.0.6'
if ($Version -match '^(\d+)\.(\d+)\.(\d+).*?(\d+)$') {
    $productVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$($Matches[4])"
}
$makensis = Join-Path $root '.tools\nsis-3.11\makensis.exe'
& $makensis /V3 /INPUTCHARSET UTF8 "/DVERSION=$Version" "/DPRODUCT_VERSION=$productVersion" `
    "/DBUNDLE_DIR=$bundle" "/DOUTPUT_EXE=$output" `
    (Join-Path $root 'deploy\windows\CodexBridge.nsi')
if ($LASTEXITCODE -ne 0) { throw 'NSIS build failed.' }

$limit = 70MB
$file = Get-Item -LiteralPath $output
if ($file.Length -gt $limit) { throw "Installer exceeds 70 MiB: $($file.Length) bytes" }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
"$hash  $($file.Name)" | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ASCII
& (Join-Path $root 'scripts\report-windows-package-size.ps1') `
    -Payload (Join-Path $bundle 'payload') `
    -Setup (Join-Path $bundle 'CodexBridge.Setup.exe') `
    -Output (Join-Path $outputRoot 'size-report.json')
Write-Output "INSTALLER=$output"
Write-Output "BYTES=$($file.Length)"
Write-Output "SHA256=$hash"
