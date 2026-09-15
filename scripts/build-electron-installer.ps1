param(
    [string]$Version = '0.2.0-electron.1'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$desktop = Join-Path $root 'src\CodexBridge.Desktop'
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$runtime = Join-Path $desktop '.electron-runtime'
$outputRoot = Join-Path $root 'artifacts\electron-installer'
$staging = Join-Path $outputRoot '.staging'
$bundle = Join-Path $staging 'Codex Remote'
$resources = Join-Path $bundle 'resources'
$appRoot = Join-Path $resources 'app'
$hostRoot = Join-Path $resources 'host'
$transportRoot = Join-Path $resources 'host'
$installer = Join-Path $outputRoot 'Codex-Remote-Electron-Setup.exe'

foreach ($path in @($staging, $installer)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if (-not (Test-Path -LiteralPath $runtime -PathType Container)) { throw "Electron runtime not found: $runtime" }
New-Item -ItemType Directory -Force -Path $appRoot, $hostRoot | Out-Null

# The Host starts the authenticated WebRTC sidecar from its base directory.
# Electron packaging must include the sidecar and its integrity manifest; the
# standalone .NET publish does not produce these Go binaries.
& (Join-Path $root 'scripts\build-transport.ps1') -Configuration 'electron'
if ($LASTEXITCODE -ne 0) { throw 'Transport build failed.' }
$transportSource = Join-Path $root 'artifacts\transport\electron\CodexBridge.Transport.exe'
$transportManifestSource = Join-Path $root 'artifacts\transport\electron\transport-manifest.json'
Copy-Item -LiteralPath $transportSource, $transportManifestSource -Destination $hostRoot -Force

# Publish the Host self-contained: an installed Electron client must not depend on a machine-wide .NET runtime.
& $dotnet publish (Join-Path $root 'src\CodexBridge.Host\CodexBridge.Host.csproj') `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -p:DebugType=None -p:DebugSymbols=false -o $hostRoot
if ($LASTEXITCODE -ne 0) { throw 'Host self-contained publish failed.' }

Get-ChildItem -LiteralPath $runtime -Force | Copy-Item -Destination $bundle -Recurse -Force
Rename-Item -LiteralPath (Join-Path $bundle 'electron.exe') -NewName 'Codex Remote.exe'
foreach ($item in @('main', 'preload', 'renderer', 'package.json')) {
    Copy-Item -LiteralPath (Join-Path $desktop $item) -Destination $appRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $desktop 'assets') -Destination (Join-Path $appRoot 'assets') -Recurse -Force

$nsis = Join-Path $root 'deploy\windows\CodexRemoteElectron.nsi'
$makensis = Join-Path $root '.tools\nsis-3.11\makensis.exe'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
& $makensis /V3 /INPUTCHARSET UTF8 "/DBUNDLE_DIR=$bundle" "/DOUTPUT_EXE=$installer" "/DVERSION=$Version" $nsis
if ($LASTEXITCODE -ne 0) { throw 'Electron NSIS build failed.' }

$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "INSTALLER=$installer"
Write-Output "SHA256=$hash"
