param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $taskRoot 'scripts/use-dotnet.ps1')
$taskPublishRoot = Join-Path $taskRoot 'artifacts/publish'
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$taskStaging = Join-Path $taskPublishRoot "Build-$taskStamp"
$taskProducts = @('ControlHost', 'App')
Push-Location -LiteralPath $taskRoot
try {
    New-Item -ItemType Directory -Path $taskStaging -ErrorAction Stop | Out-Null
    $taskMarker = Join-Path $taskStaging '.publish-in-progress'
    'Build in progress; do not launch or select as latest.' | Set-Content -LiteralPath $taskMarker -Encoding UTF8
    foreach ($taskName in $taskProducts) {
        $taskProject = "src/IntegratedContro.$taskName/IntegratedContro.$taskName.csproj"
        dotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $taskStaging $taskName)
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $taskName. Incomplete files remain at $taskStaging" }
    }
    # Keep machine-specific startup paths across new App builds; never copy credentials or the DB.
    $taskStartupSettings = Join-Path $taskRoot 'config/server-startup.local.json'
    if (Test-Path -LiteralPath $taskStartupSettings -PathType Leaf) {
        Get-Content -LiteralPath $taskStartupSettings -Raw | ConvertFrom-Json -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $taskStartupSettings -Destination (Join-Path $taskStaging 'App/server-startup.json')
    }
    & (Join-Path $PSScriptRoot 'stage-mediamtx.ps1') -DestinationPath (Join-Path $taskStaging 'MediaMTX')
    Remove-Item -LiteralPath $taskMarker
    $taskSync = & (Join-Path $PSScriptRoot 'sync-published-app.ps1') -BuildPath $taskStaging
    [ordered]@{
        staging=$taskStaging
        shortcut=$taskSync.workspace.shortcut
        shortcutTarget=$taskSync.workspace.executable
        retainedBuilds=$taskSync.history.retained
        skippedBuilds=$taskSync.history.skipped
        builtAt=(Get-Date).ToString('o')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPublishRoot 'publish-result.json') -Encoding UTF8
    Write-Output "새 배포본: $taskStaging"
    Write-Output "최신 앱 바로가기: $($taskSync.workspace.shortcut)"
    Write-Output "최신 빌드 3개 보관 · 이번 정리 $($taskSync.history.deleted.Count)개 · 실행/잠금 등으로 보류 $($taskSync.history.skipped.Count)개"
}
finally { Pop-Location }
if ($Installer) { & (Join-Path $PSScriptRoot 'build-installer.ps1') -BuildPath $taskStaging }
