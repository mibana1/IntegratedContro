param([string]$BuildPath, [string]$CompilerPath, [string]$OutputDirectory, [switch]$VerificationPackage)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildPath) {
    $BuildPath = (Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts/publish/publish-result.json') -Raw | ConvertFrom-Json).staging
}
$BuildPath = (Resolve-Path -LiteralPath $BuildPath).Path
$taskBuildName = Split-Path -Leaf $BuildPath
if ($taskBuildName -notmatch '^Build-(\d{4})(\d{2})(\d{2})-(\d{4})\d{2}-\d{3}$') { throw 'Expected a completed timestamped Build directory.' }
$taskVersion = '{0}.{1}.{2}.{3}' -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$Matches[4]
if (Test-Path -LiteralPath (Join-Path $BuildPath '.publish-in-progress')) { throw 'Cannot package an incomplete build.' }
foreach ($taskFile in @('App/IntegratedContro.App.exe','ControlHost/IntegratedContro.ControlHost.exe','App/coreclr.dll','ControlHost/coreclr.dll','App/libvlc/win-x64/libvlc.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $BuildPath $taskFile) -PathType Leaf)) { throw "Missing runtime file: $taskFile" }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'artifacts/installer' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskTools = Join-Path $taskRoot 'artifacts/installer-tools'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
# Official signed compiler, prepared portably without registry/file associations.
if (-not $CompilerPath) {
    $taskCompilerDir = Join-Path $taskTools 'inno-7.1.0'
    $CompilerPath = Join-Path $taskCompilerDir 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath)) {
        $taskDownload = Join-Path $taskTools 'innosetup-7.1.0-x64.exe'
        if (-not (Test-Path -LiteralPath $taskDownload)) {
            Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $taskDownload
        }
        if ((Get-FileHash -LiteralPath $taskDownload).Hash -ne '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F' -or
            (Get-AuthenticodeSignature -LiteralPath $taskDownload).Status -ne 'Valid') { throw 'Compiler signature/checksum mismatch.' }
        $taskToolArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/PORTABLE=1','/NOICONS','/TASKS=',('/DIR="{0}"' -f $taskCompilerDir))
        $taskToolProcess = Start-Process -FilePath $taskDownload -ArgumentList $taskToolArgs -WindowStyle Hidden -Wait -PassThru
        if ($taskToolProcess.ExitCode -ne 0) { throw "Compiler preparation failed: $($taskToolProcess.ExitCode)" }
    }
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup compiler not found.' }
# Use only the pinned clean archive, never a site's mutable MediaMTX configuration.
$taskMediaArchive = Join-Path $taskRoot 'artifacts/media-tools/mediamtx.zip'
if (-not (Test-Path -LiteralPath $taskMediaArchive)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskMediaArchive) -Force | Out-Null
    Invoke-WebRequest -Uri 'https://github.com/bluenviron/mediamtx/releases/download/v1.21.0/mediamtx_v1.21.0_windows_amd64.zip' -OutFile $taskMediaArchive
}
if ((Get-FileHash -LiteralPath $taskMediaArchive).Hash -ne '8A58A9B8C25EE99A96C23DC0A17F39ACE3072C01D2E148329073C64DDF83493D') { throw 'MediaMTX checksum mismatch.' }
$taskPayload = Join-Path $taskRoot ('artifacts/installer-staging/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskPayload | Out-Null
foreach ($taskProduct in @('App','ControlHost')) {
    $taskSource = Join-Path $BuildPath $taskProduct
    if ((Get-Item -LiteralPath $taskSource).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points cannot be packaged.' }
    foreach ($taskEntry in Get-ChildItem -LiteralPath $taskSource -Recurse -Force) {
        if ($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points cannot be packaged.' }
        if ($taskEntry.PSIsContainer) { continue }
        $taskRelative = [IO.Path]::GetRelativePath($taskSource, $taskEntry.FullName)
        if ($taskRelative -in @('server-startup.json','THIRD_PARTY_NOTICES.md') -or $taskEntry.Extension -eq '.pdb') { continue }
        if ($taskEntry.Name -match '(?i)(\.sqlite($|-)|\.db($|-)|\.dpapi|\.pfx|\.pem|\.key$|\.log$|^host\.json$|^client\.json|^server-startup|^mediamtx\.ya?ml$|^\.env)') {
            throw "Operational file is not redistributable: $taskProduct/$taskRelative"
        }
        $taskDestination = Join-Path (Join-Path $taskPayload $taskProduct) $taskRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
        Copy-Item -LiteralPath $taskEntry.FullName -Destination $taskDestination
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead($taskMediaArchive)
try {
    $taskMediaDir = Join-Path $taskPayload 'MediaMTX'
    New-Item -ItemType Directory -Path $taskMediaDir | Out-Null
    foreach ($taskName in @('mediamtx.exe','LICENSE')) {
        $taskEntry = $taskZip.GetEntry($taskName)
        if (-not $taskEntry) { throw "Missing MediaMTX archive entry: $taskName" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, (Join-Path $taskMediaDir $taskName))
    }
}
finally { $taskZip.Dispose() }
New-Item -ItemType Directory -Path (Join-Path $taskPayload 'Examples') | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'config/mediamtx.example.yml'),(Join-Path $taskRoot 'config/server-startup.example.json') -Destination (Join-Path $taskPayload 'Examples')
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $taskPayload 'App/THIRD_PARTY_NOTICES.md')
Copy-Item -LiteralPath (Join-Path $taskRoot 'installer/INSTALL.txt') -Destination (Join-Path $taskPayload 'INSTALL.txt')
$taskManifest = @(Get-ChildItem -LiteralPath $taskPayload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($taskPayload,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
[ordered]@{build=$taskBuildName;version=$taskVersion;mediaMtx='1.21.0';files=$taskManifest} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskPayload 'package-manifest.json') -Encoding UTF8
$taskPackageName = 'IntegratedContro-Setup-' + $taskBuildName.Substring(6)
$taskAppId = 'IntegratedContro.Desktop'
if ($VerificationPackage) {
    $taskAppId = 'IntegratedContro.InstallerVerification'
    $taskPackageName += '-verification'
}
$taskCompilerArgs = @('/Qp',"/DPayloadDir=$taskPayload","/DPackageVersion=$taskVersion","/DPackageName=$taskPackageName","/DInstallerAppId=$taskAppId","/O$OutputDirectory",(Join-Path $taskRoot 'installer/IntegratedContro.iss'))
& $CompilerPath @taskCompilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$taskOutput = Join-Path $OutputDirectory ($taskPackageName + '.exe')
[ordered]@{installer=$taskOutput;sourceBuild=$taskBuildName;payload=$taskPayload;version=$taskVersion;bytes=(Get-Item -LiteralPath $taskOutput).Length;sha256=(Get-FileHash -LiteralPath $taskOutput).Hash;builtAt=(Get-Date).ToString('o')} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'installer-result.json') -Encoding UTF8
Write-Output "설치 파일: $taskOutput"

