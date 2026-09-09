param([switch]$SkipUiSmoke)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    dotnet restore IntegratedContro.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    dotnet build IntegratedContro.sln --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test tests/IntegratedContro.Tests/IntegratedContro.Tests.csproj --no-build --no-restore --logger 'trx;LogFileName=first-implementation.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    if (-not $SkipUiSmoke) {
        dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --profile-dir (Join-Path $taskRoot 'artifacts/ui-smoke/profile')
        if ($LASTEXITCODE -ne 0) { throw 'WPF smoke failed' }
    }
}
finally { Pop-Location }
