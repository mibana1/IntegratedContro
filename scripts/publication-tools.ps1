$ErrorActionPreference = 'Stop'

function Assert-PublicationPath([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publication path escaped its parent: $full"
    }
    return $full
}
function Assert-PublicationTree([string]$Path) {
    $cursor = Get-Item -LiteralPath $Path -Force
    while ($null -ne $cursor) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $($cursor.FullName)" }
        $cursor = $cursor.Parent
    }
    $items = @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "Publication contains a reparse point: $Path"
    }
    if (@($items | Where-Object { -not $_.PSIsContainer -and
        ($_.Name -eq 'host.json' -or $_.Name -match '\.(sqlite3?|db)(-(wal|shm|journal))?$' -or $_.Extension -in @('.db', '.dpapi', '.pfx')) }).Count) {
        throw "Operational data was found inside a publication: $Path"
    }
}
function Get-PublicationRunningPaths {
    @(Get-Process | ForEach-Object {
        try { if ($_.Path) { $_.Path } } catch { }
    })
}
function Test-PublicationRunning([string]$Directory, [string[]]$RunningPaths) {
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return @($RunningPaths | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}
function Get-PublicationBuilds([string]$PublishRoot) {
    foreach ($directory in Get-ChildItem -LiteralPath $PublishRoot -Directory) {
        # Only known generated directories qualify. Legacy Current copies retire with old builds.
        $stamp = $null
        if ($directory.Name -match '^(Build|CameraInputFix|MediaValidation)-(\d{8}-\d{6})(?:-(\d{3}))?$') {
            $stamp = [datetime]::ParseExact($Matches[2], 'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
            if ($Matches[3]) { $stamp = $stamp.AddMilliseconds([int]$Matches[3]) }
        }
        elseif ($directory.Name -in @('App', 'App-role-fix', 'ControlHost', 'Lighting', 'Current')) { $stamp = $directory.LastWriteTime }
        else { continue }
        $executables = @('App/IntegratedContro.App.exe', 'ControlHost/IntegratedContro.ControlHost.exe',
            'IntegratedContro.App.exe', 'IntegratedContro.ControlHost.exe')
        if (-not @($executables | Where-Object { Test-Path -LiteralPath (Join-Path $directory.FullName $_) -PathType Leaf }).Count) { continue }
        # New publish.ps1 builds carry an in-progress marker until both products finish.
        if (Test-Path -LiteralPath (Join-Path $directory.FullName '.publish-in-progress')) { continue }
        [pscustomobject]@{ Path=$directory.FullName; Name=$directory.Name; BuiltAt=$stamp }
    }
}
function Get-PublicationCleanupPlan([string]$PublishRoot) {
    $builds = @(Get-PublicationBuilds $PublishRoot | Sort-Object -Property @{ Expression='BuiltAt'; Descending=$true }, Name)
    [pscustomobject]@{
        Retained=@($builds | Select-Object -First 3 | ForEach-Object { $_.Path })
        Candidates=@($builds | Select-Object -Skip 3 | ForEach-Object { $_.Path })
    }
}
function Set-PublicationShortcut([string]$BuildPath, [string]$PublishRoot, [string]$WorkspaceRoot) {
    $build = Assert-PublicationPath $BuildPath $PublishRoot
    $app = Assert-PublicationPath (Join-Path $build 'App') $build
    Assert-PublicationTree $app
    $target = Join-Path $app 'IntegratedContro.App.exe'
    foreach ($required in @('IntegratedContro.App.exe', 'IntegratedContro.App.dll', 'IntegratedContro.App.deps.json',
        'IntegratedContro.App.runtimeconfig.json', 'coreclr.dll', 'libvlc/win-x64/libvlc.dll', 'THIRD_PARTY_NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) { throw "Incomplete app: $required" }
    }
    $workspace = [IO.Path]::GetFullPath($WorkspaceRoot)
    if (-not (Test-Path -LiteralPath $workspace -PathType Container) -or $workspace -eq [IO.Path]::GetPathRoot($workspace)) {
        throw 'An existing workspace folder is required.'
    }
    $cursor = Get-Item -LiteralPath $workspace -Force
    while ($null -ne $cursor) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Workspace reparse point is not allowed.' }
        $cursor = $cursor.Parent
    }
    $linkPath = Assert-PublicationPath (Join-Path $workspace 'IntegratedContro.lnk') $workspace
    if ((Test-Path -LiteralPath $linkPath) -and ((Get-Item -LiteralPath $linkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Shortcut reparse point is not allowed.'
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($linkPath)
        if (Test-Path -LiteralPath $linkPath) {
            $previous = Assert-PublicationPath $shortcut.TargetPath $PublishRoot
            if (-not $previous.EndsWith('\App\IntegratedContro.App.exe', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Existing shortcut belongs to a different application.'
            }
        }
        $shortcut.TargetPath = $target
        $shortcut.WorkingDirectory = $app
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = 'IntegratedContro 최신 배포 앱'
        $shortcut.Save()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        $check = $shell.CreateShortcut($linkPath)
        try {
            if ($check.TargetPath -ne $target -or $check.WorkingDirectory -ne $app) { throw 'Shortcut verification failed.' }
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($check) }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    [pscustomobject]@{ shortcut=$linkPath; executable=$target; applied=$true }
}

function Remove-OldPublications([string]$PublishRoot) {
    $plan = Get-PublicationCleanupPlan $PublishRoot
    $deleted = [Collections.Generic.List[string]]::new()
    $skipped = [Collections.Generic.List[object]]::new()
    foreach ($candidate in $plan.Candidates) {
        try {
            $target = Assert-PublicationPath $candidate $PublishRoot
            if ([IO.Path]::GetDirectoryName($target) -ne [IO.Path]::GetFullPath($PublishRoot)) { throw 'Only immediate build folders may be removed.' }
            Assert-PublicationTree $target
            if (Test-PublicationRunning $target @(Get-PublicationRunningPaths)) { throw 'Build is currently running.' }
            foreach ($file in Get-ChildItem -LiteralPath $target -Recurse -File) {
                $probe = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
            }
            # Final absolute path and tree checks immediately precede recursive deletion.
            $target = Assert-PublicationPath $target $PublishRoot
            Assert-PublicationTree $target
            Remove-Item -LiteralPath $target -Recurse -Force
            $deleted.Add($target)
        }
        catch { $skipped.Add([pscustomobject]@{ path=$candidate; reason=$_.Exception.Message }) }
    }
    [pscustomobject]@{ retained=$plan.Retained; deleted=$deleted.ToArray(); skipped=$skipped.ToArray() }
}
