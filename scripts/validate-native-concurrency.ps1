param(
    [string]$Dotnet = "$PSScriptRoot\..\.tools\dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
$cli = "$PSScriptRoot\..\src\CodexBridge.Cli\bin\Debug\net8.0-windows\CodexBridge.Cli.dll"
$cases = @(
    @(
        "01a00749-2fb0-7fa0-9186-4f8292732f2c",
        "Reply exactly: native overlap test 1 success. Do not use tools or modify files."
    ),
    @(
        "01a00749-6d7c-7072-9b22-f4a70ea35331",
        "Reply exactly: native overlap test 2 success. Do not use tools or modify files."
    )
)

foreach ($case in $cases) {
    & $Dotnet $cli send --thread $case[0] --message $case[1]
    if ($LASTEXITCODE -ne 0) {
        throw "Validation send failed: $($case[0])"
    }
}
