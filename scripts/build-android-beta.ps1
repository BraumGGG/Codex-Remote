[CmdletBinding()]
param(
    [switch]$SkipBootstrap,
    [string]$Version = "0.1.0-beta.13",
    [int]$VersionCode = 13
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $SkipBootstrap) { & (Join-Path $PSScriptRoot "bootstrap-android.ps1") | Out-Null }
$env:JAVA_HOME = Join-Path $root ".tools\android-jdk17"
$env:ANDROID_HOME = Join-Path $root ".tools\android-sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:GRADLE_USER_HOME = Join-Path $root ".tools\gradle-home"
$gradle = Join-Path $root ".tools\gradle-8.9\bin\gradle.bat"
$secrets = Join-Path $root ".secrets"
$passwordFile = Join-Path $secrets "android-signing-password.dpapi"
$keyStore = Join-Path $secrets "codex-bridge-beta.jks"
New-Item -ItemType Directory -Force -Path $secrets | Out-Null

if (-not (Test-Path -LiteralPath $passwordFile)) {
    $bytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) }
    finally { $random.Dispose() }
    $plain = [Convert]::ToBase64String($bytes)
    ConvertTo-SecureString $plain -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile -Encoding ascii
}
$encryptedPassword = (Get-Content -Raw -LiteralPath $passwordFile).Trim()
$secure = ConvertTo-SecureString -String $encryptedPassword
$credential = New-Object Management.Automation.PSCredential("ignored", $secure)
$password = $credential.GetNetworkCredential().Password
$keytool = Join-Path $env:JAVA_HOME "bin\keytool.exe"
if (-not (Test-Path -LiteralPath $keyStore)) {
    & $keytool -genkeypair -keystore $keyStore -storepass $password -keypass $password `
        -alias codex-bridge-beta -keyalg EC -groupname secp256r1 -sigalg SHA256withECDSA `
        -validity 3650 -dname "CN=Codex Bridge Beta,OU=Engineering,O=Codex Bridge,L=China,C=CN"
    if ($LASTEXITCODE -ne 0) { throw "Android 签名密钥生成失败" }
}

$env:CODEX_BRIDGE_ANDROID_KEYSTORE = $keyStore
$env:CODEX_BRIDGE_ANDROID_STORE_PASSWORD = $password
$env:CODEX_BRIDGE_ANDROID_KEY_PASSWORD = $password
$env:CODEX_BRIDGE_ANDROID_KEY_ALIAS = "codex-bridge-beta"
try {
    Push-Location (Join-Path $root "android")
    try { & $gradle --no-daemon clean testReleaseUnitTest lintRelease assembleRelease }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "Android 构建失败，退出码 $LASTEXITCODE" }
} finally {
    Remove-Item Env:CODEX_BRIDGE_ANDROID_STORE_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:CODEX_BRIDGE_ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
    $password = $null
}

$output = Join-Path $root "artifacts\android"
New-Item -ItemType Directory -Force -Path $output | Out-Null
$apk = Join-Path $root "android\app\build\outputs\apk\release\app-release.apk"
$target = Join-Path $output "CodexBridge-$Version.apk"
Copy-Item -LiteralPath $apk -Destination $target -Force
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    versionCode = $VersionCode
    versionName = $Version
    minSdk = 29
    sha256 = $hash
    size = (Get-Item -LiteralPath $target).Length
    downloadUrl = "https://remote.example.invalid:8443/android/CodexBridge-$Version.apk"
    mandatory = $false
}
$manifestJson = $manifest | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $output "update.json"), $manifestJson, (New-Object Text.UTF8Encoding($false)))
$hash | Set-Content -LiteralPath (Join-Path $output "CodexBridge-$Version.apk.sha256") -Encoding ascii
[pscustomobject]@{ Apk = $target; Sha256 = $hash; Size = $manifest.size }
