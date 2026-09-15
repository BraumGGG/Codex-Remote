param(
    [string]$Configuration = "dev"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$go = Join-Path $root ".tools\go\bin\go.exe"
$transportRoot = Join-Path $root "src\CodexBridge.Transport"
$outputDirectory = Join-Path $root "artifacts\transport\$Configuration"
$output = Join-Path $outputDirectory "CodexBridge.Transport.exe"
$manifest = Join-Path $outputDirectory "transport-manifest.json"

if (-not (Test-Path -LiteralPath $go -PathType Leaf)) {
    throw "Pinned Go toolchain is unavailable. Run scripts/bootstrap-go.ps1 first."
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$env:GOMAXPROCS = "1"
$env:GOCACHE = Join-Path $root ".tools\go-build-cache"
$env:GOMODCACHE = Join-Path $root ".tools\go-mod-cache"

Push-Location $transportRoot
try {
    & $go build -p 1 -trimpath -buildvcs=false -o $output .\cmd\transport
    if ($LASTEXITCODE -ne 0) {
        throw "Transport build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
$manifestValue = [ordered]@{
    ProtocolVersion = 1
    FileName = "CodexBridge.Transport.exe"
    Sha256 = $hash
}
$manifestValue | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding ASCII
Write-Output "TRANSPORT_EXE=$output"
Write-Output "TRANSPORT_MANIFEST=$manifest"
Write-Output "TRANSPORT_SHA256=$hash"
