$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    foreach ($taskName in @('ControlHost', 'App')) {
        $taskProject = "src/IntegratedContro.$taskName/IntegratedContro.$taskName.csproj"
        dotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o "artifacts/publish/$taskName"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $taskName" }
    }
}
finally { Pop-Location }
