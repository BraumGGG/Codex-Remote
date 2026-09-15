[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $projectRoot ".tools"
$goRoot = Join-Path $toolsRoot "go"
$goExe = Join-Path $goRoot "bin\go.exe"
$expectedVersion = "go1.26.6"
$archiveName = "go1.26.6.windows-amd64.zip"
$archiveUrl = "https://golang.google.cn/dl/$archiveName"
$expectedHash = "5b6c5b556525810463b5c897b50dc7a82d6a3dc0bfaf55d990a7e9f31d6b2318"
$downloadDirectory = Join-Path $toolsRoot "downloads"
$archivePath = Join-Path $downloadDirectory $archiveName
$lockPath = Join-Path $toolsRoot "bootstrap-go.lock"

if (Test-Path -LiteralPath $goExe) {
    $installedVersion = (& $goExe version).Split(" ")[2]
    if ($installedVersion -eq $expectedVersion) {
        Write-Output "Go $expectedVersion is ready at $goExe"
        exit 0
    }

    throw "Unexpected Go version at ${goExe}: $installedVersion"
}

New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
$lock = $null
try {
    try {
        $lock = [IO.File]::Open(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another Go bootstrap process is already running."
    }

    if (-not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -UseBasicParsing -Uri $archiveUrl -OutFile $archivePath
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Go archive checksum mismatch. Expected $expectedHash, got $actualHash."
    }

    $extractRoot = Join-Path $toolsRoot ("go-extract-" + [Guid]::NewGuid().ToString("N"))
    try {
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
        $extractedGo = Join-Path $extractRoot "go"
        if (-not (Test-Path -LiteralPath (Join-Path $extractedGo "bin\go.exe"))) {
            throw "Go archive did not contain go\bin\go.exe."
        }

        Move-Item -LiteralPath $extractedGo -Destination $goRoot
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
}

Write-Output "Go $expectedVersion is ready at $goExe"
