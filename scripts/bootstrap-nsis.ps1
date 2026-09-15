param([string]$InstallDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $root '.tools'
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $toolsRoot 'nsis-3.11'
}
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$toolsPrefix = [IO.Path]::GetFullPath($toolsRoot).TrimEnd('\') + '\'
if (-not $InstallDirectory.StartsWith($toolsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'NSIS install directory must be inside project .tools.'
}

$makensis = Join-Path $InstallDirectory 'makensis.exe'
if (Test-Path -LiteralPath $makensis -PathType Leaf) {
    $version = (& $makensis /VERSION).Trim()
    if ($version -ne 'v3.11') { throw "Unexpected NSIS version: $version" }
    Write-Output "NSIS=$makensis"
    return
}

$downloadDir = Join-Path $toolsRoot 'downloads'
New-Item -ItemType Directory -Force $downloadDir | Out-Null
$archive = Join-Path $downloadDir 'nsis-3.11.zip'
$url = 'https://sourceforge.net/projects/nsis/files/NSIS%203/3.11/nsis-3.11.zip/download'
$expectedSha256 = 'C7D27F780DDB6CFFB4730138CD1591E841F4B7EDB155856901CDF5F214394FA1'
$expectedSha1 = 'EF7FF767E5CBD9EDD22ADD3A32C9B8F4500BB10D'

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    & curl.exe -fsSL --retry 5 --retry-delay 2 --retry-all-errors --max-time 240 -o $archive $url
    if ($LASTEXITCODE -ne 0) { throw "NSIS download failed: $LASTEXITCODE" }
}
$actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$actualSha1 = (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash
if ($actualSha256 -ne $expectedSha256) { throw "NSIS SHA-256 mismatch: $actualSha256" }
if ($actualSha1 -ne $expectedSha1) { throw "NSIS SHA-1 mismatch: $actualSha1" }

if (Test-Path -LiteralPath $InstallDirectory) {
    Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
}
Expand-Archive -LiteralPath $archive -DestinationPath $toolsRoot -Force
if (-not (Test-Path -LiteralPath $makensis -PathType Leaf)) { throw 'makensis.exe was not extracted.' }
$version = (& $makensis /VERSION).Trim()
if ($version -ne 'v3.11') { throw "Unexpected NSIS version: $version" }
Write-Output "NSIS=$makensis"
