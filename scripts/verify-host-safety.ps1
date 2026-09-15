param(
    [ValidateSet("Capture", "Verify")]
    [string]$Mode = "Verify"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
$testProjectName = -join @(
    [char]0x6D4B
    [char]0x8BD5
    [char]0x4F1A
    [char]0x8BDD
)
$testProject = Join-Path (Split-Path $root -Parent) $testProjectName
$snapshotDirectory = Join-Path $root ".safety"
$snapshotPath = Join-Path $snapshotDirectory "test-project-files.json"

function Get-TestProjectSnapshot {
    if (-not (Test-Path -LiteralPath $testProject -PathType Container)) {
        throw "Test project does not exist: $testProject"
    }

    return @(
        Get-ChildItem -LiteralPath $testProject -Recurse -File -Force |
            Sort-Object FullName |
            ForEach-Object {
                [pscustomobject]@{
                    Path = $_.FullName.Substring($testProject.Length).TrimStart('\')
                    Length = $_.Length
                    LastWriteTimeUtcTicks = $_.LastWriteTimeUtc.Ticks
                }
            }
    )
}

function ConvertTo-SnapshotJson($snapshot) {
    $items = @($snapshot | Where-Object { $null -ne $_ })
    if ($items.Count -eq 0) {
        return "[]"
    }

    return ConvertTo-Json -InputObject $items -Depth 4 -Compress
}

function Assert-SafeApiRoutes {
    $source = Get-Content -Raw -Encoding UTF8 (Join-Path $root "src\CodexBridge.Host\HostApplication.cs")
    $routeMatches = [regex]::Matches(
        $source,
        '\.Map(?:Get|Post|Put|Patch|Delete)\("(?<route>/api/[^" ]+)"')
    $actual = @($routeMatches | ForEach-Object { $_.Groups["route"].Value } | Sort-Object -Unique)
    $expected = @(
        "/api/management/devices",
        "/api/management/devices/remote/{deviceId}/entitlement",
        "/api/management/devices/remote/{deviceId}/redeem",
        "/api/management/devices/{transport}/{deviceId}",
        "/api/management/diagnostics",
        "/api/status"
    ) | Sort-Object

    if ((Compare-Object $expected $actual).Count -ne 0) {
        throw "API route allowlist mismatch.`nExpected: $($expected -join ', ')`nActual: $($actual -join ', ')"
    }
}

function Assert-SafeActionModel {
    $source = Get-Content -Raw -Encoding UTF8 (Join-Path $root "src\CodexBridge.Core\BridgeAction.cs")
    $body = [regex]::Match($source, 'enum\s+BridgeAction\s*\{(?<body>[^}]*)\}', 'Singleline')
    if (-not $body.Success) {
        throw "BridgeAction enum was not found."
    }

    $actions = @(
        $body.Groups["body"].Value -split ',' |
            ForEach-Object { ($_ -replace '//.*', '').Trim() } |
            Where-Object { $_ }
    )
    if ((Compare-Object @("View", "Send") $actions).Count -ne 0) {
        throw "BridgeAction must contain only View and Send. Actual: $($actions -join ', ')"
    }
}

$env:DOTNET_CLI_HOME = Join-Path $root ".tools\dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = Join-Path $root ".tools\nuget-packages"

Push-Location $root
try {
    & $dotnet test CodexBridge.sln --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Automated tests failed."
    }

    Assert-SafeApiRoutes
    Assert-SafeActionModel
    $current = Get-TestProjectSnapshot
    $currentJson = ConvertTo-SnapshotJson $current

    if ($Mode -eq "Capture") {
        New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
        Set-Content -LiteralPath $snapshotPath -Encoding UTF8 -Value $currentJson
        Write-Host "Safety baseline captured: $snapshotPath"
        Write-Host "Tracked test-project files: $($current.Count)"
        return
    }

    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) {
        throw "Safety baseline is missing. Run this script once with -Mode Capture before live validation."
    }

    $baselineJson = (Get-Content -Raw -Encoding UTF8 $snapshotPath).Trim()
    if ($baselineJson -cne $currentJson) {
        throw "Test project files changed during validation. Capture a new baseline only after reviewing the project manually."
    }

    Write-Host "Safety verification passed."
    Write-Host "API routes: allowlist matched."
    Write-Host "Action model: View and Send only."
    Write-Host "Test project files: unchanged ($($current.Count) files)."
}
finally {
    Pop-Location
}
