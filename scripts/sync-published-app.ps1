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
$taskGuide = Assert-PublicationPath (Join-Path $taskPublishRoot 'latest-run.txt') $taskPublishRoot
$taskGuideLinkPath = Assert-PublicationPath (Join-Path $taskWorkspace '최신 실행 경로.lnk') $taskWorkspace
foreach ($taskPath in @($taskGuide, $taskGuideLinkPath)) {
    if ((Test-Path -LiteralPath $taskPath) -and ((Get-Item -LiteralPath $taskPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Launch guide reparse point is not allowed.'
    }
}
[IO.File]::WriteAllLines($taskGuide, @(
    'IntegratedContro 최신 실행 경로'
    ''
    '앱 실행 바로가기'
    $taskShortcut.shortcut
    ''
    '현재 실행 파일'
    $taskShortcut.executable
    ''
    '실행 파일 폴더'
    (Split-Path -Parent $taskShortcut.executable)
    ''
    '이 문서는 배포할 때마다 현재 실행 경로로 갱신됩니다.'
), [Text.UTF8Encoding]::new($true))
$taskGuideShell = New-Object -ComObject WScript.Shell
try {
    $taskGuideLink = $taskGuideShell.CreateShortcut($taskGuideLinkPath)
    try {
        $taskGuideLink.TargetPath = $taskGuide
        $taskGuideLink.WorkingDirectory = $taskPublishRoot
        $taskGuideLink.Description = 'IntegratedContro 최신 실행 경로 문서'
        $taskGuideLink.Save()
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskGuideLink) }
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskGuideShell) }
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
