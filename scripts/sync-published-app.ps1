param(
    [Parameter(Mandatory=$true)][string]$BuildPath,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPublishRoot = Join-Path $taskRoot 'artifacts/publish'
$taskWorkspace = Split-Path -Parent $taskRoot
. (Join-Path $PSScriptRoot 'publication-tools.ps1')
$taskBuild = Assert-PublicationPath $BuildPath $taskPublishRoot
if ($Preview) {
    [pscustomobject]@{
        appSource=(Join-Path $taskBuild 'App')
        shortcut=(Join-Path $taskWorkspace 'IntegratedContro.lnk')
        cleanup=(Get-PublicationCleanupPlan $taskPublishRoot)
    }
    return
}
$taskLatest = @(Get-PublicationBuilds $taskPublishRoot | Where-Object { Test-Path -LiteralPath (Join-Path $_.Path 'App/IntegratedContro.App.exe') } | Sort-Object -Property @{Expression='BuiltAt';Descending=$true}, Name | Select-Object -First 1)
if ($taskLatest.Count -ne 1 -or $taskLatest[0].Path -ne $taskBuild) { throw 'Choose the newest completed app build; the shortcut must not target a pruned build.' }
$taskShortcut = Set-PublicationShortcut $taskBuild $taskPublishRoot $taskWorkspace
$taskCleanup = Remove-OldPublications $taskPublishRoot
$taskResult = [pscustomobject]@{ workspace=$taskShortcut; history=$taskCleanup }
[pscustomobject]@{
    workspace=$taskShortcut
    history=[pscustomobject]@{ retained=$taskCleanup.retained; skipped=$taskCleanup.skipped }
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPublishRoot 'shortcut-result.json') -Encoding UTF8
foreach ($taskSkipped in $taskCleanup.skipped) {
    Write-Warning "이전 빌드 정리 보류: $($taskSkipped.path) — $($taskSkipped.reason)"
}
$taskResult
