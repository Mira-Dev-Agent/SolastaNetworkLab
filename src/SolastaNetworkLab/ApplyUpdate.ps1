param(
    [Parameter(Mandatory)][int]$GameProcessId,
    [Parameter(Mandatory)][long]$GameStartTicks,
    [Parameter(Mandatory)][string]$TargetDirectory,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$target = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
$package = [IO.Path]::GetFullPath($PackagePath)
$updates = Join-Path $target '.updates'
if (!$package.StartsWith($updates + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package must be staged inside this mod directory' }
New-Item -ItemType Directory -Path $updates -Force | Out-Null
$logPath = Join-Path $updates 'install.log'
$zip = $null
$backups = @{}
$written = [Collections.Generic.List[string]]::new()
try {
    $game = Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
    if ($game) {
        if ($game.StartTime.ToUniversalTime().Ticks -ne $GameStartTicks) { throw 'Game process identity changed' }
        "Waiting for Solasta to exit before installing $ExpectedVersion" | Set-Content -LiteralPath $logPath
        $game.WaitForExit()
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    $packageStream = [IO.File]::OpenRead($package)
    try { $actualHash = [BitConverter]::ToString($hash.ComputeHash($packageStream)).Replace('-', '') }
    finally { $packageStream.Dispose(); $hash.Dispose() }
    if ($actualHash -ine $ExpectedSha256) { throw 'Package checksum changed' }
    $files = @('SolastaNetworkLab.dll', 'DirectTransport.dll', 'Info.json', 'ApplyUpdate.ps1', 'README.md', 'analysis/FINDINGS.md')
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    $entries = @($zip.Entries | Where-Object { !$_.FullName.EndsWith('/') })
    if ($entries.Count -ne $files.Count -or ($entries | Measure-Object Length -Sum).Sum -gt 33554432) { throw 'Unexpected package contents' }
    foreach ($relative in $files) {
        if (@($entries | Where-Object FullName -CEQ ('SolastaNetworkLab/' + $relative)).Count -ne 1) { throw 'Missing or duplicate package entry' }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('SolastaNetworkLab/Info.json').Open())
    try { $info = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($info.Id -cne 'SolastaNetworkLab' -or $info.Version -cne $ExpectedVersion) { throw 'Mod identity/version mismatch' }
    $backupDirectory = Join-Path $updates ('backup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    # Back up every existing target before replacing any files.
    foreach ($relative in $files) {
        $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (!$destination.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid target path' }
        $parent = [IO.Path]::GetDirectoryName($destination)
        # Reject junctions/symlinks in the target or any existing parent below it.
        $ancestor = $parent
        while ($ancestor.Length -ge $target.Length) {
            if (Test-Path -LiteralPath $ancestor) {
                if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Update target contains a reparse point' }
            }
            if ($ancestor -ieq $target) { break }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        if (Test-Path -LiteralPath $destination) {
            if ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Update file is a reparse point' }
            $backup = Join-Path $backupDirectory $relative
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup
            $backups[$destination] = $backup
        } else { $backups[$destination] = $null }
    }
    foreach ($relative in $files) {
        $destination = Join-Path $target $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        $written.Add($destination)
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('SolastaNetworkLab/' + $relative), $destination, $true)
    }
    "Installed $ExpectedVersion. Backup: $backupDirectory" | Add-Content -LiteralPath $logPath
} catch {
    $failure = $_.Exception.Message
    foreach ($destination in $written) {
        try {
            if ($backups[$destination]) { Copy-Item -LiteralPath $backups[$destination] -Destination $destination -Force }
            elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
        } catch { "Rollback failed for $destination : $($_.Exception.Message)" | Add-Content -LiteralPath $logPath }
    }
    "Update failed: $failure" | Add-Content -LiteralPath $logPath
    exit 1
} finally { if ($zip) { $zip.Dispose() } }
