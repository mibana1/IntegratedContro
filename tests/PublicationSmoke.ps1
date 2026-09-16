$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $taskRoot 'scripts/publication-tools.ps1')
$taskTestParent = Join-Path $taskRoot 'artifacts/publication-tests'
$taskTest = Join-Path $taskTestParent ([guid]::NewGuid().ToString('N'))
$taskPublish = Join-Path $taskTest 'publish'
$taskWorkspace = Join-Path $taskTest 'workspace'
New-Item -ItemType Directory -Path $taskPublish, $taskWorkspace -Force | Out-Null
function Assert-Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function New-TestBuild([string]$name) {
    $build = Join-Path $taskPublish $name
    foreach ($relative in @('IntegratedContro.App.exe','IntegratedContro.App.dll','IntegratedContro.App.deps.json',
        'IntegratedContro.App.runtimeconfig.json','coreclr.dll','Microsoft.Data.Sqlite.dll','libvlc/win-x64/libvlc.dll','THIRD_PARTY_NOTICES.md')) {
        $file = Join-Path $build ('App/' + $relative)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        [IO.File]::WriteAllText($file, 'fixture only; never execute')
    }
    return $build
}
try {
    $builds = @(1..6 | ForEach-Object { New-TestBuild ('Build-2026090' + $_ + '-120000') })
    $current = New-TestBuild 'Current'
    (Get-Item -LiteralPath $current).LastWriteTime = [datetime]'2026-08-31'
    $unknown = Join-Path $taskPublish 'user-files'
    New-Item -ItemType Directory -Path $unknown | Out-Null
    $data = Join-Path $builds[1] 'control.sqlite'
    [IO.File]::WriteAllText($data, 'protected data fixture')
    $script:taskFakeRunning = @((Join-Path $builds[0] 'App/IntegratedContro.App.exe'))
    function Get-PublicationRunningPaths { $script:taskFakeRunning }

    $plan = Get-PublicationCleanupPlan $taskPublish
    Assert-Check ($plan.Retained.Count -eq 3 -and $plan.Retained[0] -eq $builds[5]) 'Newest three not selected'
    $result = Remove-OldPublications $taskPublish
    Assert-Check ($result.deleted.Count -eq 2 -and $result.skipped.Count -eq 2) 'Unsafe cleanup selection'
    Assert-Check ((Test-Path -LiteralPath $builds[0]) -and (Test-Path -LiteralPath $data)) 'Running build or data was deleted'
    Assert-Check (-not (Test-Path -LiteralPath $builds[2])) 'Old eligible build was retained'
    Assert-Check (Test-Path -LiteralPath $unknown) 'Unknown user folder was deleted'

    $link = Set-PublicationShortcut $builds[4] $taskPublish $taskWorkspace
    $link = Set-PublicationShortcut $builds[5] $taskPublish $taskWorkspace
    Assert-Check ($link.executable -eq (Join-Path $builds[5] 'App/IntegratedContro.App.exe')) 'Shortcut target was not updated'
    Assert-Check (@(Get-ChildItem -LiteralPath $taskWorkspace).Count -eq 1) 'Shortcut copied payload into workspace'

    $escaped = $false
    try { $null = Assert-PublicationPath $taskWorkspace $taskPublish } catch { $escaped = $true }
    Assert-Check $escaped 'Path boundary was not enforced'

    $script:taskFakeRunning = @()
    Remove-Item -LiteralPath $data
    $result = Remove-OldPublications $taskPublish
    Assert-Check ($result.deleted.Count -eq 2 -and $result.skipped.Count -eq 0) 'Deferred cleanup did not finish'
    Assert-Check (@(Get-PublicationBuilds $taskPublish).Count -eq 3) 'More than three generated builds remain'
    $message = 'PASS: latest-three retention; Current/legacy cleanup; running-build and operational-data protection; path boundary; unknown folder preservation; shortcut creation/update without copied payload; deferred cleanup.'
    $message | Set-Content -LiteralPath (Join-Path $taskTestParent 'result.txt') -Encoding UTF8
    Write-Output $message
}
finally {
    $taskChecked = Assert-PublicationPath $taskTest $taskTestParent
    # Fixture files only; any remaining protected-data sentinel is removed explicitly before tree cleanup.
    $taskSentinel = Join-Path $taskChecked 'publish/Build-20260902-120000/control.sqlite'
    if (Test-Path -LiteralPath $taskSentinel) { Remove-Item -LiteralPath $taskSentinel }
    Assert-PublicationTree $taskChecked
    Remove-Item -LiteralPath $taskChecked -Recurse -Force
}
