[CmdletBinding()]
param([string]$Apk = "")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Apk) { $Apk = Join-Path $root "artifacts\android\CodexBridge-0.1.0-beta.4.apk" }
$sdk = Join-Path $root ".tools\android-sdk"
$aapt = Join-Path $sdk "build-tools\35.0.0\aapt.exe"
$signer = Join-Path $sdk "build-tools\35.0.0\apksigner.bat"
if (-not (Test-Path -LiteralPath $Apk)) { throw "APK does not exist: $Apk" }

& $signer verify --verbose --print-certs $Apk
if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed." }
$badging = (& $aapt dump badging $Apk | Out-String)
$permissions = (& $aapt dump permissions $Apk | Out-String)
if ($badging -notmatch "package: name='online\.braumg\.codexbridge'") { throw "applicationId mismatch." }
if ($badging -notmatch "sdkVersion:'29'" -or $badging -notmatch "targetSdkVersion:'35'") { throw "SDK range mismatch." }
$packageMatch = [regex]::Match($badging, "package: name='online\.braumg\.codexbridge' versionCode='(?<code>\d+)' versionName='(?<name>[^']+)'")
if (-not $packageMatch.Success) { throw "Unable to read APK version metadata." }
$versionCode = [int]$packageMatch.Groups["code"].Value
$versionName = $packageMatch.Groups["name"].Value
foreach ($required in @("android.permission.INTERNET", "android.permission.CAMERA")) {
    if ($permissions -notmatch [regex]::Escape($required)) { throw "Missing permission: $required" }
}
foreach ($forbidden in @("READ_EXTERNAL_STORAGE", "WRITE_EXTERNAL_STORAGE", "MANAGE_EXTERNAL_STORAGE", "QUERY_ALL_PACKAGES", "REQUEST_INSTALL_PACKAGES")) {
    if ($permissions -match $forbidden) { throw "Forbidden permission: $forbidden" }
}
$hash = (Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Get-Content -Raw -LiteralPath (Join-Path (Split-Path $Apk -Parent) "update.json") | ConvertFrom-Json
if ($manifest.sha256 -ne $hash -or $manifest.size -ne (Get-Item -LiteralPath $Apk).Length) { throw "Update manifest does not match APK." }
if ([int]$manifest.versionCode -ne $versionCode -or [string]$manifest.versionName -ne $versionName) { throw "Update manifest version does not match APK." }
[pscustomobject]@{ Package = "online.braumg.codexbridge"; VersionCode = $versionCode; Version = $versionName; Sha256 = $hash; Permissions = @("INTERNET", "CAMERA") }
