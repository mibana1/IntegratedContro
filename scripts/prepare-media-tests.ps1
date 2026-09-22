$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot 'artifacts/media-tools'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null

# FFmpeg remains test-only. build-installer.ps1 also uses the verified MediaMTX archive, extracting only its executable and license, never the mutable test configuration.
# The FFmpeg release URL can change; the pinned 9.0.1 SHA-256 must match before extraction.
# Keep the verified archives for reproducible/offline validation. Never accept a new hash automatically.
$taskPackages = @(
    @{ Name='mediamtx'; Url='https://github.com/bluenviron/mediamtx/releases/download/v1.21.0/mediamtx_v1.21.0_windows_amd64.zip'; Hash='8a58a9b8c25ee99a96c23dc0a17f39ace3072c01d2e148329073c64ddf83493d' },
    @{ Name='ffmpeg'; Url='https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'; Hash='fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9' }
)
foreach ($taskPackage in $taskPackages) {
    $taskArchive = Join-Path $taskTools ($taskPackage.Name + '.zip')
    if (-not (Test-Path -LiteralPath $taskArchive) -or (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskPackage.Hash) {
        Invoke-WebRequest -Uri $taskPackage.Url -OutFile $taskArchive
    }
    if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskPackage.Hash) {
        throw "$($taskPackage.Name) checksum mismatch; binary was not extracted or executed."
    }
    Expand-Archive -LiteralPath $taskArchive -DestinationPath (Join-Path $taskTools $taskPackage.Name) -Force
    Write-Output "$($taskPackage.Name): pinned SHA-256 verified"
}
