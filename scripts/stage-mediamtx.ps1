param([Parameter(Mandatory=$true)][string]$DestinationPath)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskRoot = Split-Path -Parent $PSScriptRoot
# Share the same verified, configuration-free MediaMTX bundle between published apps and installers.
$taskMediaArchive = Join-Path $taskRoot 'artifacts/media-tools/mediamtx.zip'
if (Test-Path -LiteralPath $DestinationPath) { throw 'MediaMTX destination must be a new directory; existing files are preserved.' }
if (-not (Test-Path -LiteralPath $taskMediaArchive)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskMediaArchive) -Force | Out-Null
    Invoke-WebRequest -Uri 'https://github.com/bluenviron/mediamtx/releases/download/v1.21.0/mediamtx_v1.21.0_windows_amd64.zip' -OutFile $taskMediaArchive
}
if ((Get-FileHash -LiteralPath $taskMediaArchive).Hash -ne '8A58A9B8C25EE99A96C23DC0A17F39ACE3072C01D2E148329073C64DDF83493D') { throw 'MediaMTX checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead($taskMediaArchive)
try {
    New-Item -ItemType Directory -Path $DestinationPath -ErrorAction Stop | Out-Null
    foreach ($taskName in @('mediamtx.exe','LICENSE')) {
        $taskEntry = $taskZip.GetEntry($taskName)
        if (-not $taskEntry) { throw "Missing MediaMTX archive entry: $taskName" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, (Join-Path $DestinationPath $taskName))
    }
}
finally { $taskZip.Dispose() }
