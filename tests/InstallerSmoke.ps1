param([Parameter(Mandatory)][string]$InstallerPath, [string]$PreviousInstallerPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $taskRoot 'scripts/use-dotnet.ps1')
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
if ($InstallerPath -notlike '*-verification.exe') { throw 'Only an isolated verification package may be installed.' }
if ($PreviousInstallerPath) {
    $PreviousInstallerPath = (Resolve-Path -LiteralPath $PreviousInstallerPath).Path
    if ($PreviousInstallerPath -notlike '*-verification.exe') { throw 'Previous package must also be a verification package.' }
}
$taskRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\IntegratedContro.InstallerVerification_is1'
if (Test-Path -LiteralPath $taskRegistry) { throw 'A previous verification installation exists; inspect it first.' }
$taskTest = Join-Path $taskRoot ('artifacts/installer-smoke/' + [guid]::NewGuid().ToString('N'))
$taskInstall = Join-Path $taskTest '설치 폴더'
$taskProfile = Join-Path $taskTest '앱 프로필'
New-Item -ItemType Directory -Path $taskTest,$taskProfile -Force | Out-Null
function Assert-Check([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Setup([string]$Package=$InstallerPath,[int]$Expected=0,[string[]]$Extra=@()) {
    $log = Join-Path $taskTest ('setup-' + [guid]::NewGuid().ToString('N') + '.log')
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="{0}"' -f $taskInstall),('/LOG="{0}"' -f $log)) + $Extra
    $process = Start-Process -FilePath $Package -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Assert-Check ($process.ExitCode -eq $Expected) "Installer exit $($process.ExitCode), expected $Expected. See $log"
}
function Start-Fixture([string]$Executable,[string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = $taskTest
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}
function Assert-Payload {
    $manifest = Get-Content -LiteralPath (Join-Path $taskInstall 'package-manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $path = Join-Path $taskInstall $file.path
        Assert-Check (Test-Path -LiteralPath $path -PathType Leaf) "Missing installed file $($file.path)"
        Assert-Check ((Get-FileHash -LiteralPath $path).Hash -eq $file.sha256) "Installed file mismatch $($file.path)"
    }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'App/server-startup.json'))) 'Site startup settings leaked into installer'
    Write-Output "PASS: $($manifest.files.Count) installed payload hashes"
}
function Run-Lifecycle([switch]$Media,[switch]$Legacy) {
    $arguments = @('--setup-lifecycle-only','--installed-root',$taskInstall,'--profile-dir',$taskProfile)
    if ($Media) { $arguments += '--include-setup-media' }
    if ($Legacy) { $arguments += '--legacy-password-fixture' }
    $log = Join-Path $taskTest ('lifecycle-' + [guid]::NewGuid().ToString('N') + '.log')
    & dotnet run --project (Join-Path $taskRoot 'tests/IntegratedContro.UiSmoke') --no-build --no-restore -- @arguments *> $log
    Assert-Check ($LASTEXITCODE -eq 0) "Installed setup lifecycle failed. See $log"
    Get-Content -LiteralPath $log
}
function Snapshot-Data {
    @(Get-ChildItem -LiteralPath $taskProfile -Recurse -File | Where-Object { $_.Extension -notin @('.png','.log') } | ForEach-Object {
        @{ path=$_.FullName; hash=(Get-FileHash -LiteralPath $_.FullName).Hash }
    })
}
function Assert-Preserved($Files) {
    foreach ($file in $Files) {
        Assert-Check ((Test-Path -LiteralPath $file.path) -and (Get-FileHash -LiteralPath $file.path).Hash -eq $file.hash) "Data/profile changed: $($file.path)"
    }
}
$taskApp = $null; $taskHost = $null; $taskShutdown = $null
try {
    if ($PreviousInstallerPath) {
        # The old installer wrote startup settings; keep those writes in this isolated profile.
        Run-Setup -Package $PreviousInstallerPath -Extra @(('/PROFILEDIR="{0}"' -f (Join-Path $taskTest 'old-installer-profile')))
        Run-Lifecycle -Legacy
        $taskPreviousVersion = (Get-Content (Join-Path $taskInstall 'package-manifest.json') -Raw | ConvertFrom-Json).version
        $taskPreviousData = Snapshot-Data
        $taskUpgradeHostData = (Get-ChildItem -LiteralPath $taskProfile -Recurse -Filter host.json -File | Select-Object -First 1).DirectoryName
        Run-Setup
        $taskCurrentVersion = (Get-Content (Join-Path $taskInstall 'package-manifest.json') -Raw | ConvertFrom-Json).version
        Assert-Check ([version]$taskCurrentVersion -gt [version]$taskPreviousVersion) 'Upgrade did not advance product version'
        Assert-Preserved $taskPreviousData
        Write-Output "PASS: upgrade $taskPreviousVersion -> $taskCurrentVersion preserves all existing fixture data/settings"
    } else { Run-Setup }
    Assert-Payload
    Assert-Check (Test-Path -LiteralPath $taskRegistry) 'Uninstall registration missing'
    $taskLaunchProfile = Join-Path $taskTest '첫 실행 프로필'
    Assert-Check (-not (Test-Path -LiteralPath $taskLaunchProfile)) 'Installer created user settings'
    $taskApp = Start-Fixture (Join-Path $taskInstall 'App/IntegratedContro.App.exe') @('--profile-dir',$taskLaunchProfile,'--server-startup',(Join-Path $taskTest 'absent.json'))
    $taskDeadline = [datetime]::UtcNow.AddSeconds(25)
    do { Start-Sleep -Milliseconds 200; $taskApp.Refresh() } while (-not $taskApp.HasExited -and $taskApp.MainWindowHandle -eq 0 -and [datetime]::UtcNow -lt $taskDeadline)
    Assert-Check (-not $taskApp.HasExited -and $taskApp.MainWindowHandle -ne 0) 'Installed WPF app did not open'
    Run-Setup -Expected 7
    Assert-Check (-not $taskApp.HasExited) 'Installer terminated the running app'
    $taskUninstaller = Join-Path $taskInstall 'unins000.exe'
    $taskUninstall = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($taskUninstall.ExitCode -ne 0 -and (Test-Path -LiteralPath (Join-Path $taskInstall 'App/IntegratedContro.App.exe'))) 'Uninstall did not protect the running app'
    $taskApp.Kill(); Assert-Check ($taskApp.WaitForExit(15000)) 'Test app did not exit'
    $taskApp.Dispose(); $taskApp = $null
    # Clean installation covers media fault injection. Upgrade focuses on old data reopening and preservation.
    Run-Lifecycle -Media:([string]::IsNullOrEmpty($PreviousInstallerPath))
    $taskHostData = if ($taskUpgradeHostData) { $taskUpgradeHostData } else { (Get-ChildItem -LiteralPath $taskProfile -Recurse -Filter host.json -File | Select-Object -First 1).DirectoryName }
    Assert-Check (-not [string]::IsNullOrEmpty($taskHostData)) 'App setup did not create test host data'
    $taskEventName = 'Local\IntegratedContro.ServerStop.' + [guid]::NewGuid().ToString('N')
    $taskShutdown = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$taskEventName)
    $taskHost = Start-Fixture (Join-Path $taskInstall 'ControlHost/IntegratedContro.ControlHost.exe') @('run','--data',$taskHostData,'--shutdown-event',$taskEventName)
    $taskPort = (Get-Content (Join-Path $taskHostData 'host.json') -Raw | ConvertFrom-Json).port
    $taskReady = $false
    for ($taskAttempt=0; $taskAttempt -lt 60; $taskAttempt++) {
        if ($taskHost.HasExited) { break }
        try { $taskHealth = Invoke-RestMethod -Uri "https://127.0.0.1:$taskPort/health" -SkipCertificateCheck -TimeoutSec 1; $taskReady = $taskHealth.status -eq 'ready'; if ($taskReady) { break } }
        catch { Start-Sleep -Milliseconds 200 }
    }
    Assert-Check $taskReady 'Installed host did not become ready'
    Run-Setup -Expected 7
    Assert-Check (-not $taskHost.HasExited) 'Installer terminated the running host'
    $taskUninstall = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($taskUninstall.ExitCode -ne 0 -and -not $taskHost.HasExited) 'Uninstall did not protect the running host'
    $taskShutdown.Set() | Out-Null
    Assert-Check ($taskHost.WaitForExit(15000) -and $taskHost.ExitCode -eq 0) 'Host failed normal shutdown'
    $taskHost.Dispose(); $taskHost = $null
    $taskDataHashes = Snapshot-Data
    Assert-Check ($taskDataHashes.Count -gt 20) 'Data preservation fixture is incomplete'
    Run-Setup
    Assert-Payload
    Assert-Preserved $taskDataHashes
    $taskMediaVersion = & (Join-Path $taskInstall 'MediaMTX/mediamtx.exe') --version
    Assert-Check ($LASTEXITCODE -eq 0 -and $taskMediaVersion -match '1\.21\.0') 'Packaged MediaMTX failed to execute'
    $taskUninstall = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($taskUninstall.ExitCode -eq 0) 'Test uninstall failed'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'App/IntegratedContro.App.exe'))) 'Product binary remained after uninstall'
    Assert-Check (-not (Test-Path -LiteralPath $taskRegistry)) 'Uninstall registration remained'
    Assert-Preserved $taskDataHashes
    $taskResult = "PASS: installed payload hashes, WPF/host/MediaMTX, app-driven new/existing/remote setup, interruption/retry, active-process protection, reinstall and uninstall; $($taskDataHashes.Count) data/protected/profile/settings files preserved. Physical second PC and Enterprise not tested."
    if (-not $PreviousInstallerPath) { $taskResult += " Installed media setup/password/recovery coverage passed." }
    else { $taskResult += " Previous-version upgrade and new host reopening old data passed; media coverage is in the clean-install run." }
    $taskResult | Set-Content -LiteralPath (Join-Path $taskTest 'result.txt') -Encoding UTF8
    Write-Output $taskResult
    Write-Output "Evidence: $taskTest"
}
finally {
    foreach ($process in @($taskApp,$taskHost)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
            $process.Dispose()
        }
    }
    if ($null -ne $taskShutdown) { $taskShutdown.Dispose() }
}
