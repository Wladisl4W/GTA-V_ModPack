[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ScriptsDirectory,
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $ScriptsDirectory 'ReloaderPlugins')).TrimEnd('\')
$backup = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\')
if (!(Test-Path -LiteralPath $root -PathType Container) -or !(Test-Path -LiteralPath $backup -PathType Container)) {
    throw 'Game data root or verified backup is missing.'
}
if ($Apply -and (Get-Process -Name GTA5 -ErrorAction SilentlyContinue)) { throw 'Close GTA5.exe before migrating data.' }
$owners = [ordered]@{
    ModdedCamera='Camera'; RainbowPaint='Paint'; SharkRider='Wildlife'
    RemoveDroppedPeds='Cleanup'; FrozenDynamic='PedControl'
    MenyooStreamer='Streamer'; Development='Dev'; Logs='Core'
}
$moves = @()
foreach ($owner in $owners.Keys) {
    $sourceDir = Join-Path $root $owner
    if (!(Test-Path -LiteralPath $sourceDir -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $sourceDir -File -Recurse) {
        $relative = $file.FullName.Substring($sourceDir.Length + 1)
        $backupFile = Join-Path (Join-Path $backup $owner) $relative
        if (!(Test-Path -LiteralPath $backupFile -PathType Leaf)) { throw "File absent from backup: $($file.FullName)" }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $backupFile -Algorithm SHA256).Hash -ne $hash) {
            throw "File changed after backup: $($file.FullName)"
        }
        if ($owner -eq 'ModdedCamera' -and $relative.StartsWith('Paths\',[StringComparison]::OrdinalIgnoreCase)) {
            $targetRelative = Join-Path 'Saves' $relative
        } elseif ($file.Extension -eq '.log' -or $owner -eq 'Logs') {
            $targetRelative = Join-Path 'Logs' $relative
        } else {
            $targetRelative = Join-Path 'Settings' $relative
        }
        $dataRoot = Join-Path $root (Join-Path 'Plugins' (Join-Path $owners[$owner] 'Data'))
        $target = [IO.Path]::GetFullPath((Join-Path $dataRoot $targetRelative))
        if (!$target.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Target escapes data root.' }
        if ((Test-Path -LiteralPath $target -PathType Leaf) -and (Get-FileHash -LiteralPath $target).Hash -ne $hash) {
            $target = Join-Path $dataRoot (Join-Path 'Legacy' ($relative.Replace('\','_') + '.' + $hash.Substring(0,12)))
        }
        $moves += [pscustomobject]@{Source=$file.FullName;Target=$target;Hash=$hash}
    }
}
Write-Host "Regular data: $($moves.Count) file(s) to verify or migrate. CrashLogger is excluded."
if (!$Apply) { return }
foreach ($move in $moves) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $move.Target)) | Out-Null
    if (!(Test-Path -LiteralPath $move.Target -PathType Leaf)) {
        Copy-Item -LiteralPath $move.Source -Destination $move.Target
    }
    if ((Get-FileHash -LiteralPath $move.Target -Algorithm SHA256).Hash -ne $move.Hash) {
        throw "Copy verification failed: $($move.Source)"
    }
}
foreach ($move in $moves) {
    if ((Get-FileHash -LiteralPath $move.Source -Algorithm SHA256).Hash -ne $move.Hash) {
        throw "Source changed during migration: $($move.Source)"
    }
    Remove-Item -LiteralPath $move.Source
}
foreach ($owner in $owners.Keys) {
    $sourceDir = Join-Path $root $owner
    if (!(Test-Path -LiteralPath $sourceDir -PathType Container)) { continue }
    $directories = @(Get-ChildItem -LiteralPath $sourceDir -Directory -Recurse | Sort-Object FullName -Descending)
    $directories += Get-Item -LiteralPath $sourceDir
    foreach ($directory in $directories) {
        if (!(Get-ChildItem -LiteralPath $directory.FullName -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $directory.FullName
        }
    }
}
Write-Host "Verified and migrated $($moves.Count) regular data files."
