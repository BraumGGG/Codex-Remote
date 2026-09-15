param(
    [switch]$IncludeResourceMeasurement
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
$go = Join-Path $root ".tools\go\bin\go.exe"
$node = "C:\Users\Redmi\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:APPDATA = Join-Path $root ".tools\appdata"
$env:NUGET_PACKAGES = Join-Path $root ".tools\nuget-packages"
$env:GOMAXPROCS = "1"
$env:GOCACHE = Join-Path $root ".tools\go-build-cache"
$env:GOMODCACHE = Join-Path $root ".tools\go-mod-cache"
$env:NODE_PATH = "C:\Users\Redmi\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules"
$env:CODEX_BRIDGE_TEST_ROOT = Join-Path $root ".test-state\host-remote-safety"
$isolatedOutput = Join-Path $root "artifacts\isolated-safety-tests"
$testProject = Join-Path (Split-Path $root -Parent) (-join @([char]0x6D4B,[char]0x8BD5,[char]0x4F1A,[char]0x8BDD))

function Invoke-Checked([scriptblock]$Command, [string]$Name) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Get-ProjectSnapshot {
    return @(
        Get-ChildItem -LiteralPath $testProject -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName.Substring($testProject.Length)
                Length = $_.Length
                Modified = $_.LastWriteTimeUtc.Ticks
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
    ) | ConvertTo-Json -Depth 4 -Compress
}

function Assert-ProtocolAllowlist {
    $rpc = Get-Content -Raw -Encoding UTF8 (Join-Path $root "src\CodexBridge.Remote.Protocol\RpcMethod.cs")
    foreach ($forbidden in @("Delete", "Archive", "Rename", "Pin", "Shell", "GenericRpc")) {
        if ($rpc -match "\b$forbidden\b") { throw "Forbidden remote RPC found: $forbidden" }
    }
    $routes = Get-Content -Raw -Encoding UTF8 (Join-Path $root "src\CodexBridge.Host\HostApplication.cs")
    if ($routes -match '\.MapPut|\.MapPatch') { throw "Unexpected mutating management route found." }
    $deleteRoutes = @([regex]::Matches($routes, '\.MapDelete\("([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    $allowedDelete = @(
        "/api/management/devices/{transport}/{deviceId}",
        "/api/management/devices/remote/{deviceId}/entitlement"
    )
    if ((Compare-Object $allowedDelete $deleteRoutes).Count -ne 0) {
        throw "DELETE routes must be limited to device and local entitlement revocation."
    }
}

function Assert-TransportManifest {
    $manifestPath = Join-Path $root "artifacts\transport\dev\transport-manifest.json"
    $manifest = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json
    $exe = Join-Path (Split-Path $manifestPath -Parent) $manifest.FileName
    $actual = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($actual -cne $manifest.Sha256.ToUpperInvariant()) { throw "Transport manifest hash mismatch." }
    if ($manifest.ProtocolVersion -ne 1) { throw "Transport protocol version mismatch." }
}

Push-Location $root
try {
    $snapshotBefore = Get-ProjectSnapshot
    $sidecarsBefore = @(Get-Process -Name "CodexBridge.Transport" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

    $projects = @(
        "tests\CodexBridge.Core.Tests\CodexBridge.Core.Tests.csproj",
        "tests\CodexBridge.Windows.Tests\CodexBridge.Windows.Tests.csproj",
        "tests\CodexBridge.Remote.Protocol.Tests\CodexBridge.Remote.Protocol.Tests.csproj",
        "tests\CodexBridge.Signal.Tests\CodexBridge.Signal.Tests.csproj"
    )
    foreach ($project in $projects) {
        Invoke-Checked { & $dotnet test $project -m:1 --no-restore } "dotnet test $project"
    }
    Invoke-Checked {
        & $dotnet test "tests\CodexBridge.Host.Tests\CodexBridge.Host.Tests.csproj" -m:1 --no-restore "-p:OutDir=$isolatedOutput\"
    } "Host tests"

    Push-Location (Join-Path $root "src\CodexBridge.Transport")
    try { Invoke-Checked { & $go test -p 1 .\... } "Go tests" }
    finally { Pop-Location }

    foreach ($spec in Get-ChildItem (Join-Path $root "tests\browser") -Filter "*.spec.mjs" | Sort-Object Name) {
        if ($spec.Name -eq 'public-remote-smoke.spec.mjs' -and
            [string]::IsNullOrWhiteSpace($env:CODEX_BRIDGE_PUBLIC_PAIRING_URL)) {
            Write-Host 'Skipping public-remote-smoke.spec.mjs (no isolated public pairing URL supplied).'
            continue
        }
        if ($spec.Name -eq 'sidecar-pipe-interop.spec.mjs' -and
            ([string]::IsNullOrWhiteSpace($env:TURN_CHECK_URLS) -or
             [string]::IsNullOrWhiteSpace($env:TURN_CHECK_USERNAME) -or
             [string]::IsNullOrWhiteSpace($env:TURN_CHECK_CREDENTIAL))) {
            Write-Host 'Skipping sidecar-pipe-interop.spec.mjs (no isolated TURN credentials supplied).'
            continue
        }
        if ($spec.Name -eq 'tailscale-interop.spec.mjs' -and
            [string]::IsNullOrWhiteSpace($env:TAILSCALE_IP)) {
            Write-Host 'Skipping tailscale-interop.spec.mjs (no isolated Tailscale address supplied).'
            continue
        }
        Invoke-Checked { & $node $spec.FullName } "browser test $($spec.Name)"
    }

    Assert-ProtocolAllowlist
    Assert-TransportManifest
    if ($IncludeResourceMeasurement) {
        Invoke-Checked { powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\measure-transport.ps1") } "resource measurement"
    }

    Start-Sleep -Seconds 2
    $newSidecars = @(Get-Process -Name "CodexBridge.Transport" -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -notin $sidecarsBefore })
    if ($newSidecars.Count -gt 0) { throw "Transport sidecar remained after tests." }
    if ((Get-ProjectSnapshot) -cne $snapshotBefore) { throw "Allowed test project changed during remote safety tests." }
    Write-Host "Remote safety verification passed."
    Write-Host "No real Codex message was submitted; all send tests used fakes."
}
finally { Pop-Location }
