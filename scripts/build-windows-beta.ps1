param(
    [string]$Version = "0.1.0-beta.7",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root "artifacts\windows-beta"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be inside the project directory."
}

$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
$packageName = "CodexBridge-$Version-win-x64"
$work = Join-Path $OutputRoot ".build-$Version"
$payload = Join-Path $work "payload"
$setupPublish = Join-Path $work "setup"
$bundle = Join-Path $OutputRoot $packageName
$zip = "$bundle.zip"

foreach ($path in @($work, $bundle, $zip)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $payload, $setupPublish, $bundle | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $root ".test-state\dotnet-home"
$env:APPDATA = Join-Path $root ".test-state\appdata"
$env:NUGET_PACKAGES = Join-Path $root ".tools\nuget-packages"

& (Join-Path $root "scripts\build-transport.ps1") -Configuration "windows-beta"
if ($LASTEXITCODE -ne 0) { throw "Transport build failed." }

& $dotnet restore (Join-Path $root "src\CodexBridge.App\CodexBridge.App.csproj") `
    -r win-x64 --configfile (Join-Path $root "NuGet.Config")
if ($LASTEXITCODE -ne 0) { throw "App win-x64 restore failed." }
& $dotnet publish (Join-Path $root "src\CodexBridge.App\CodexBridge.App.csproj") `
    -c Release -r win-x64 --self-contained true --no-restore `
    -p:Version=$Version `
    -p:PublishTrimmed=true -p:TrimMode=partial -p:_SuppressWinFormsTrimError=true `
    -p:SuppressTrimAnalysisWarnings=false -p:TreatWarningsAsErrors=true `
    -p:DebugType=None -p:DebugSymbols=false -o $payload
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

# Host is linked into the WinForms entry point. These framework files belong only to
# the unused WPF native stack and are not loaded by FlaUI's UIA3 COM implementation.
foreach ($unused in @(
    "CodexBridge.Host.exe",
    "CodexBridge.Host.runtimeconfig.json",
    "D3DCompiler_47_cor3.dll",
    "wpfgfx_cor3.dll",
    "PresentationNative_cor3.dll",
    "PenImc_cor3.dll"
)) {
    $unusedPath = Join-Path $payload $unused
    if (Test-Path -LiteralPath $unusedPath) { Remove-Item -LiteralPath $unusedPath -Force }
}
$webRoot = Join-Path $payload 'wwwroot'
New-Item -ItemType Directory -Force -Path $webRoot | Out-Null
Copy-Item -Path (Join-Path $root 'src\CodexBridge.Host\wwwroot\*') `
    -Destination $webRoot -Recurse -Force

$transport = Join-Path $root "artifacts\transport\windows-beta\CodexBridge.Transport.exe"
$transportManifest = Join-Path $root "artifacts\transport\windows-beta\transport-manifest.json"
Copy-Item -LiteralPath $transport, $transportManifest -Destination $payload
Copy-Item -LiteralPath (Join-Path $root "docs\legal\UNSIGNED-NOTICE.txt") -Destination $payload
Copy-Item -LiteralPath (Join-Path $root "docs\legal\THIRD-PARTY-NOTICES.txt") -Destination $payload
$licenses = Join-Path $payload "licenses"
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $root "docs\legal\QRCoder-LICENSE.txt") -Destination $licenses
Copy-Item -LiteralPath (Join-Path $root ".tools\go-mod-cache\github.com\pion\webrtc\v4@v4.2.18\LICENSE") -Destination (Join-Path $licenses "Pion-WebRTC-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $root ".tools\nuget-packages\flaui.core\5.0.0\LICENSE.txt") -Destination (Join-Path $licenses "FlaUI-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $root "src\CodexBridge.Host\wwwroot\vendor\LICENSE-marked.md") -Destination $licenses
Copy-Item -LiteralPath (Join-Path $root "src\CodexBridge.Host\wwwroot\vendor\LICENSE-dompurify.txt") -Destination $licenses

$manifestFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
    [ordered]@{
        Path = $relative
        Length = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifest = [ordered]@{ SchemaVersion = 1; ProductVersion = $Version; Files = $manifestFiles }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload "package-manifest.json") -Encoding UTF8

& $dotnet restore (Join-Path $root "src\CodexBridge.Setup\CodexBridge.Setup.csproj") `
    -r win-x64 -p:PublishAot=true --configfile (Join-Path $root "NuGet.Config")
if ($LASTEXITCODE -ne 0) { throw "Setup win-x64 restore failed." }
& $dotnet publish (Join-Path $root "src\CodexBridge.Setup\CodexBridge.Setup.csproj") `
    -c Release -r win-x64 --self-contained true --no-restore `
    -p:Version=$Version -p:PublishAot=true `
    -p:DebugType=None -p:DebugSymbols=false -o $setupPublish
if ($LASTEXITCODE -ne 0) { throw "Setup publish failed." }

Copy-Item -LiteralPath (Join-Path $setupPublish "CodexBridge.Setup.exe") -Destination $bundle
Copy-Item -LiteralPath $payload -Destination (Join-Path $bundle "payload") -Recurse
Copy-Item -LiteralPath (Join-Path $root "docs\legal\UNSIGNED-NOTICE.txt") -Destination $bundle
$reproducibleTimestamp = [DateTime]::SpecifyKind([DateTime]"2000-01-01T00:00:00", [DateTimeKind]::Utc)
Get-ChildItem -LiteralPath $bundle -Recurse -File -Force | ForEach-Object { $_.IsReadOnly = $false }
Get-ChildItem -LiteralPath $bundle -Recurse -Force | ForEach-Object { $_.LastWriteTimeUtc = $reproducibleTimestamp }
(Get-Item -LiteralPath $bundle).LastWriteTimeUtc = $reproducibleTimestamp
Compress-Archive -Path (Join-Path $bundle "*") -DestinationPath $zip -CompressionLevel Optimal

$sumEntries = @(
    Get-Item -LiteralPath (Join-Path $bundle "CodexBridge.Setup.exe"), $zip | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name
    }
)
$sumEntries | Set-Content -LiteralPath (Join-Path $OutputRoot "SHA256SUMS.txt") -Encoding ASCII
Remove-Item -LiteralPath $work -Recurse -Force
Write-Output "BUNDLE=$bundle"
Write-Output "ZIP=$zip"
Write-Output "SHA256SUMS=$(Join-Path $OutputRoot 'SHA256SUMS.txt')"
