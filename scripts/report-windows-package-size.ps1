param(
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$Setup,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$Payload = [IO.Path]::GetFullPath($Payload)
$files = @(Get-ChildItem -LiteralPath $Payload -Recurse -File)
$report = [ordered]@{
    schemaVersion = 1
    baseline = [ordered]@{
        payloadBytes = 211738706
        zipBytes = 120187699
        setupBytes = 35095824
    }
    payloadBytes = [long](($files | Measure-Object Length -Sum).Sum)
    appBytes = [long](Get-Item -LiteralPath (Join-Path $Payload 'CodexBridge.App.exe')).Length
    transportBytes = [long](Get-Item -LiteralPath (Join-Path $Payload 'CodexBridge.Transport.exe')).Length
    setupBytes = [long](Get-Item -LiteralPath $Setup).Length
    largestFiles = @($files | Sort-Object Length -Descending | Select-Object -First 30 | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($Payload.Length + 1).Replace('\', '/'); bytes = [long]$_.Length }
    })
}
$directory = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Output -Encoding UTF8
Write-Output "SIZE_REPORT=$Output"
