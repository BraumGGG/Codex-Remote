[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root ".tools"
$downloads = Join-Path $tools "downloads"
$jdk = Join-Path $tools "android-jdk17"
$gradle = Join-Path $tools "gradle-8.9"
$sdk = Join-Path $tools "android-sdk"

$archives = @(
    @{ Name = "jdk17.zip"; Hash = "53F0C9EC64811A9AB968747076653E5500115DB7230D244E4EC53577CA5EC8FC" },
    @{ Name = "gradle-8.9-bin.zip"; Hash = "D725D707BFABD4DFDC958C624003B3C80ACCC03F7037B5122C4B1D0EF15CECAB" },
    @{ Name = "android-commandlinetools-11076708.zip"; Hash = "4D6931209EEBB1BFB7C7E8B240A6A3CB3AB24479EA294F3539429574B1EEC862" }
)
foreach ($archive in $archives) {
    $path = Join-Path $downloads $archive.Name
    if (-not (Test-Path -LiteralPath $path)) { throw "缺少工具链归档：$($archive.Name)" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $archive.Hash) { throw "工具链哈希不匹配：$($archive.Name)" }
}

if (-not (Test-Path -LiteralPath (Join-Path $jdk "bin\java.exe"))) {
    $staging = Join-Path $tools "android-jdk17.staging"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    Expand-Archive -LiteralPath (Join-Path $downloads "jdk17.zip") -DestinationPath $staging
    $source = Get-ChildItem -LiteralPath $staging -Directory | Select-Object -First 1
    Move-Item -LiteralPath $source.FullName -Destination $jdk
    Remove-Item -LiteralPath $staging -Recurse -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $gradle "bin\gradle.bat"))) {
    Expand-Archive -LiteralPath (Join-Path $downloads "gradle-8.9-bin.zip") -DestinationPath $tools
}
if (-not (Test-Path -LiteralPath (Join-Path $sdk "cmdline-tools\latest\bin\sdkmanager.bat"))) {
    $staging = Join-Path $tools "android-commandline.staging"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    Expand-Archive -LiteralPath (Join-Path $downloads "android-commandlinetools-11076708.zip") -DestinationPath $staging
    $latest = Join-Path $sdk "cmdline-tools\latest"
    New-Item -ItemType Directory -Force -Path (Split-Path $latest -Parent) | Out-Null
    Move-Item -LiteralPath (Join-Path $staging "cmdline-tools") -Destination $latest
    Remove-Item -LiteralPath $staging -Recurse -Force
}

$env:JAVA_HOME = $jdk
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk
$sdkManager = Join-Path $sdk "cmdline-tools\latest\bin\sdkmanager.bat"
$licenseDirectory = Join-Path $sdk "licenses"
New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
@("24333f8a63b6825ea9c5514f83c2829b004d1fee", "d56f5187479451eabf01fb78af6dfcb131a6481e") |
    Set-Content -LiteralPath (Join-Path $licenseDirectory "android-sdk-license") -Encoding ascii
"84831b9409646a918e30573bab4c9c91346d8abd5" |
    Set-Content -LiteralPath (Join-Path $licenseDirectory "android-sdk-preview-license") -Encoding ascii
& $sdkManager --sdk_root=$sdk "platform-tools" "platforms;android-35" "build-tools;35.0.0"
if ($LASTEXITCODE -ne 0) { throw "Android SDK 安装失败，退出码 $LASTEXITCODE" }
foreach ($required in @(
    (Join-Path $sdk "platform-tools\adb.exe"),
    (Join-Path $sdk "platforms\android-35\android.jar"),
    (Join-Path $sdk "build-tools\35.0.0\aapt.exe")
)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Android SDK 组件未真实安装：$required" }
}

[pscustomobject]@{ JavaHome = $jdk; Gradle = $gradle; AndroidSdk = $sdk }
