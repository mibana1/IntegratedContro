$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskStaging = Join-Path $taskRoot "artifacts/publish/Build-$taskStamp"
$taskCurrent = Join-Path $taskRoot 'artifacts/publish/Current'
Push-Location -LiteralPath $taskRoot
try {
    foreach ($taskName in @('ControlHost', 'App')) {
        $taskProject = "src/IntegratedContro.$taskName/IntegratedContro.$taskName.csproj"
        dotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o (Join-Path $taskStaging $taskName)
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $taskName. Completed files remain at $taskStaging" }
    }
    $taskApplied = $false
    try {
        $taskPrefix = [IO.Path]::GetFullPath($taskCurrent) + [IO.Path]::DirectorySeparatorChar
        $taskRunning = @(Get-Process | Where-Object {
            try { $_.Path -and $_.Path.StartsWith($taskPrefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        })
        if ($taskRunning.Count -gt 0) { throw 'Current 실행 파일이 사용 중입니다. 프로세스를 종료하지 않았습니다.' }
        # Check all existing destination files before copying either product.
        foreach ($taskFile in Get-ChildItem -LiteralPath $taskStaging -Recurse -File) {
            $taskRelative = [IO.Path]::GetRelativePath($taskStaging, $taskFile.FullName)
            $taskDestination = Join-Path $taskCurrent $taskRelative
            if (Test-Path -LiteralPath $taskDestination) {
                $taskProbe = [IO.File]::Open($taskDestination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $taskProbe.Dispose()
            }
        }
        foreach ($taskFile in Get-ChildItem -LiteralPath $taskStaging -Recurse -File) {
            $taskRelative = [IO.Path]::GetRelativePath($taskStaging, $taskFile.FullName)
            $taskDestination = Join-Path $taskCurrent $taskRelative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskDestination)) | Out-Null
            Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination -Force
        }
        $taskApplied = $true
    }
    catch {
        Write-Warning "Current 반영을 완료하지 못했습니다: $($_.Exception.Message)"
        Write-Warning "완성된 두 배포본을 보존했습니다: $taskStaging. 작업을 확인하고 기존 앱/호스트를 정상 종료한 뒤 두 폴더를 함께 교체하세요."
    }
    [ordered]@{ staging = $taskStaging; current = $taskCurrent; currentApplied = $taskApplied; builtAt = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts/publish/publish-result.json') -Encoding UTF8
    Write-Output "검증용 배포본: $taskStaging"
    Write-Output "Current 반영 완료: $taskApplied"
}
finally { Pop-Location }
