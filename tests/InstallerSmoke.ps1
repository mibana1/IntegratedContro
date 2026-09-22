param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
if ($InstallerPath -notlike '*-verification.exe') { throw 'Only the isolated verification package may be installed by this test.' }
$taskRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\IntegratedContro.InstallerVerification_is1'
if (Test-Path -LiteralPath $taskRegistry) { throw 'A previous verification installation exists; inspect it first.' }
$taskTest = Join-Path $taskRoot ('artifacts/installer-smoke/' + [guid]::NewGuid().ToString('N'))
$taskInstall = Join-Path $taskTest '설치 폴더'
$taskData = Join-Path $taskTest '서버 데이터'
$taskProfile = Join-Path $taskTest '앱 프로필'
New-Item -ItemType Directory -Path $taskTest,$taskProfile -Force | Out-Null
function Assert-Check([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Setup([string[]]$Extra=@(),[int]$Expected=0) {
    $log = Join-Path $taskTest ('setup-' + [guid]::NewGuid().ToString('N') + '.log')
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="{0}"' -f $taskInstall),('/PROFILEDIR="{0}"' -f $taskProfile),('/LOG="{0}"' -f $log)) + $Extra
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Assert-Check ($process.ExitCode -eq $Expected) "Installer exit $($process.ExitCode), expected $Expected. See $log"
}
function Start-Fixture([string]$Executable,[string[]]$Arguments,[bool]$Input=$false) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardInput = $Input
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
    Write-Output "PASS: $($manifest.files.Count) installed payload hashes"
}
$taskApp = $null
$taskHost = $null
$taskShutdown = $null
try {
    Run-Setup
    Assert-Payload
    Assert-Check (Test-Path -LiteralPath $taskRegistry) 'Uninstall registration missing'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'App/server-startup.json'))) 'Site startup settings leaked into installer'
    $taskApp = Start-Fixture (Join-Path $taskInstall 'App/IntegratedContro.App.exe') @('--profile-dir',$taskProfile,'--server-startup',(Join-Path $taskTest 'absent.json'))
    $taskDeadline = [datetime]::UtcNow.AddSeconds(25)
    do { Start-Sleep -Milliseconds 200; $taskApp.Refresh() } while (-not $taskApp.HasExited -and $taskApp.MainWindowHandle -eq 0 -and [datetime]::UtcNow -lt $taskDeadline)
    Assert-Check (-not $taskApp.HasExited -and $taskApp.MainWindowHandle -ne 0) 'Installed WPF app did not open its login window'
    Run-Setup -Expected 7
    Assert-Check (-not $taskApp.HasExited) 'Installer terminated the running app'
    Assert-Payload
    $taskUninstaller = Join-Path $taskInstall 'unins000.exe'
    $taskUninstall = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($taskUninstall.ExitCode -ne 0 -and (Test-Path -LiteralPath (Join-Path $taskInstall 'App/IntegratedContro.App.exe'))) 'Uninstall did not protect the running app'
    # Only this isolated test process is stopped; the login dialog owns the active window.
    $taskApp.Kill()
    Assert-Check ($taskApp.WaitForExit(15000)) 'Test app did not exit'
    $taskApp.Dispose(); $taskApp = $null
    Write-Output 'PASS: installed WPF launch, running app update/uninstall protection'
    $taskProfileHash = (Get-FileHash -LiteralPath (Join-Path $taskProfile 'client.json')).Hash
    $taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $taskListener.Start(); $taskPort = $taskListener.LocalEndpoint.Port; $taskListener.Stop()
    $taskHostExe = Join-Path $taskInstall 'ControlHost/IntegratedContro.ControlHost.exe'
    $taskInit = Start-Fixture $taskHostExe @('setup','--data',$taskData,'--site','Installer verification','--admin','installer-test','--port',"$taskPort") $true
    $taskPassword = [guid]::NewGuid().ToString('N')
    $taskInit.StandardInput.WriteLine($taskPassword)
    $taskInit.StandardInput.WriteLine($taskPassword)
    $taskInit.StandardInput.Close()
    Assert-Check ($taskInit.WaitForExit(20000) -and $taskInit.ExitCode -eq 0) 'Installed host setup failed'
    $taskInit.Dispose()
    $taskPassword = $null
    $taskEventName = 'Local\IntegratedContro.ServerStop.' + [guid]::NewGuid().ToString('N')
    $taskShutdown = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$taskEventName)
    $taskHost = Start-Fixture $taskHostExe @('run','--data',$taskData,'--shutdown-event',$taskEventName)
    $taskReady = $false
    for ($taskAttempt=0; $taskAttempt -lt 60; $taskAttempt++) {
        if ($taskHost.HasExited) { break }
        try {
            $taskHealth = Invoke-RestMethod -Uri "https://127.0.0.1:$taskPort/health" -SkipCertificateCheck -TimeoutSec 1
            $taskReady = $taskHealth.status -eq 'ready'
            if ($taskReady) { break }
        } catch { Start-Sleep -Milliseconds 200 }
    }
    Assert-Check $taskReady 'Installed self-contained host did not become ready'
    Run-Setup -Expected 7
    Assert-Check (-not $taskHost.HasExited) 'Installer terminated the running host'
    $taskShutdown.Set() | Out-Null
    Assert-Check ($taskHost.WaitForExit(15000) -and $taskHost.ExitCode -eq 0) 'Installed host failed normal shutdown'
    $taskHost.Dispose(); $taskHost = $null
    Write-Output 'PASS: installed host initial setup/HTTPS/normal shutdown and running host update protection'
    $taskMediaConfig = Join-Path $taskData 'mediamtx.yml'
    Copy-Item -LiteralPath (Join-Path $taskInstall 'Examples/mediamtx.example.yml') -Destination $taskMediaConfig
    $taskDataHashes = @(Get-ChildItem -LiteralPath $taskData -Recurse -File | ForEach-Object { @{path=$_.FullName;hash=(Get-FileHash -LiteralPath $_.FullName).Hash} })
    Run-Setup -Extra @('/CONNECTLOCAL=1',('/HOSTDATA="{0}"' -f $taskData),('/MEDIACONFIG="{0}"' -f $taskMediaConfig),'/MEDIAPORT=19997')
    $taskStartupPath = Join-Path $taskProfile 'server-startup.json'
    $taskStartup = Get-Content -LiteralPath $taskStartupPath -Raw | ConvertFrom-Json
    Assert-Check ($taskStartup.hostDataPath -eq $taskData) 'Korean/space data path did not round trip'
    Assert-Check ($taskStartup.mediaMtxExecutablePath -eq '../MediaMTX/mediamtx.exe') 'Media binary path incorrect'
    Assert-Check ($taskStartup.mediaMtxConfigurationPath -eq $taskMediaConfig -and $taskStartup.mediaMtxApiEndpoint -eq 'http://127.0.0.1:19997') 'Media configuration incorrect'
    $taskSettingsHash = (Get-FileHash -LiteralPath $taskStartupPath).Hash
    Run-Setup
    Assert-Payload
    Assert-Check ((Get-FileHash -LiteralPath $taskStartupPath).Hash -eq $taskSettingsHash) 'Upgrade replaced startup settings'
    Run-Setup -Extra @('/CONNECTLOCAL=1',('/HOSTDATA="{0}"' -f $taskInstall),('/MEDIACONFIG="{0}"' -f $taskMediaConfig)) -Expected 7
    Assert-Check ((Get-FileHash -LiteralPath $taskStartupPath).Hash -eq $taskSettingsHash) 'Rejected configuration changed settings'
    $taskMediaVersion = & (Join-Path $taskInstall 'MediaMTX/mediamtx.exe') --version
    Assert-Check ($LASTEXITCODE -eq 0 -and $taskMediaVersion -match '1\.21\.0') 'Packaged MediaMTX failed to execute'
    $taskUninstall = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($taskUninstall.ExitCode -eq 0) 'Test uninstall failed'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $taskInstall 'App/IntegratedContro.App.exe'))) 'Product binary remained after uninstall'
    Assert-Check (-not (Test-Path -LiteralPath $taskRegistry)) 'Uninstall registration remained'
    Assert-Check ((Get-FileHash -LiteralPath $taskStartupPath).Hash -eq $taskSettingsHash) 'Uninstall removed startup settings'
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $taskProfile 'client.json')).Hash -eq $taskProfileHash) 'Profile was changed'
    foreach ($file in $taskDataHashes) { Assert-Check ((Get-FileHash -LiteralPath $file.path).Hash -eq $file.hash) 'Operational data changed during installation/uninstallation' }
    $taskResult = 'PASS: clean install, all payload hashes, WPF/host/MediaMTX execution, active-process protection, local data connection, upgrade, invalid config rejection, uninstall with DB/protected files/profile/settings preservation.'
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

