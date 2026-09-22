# Use the pinned SDK installed globally, or its verified portable copy in artifacts.
$taskSdkRepo = Split-Path -Parent $PSScriptRoot
$taskSdkVersion = (Get-Content -LiteralPath (Join-Path $taskSdkRepo 'global.json') -Raw | ConvertFrom-Json).sdk.version
$taskPortableSdk = Join-Path $taskSdkRepo ('artifacts/dotnet-sdk-' + $taskSdkVersion)
if (Test-Path -LiteralPath (Join-Path $taskPortableSdk 'dotnet.exe') -PathType Leaf) {
    $env:DOTNET_ROOT = $taskPortableSdk
    $env:DOTNET_ROOT_X64 = $taskPortableSdk
    $env:PATH = $taskPortableSdk + [IO.Path]::PathSeparator + $env:PATH
}
Push-Location -LiteralPath $taskSdkRepo
try {
    $taskSelectedSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $taskSelectedSdk -ne $taskSdkVersion) {
        throw "Install .NET SDK $taskSdkVersion from https://dotnet.microsoft.com/download/dotnet/10.0 (or extract its verified Windows x64 ZIP to $taskPortableSdk)."
    }
}
finally { Pop-Location }
