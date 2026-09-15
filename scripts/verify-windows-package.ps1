param(
    [Parameter(Mandatory = $true)][string]$Bundle,
    [string]$TestRoot
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$Bundle = [IO.Path]::GetFullPath($Bundle)
if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    $TestRoot = Join-Path $root ".test-state\package-verification"
}
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
if (-not $TestRoot.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TestRoot must be inside the project directory."
}
if (Test-Path -LiteralPath $TestRoot) { Remove-Item -LiteralPath $TestRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $TestRoot | Out-Null

$setup = Join-Path $Bundle "CodexBridge.Setup.exe"
$payload = Join-Path $Bundle "payload"
$local = Join-Path $TestRoot "LocalAppData"
# Keep the package smoke test path ASCII-compatible with Windows PowerShell 5.1.
# Chinese install paths remain covered by PackageInstaller unit tests.
$install = Join-Path $TestRoot "Custom Directory\CodexBridge"
& $setup install --source $payload --local-app-data $local --install-dir $install
if ($LASTEXITCODE -ne 0) { throw "Isolated install failed." }
& $setup verify --local-app-data $local --install-dir $install
if ($LASTEXITCODE -ne 0) { throw "Isolated install verification failed." }

$data = Join-Path $local "CodexBridge"
New-Item -ItemType Directory -Force -Path $data | Out-Null
$device = Join-Path $data "devices.json"
$config = Join-Path $data "bridge-config.json"
Set-Content -LiteralPath $device -Value "fake-device" -Encoding ASCII
Set-Content -LiteralPath $config -Value "fake-config" -Encoding ASCII
$deviceHash = (Get-FileHash -LiteralPath $device -Algorithm SHA256).Hash
$configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash

& $setup repair --source $payload --local-app-data $local --install-dir $install
if ($LASTEXITCODE -ne 0) { throw "Isolated repair failed." }
if ((Get-FileHash -LiteralPath $device -Algorithm SHA256).Hash -ne $deviceHash -or
    (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -ne $configHash) {
    throw "Repair changed device or configuration data."
}

& $setup uninstall --local-app-data $local --install-dir $install
if ($LASTEXITCODE -ne 0) { throw "Isolated uninstall failed." }
if (-not (Test-Path -LiteralPath $device) -or -not (Test-Path -LiteralPath $config)) {
    throw "Default uninstall did not preserve data."
}
& $setup install --source $payload --local-app-data $local --install-dir $install
if ($LASTEXITCODE -ne 0) { throw "Isolated reinstall failed." }
& $setup uninstall --purge-data --local-app-data $local --install-dir $install
if ($LASTEXITCODE -ne 0) { throw "Isolated purge uninstall failed." }
if (Test-Path -LiteralPath $data) { throw "--purge-data did not remove isolated data." }

Remove-Item -LiteralPath $TestRoot -Recurse -Force
Write-Output "PACKAGE_VERIFICATION=PASS"
