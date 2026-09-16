param([string]$DataPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'publication-tools.ps1')
$taskBuild = @(Get-PublicationBuilds (Join-Path $taskRoot 'artifacts/publish') |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.Path 'ControlHost/IntegratedContro.ControlHost.exe') } |
    Sort-Object -Property @{Expression='BuiltAt';Descending=$true}, Name | Select-Object -First 1)
if ($taskBuild.Count -ne 1) { throw '완성된 ControlHost 배포본이 없습니다. scripts/publish.ps1을 실행하세요.' }
$taskExe = Join-Path $taskBuild[0].Path 'ControlHost/IntegratedContro.ControlHost.exe'
if ([string]::IsNullOrWhiteSpace($DataPath)) {
    $DataPath = Read-Host '최초 설정에서 사용한 기존 호스트 데이터 폴더의 전체 경로'
}
if (-not [IO.Path]::IsPathFullyQualified($DataPath)) { throw '기존 데이터 폴더의 절대 경로가 필요합니다.' }
foreach ($taskFile in @('control.sqlite', 'host.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $DataPath $taskFile) -PathType Leaf)) {
        throw "기존 호스트 파일이 없습니다: $taskFile. 최초 설정에 사용한 폴더를 확인하세요."
    }
}
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'ControlHost 배포본이 없습니다.' }
& $taskExe run --data $DataPath
exit $LASTEXITCODE
