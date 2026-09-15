param()

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
$project = Join-Path $root "src\CodexBridge.Host\CodexBridge.Host.csproj"

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Project-local .NET SDK was not found: $dotnet"
}

$env:DOTNET_CLI_HOME = Join-Path $root ".tools\dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = Join-Path $root ".tools\nuget-packages"
if ([string]::IsNullOrWhiteSpace($env:CODEX_BRIDGE_REMOTE_ENABLED)) { $env:CODEX_BRIDGE_REMOTE_ENABLED = "1" }
if ([string]::IsNullOrWhiteSpace($env:CODEX_BRIDGE_SIGNAL_URL)) { $env:CODEX_BRIDGE_SIGNAL_URL = "wss://remote.example.invalid:8443/signal" }
if ([string]::IsNullOrWhiteSpace($env:CODEX_BRIDGE_REMOTE_APP_URL)) { $env:CODEX_BRIDGE_REMOTE_APP_URL = "https://remote.example.invalid:8443/remote/" }
if ([string]::IsNullOrWhiteSpace($env:CODEX_BRIDGE_ENTITLEMENT_URL)) { $env:CODEX_BRIDGE_ENTITLEMENT_URL = "https://remote.example.invalid:8443/" }
foreach ($name in "CODEX_BRIDGE_TRANSPORT_PATH", "CODEX_BRIDGE_TRANSPORT_MANIFEST") {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrWhiteSpace($value) -and -not [IO.Path]::IsPathRooted($value)) {
        [Environment]::SetEnvironmentVariable($name, [IO.Path]::GetFullPath((Join-Path $root $value)))
    }
}

Write-Host ""
Write-Host "Local management URL: http://127.0.0.1:5096/"
Write-Host "Mobile access uses the configured public HTTPS/WebRTC service only."
Write-Host "Press Ctrl+C to stop."
Write-Host ""

Push-Location $root
try {
    & $dotnet run --project $project --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Codex Bridge Host exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
