$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ModPackDataTests-' + [Guid]::NewGuid().ToString('N'))
$scripts = Join-Path $temp 'game/scripts'
$root = Join-Path $scripts 'ReloaderPlugins'
$backup = Join-Path $temp 'backup'
try {
    foreach ($name in @('ModdedCamera/Paths','RainbowPaint','RemoveDroppedPeds','Logs')) {
        [IO.Directory]::CreateDirectory((Join-Path $root $name)) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $backup $name)) | Out-Null
    }
    $files = @{
        'ModdedCamera/Paths/path.json'='camera path'
        'ModdedCamera/Paths/path.json.bak'='camera backup'
        'RainbowPaint/RainbowPaintSettings.json'='settings'
        'RemoveDroppedPeds/RemoveDroppedPeds.log'='events'
        'Logs/PluginErrors.log'='errors'
    }
    foreach ($name in $files.Keys) {
        [IO.File]::WriteAllText((Join-Path $root $name),$files[$name])
        [IO.File]::WriteAllText((Join-Path $backup $name),$files[$name])
    }
    $migration = Join-Path $repo 'tools/migrate-regular-data.ps1'
    & $migration -ScriptsDirectory $scripts -BackupDirectory $backup | Out-Null
    if (!(Test-Path -LiteralPath (Join-Path $root 'ModdedCamera/Paths/path.json'))) { throw 'Preview changed source.' }
    & $migration -ScriptsDirectory $scripts -BackupDirectory $backup -Apply | Out-Null
    $targets = @{
        'Plugins/Camera/Data/Saves/Paths/path.json'='camera path'
        'Plugins/Camera/Data/Saves/Paths/path.json.bak'='camera backup'
        'Plugins/Paint/Data/Settings/RainbowPaintSettings.json'='settings'
        'Plugins/Cleanup/Data/Logs/RemoveDroppedPeds.log'='events'
        'Plugins/Core/Data/Logs/PluginErrors.log'='errors'
    }
    foreach ($name in $targets.Keys) {
        if ([IO.File]::ReadAllText((Join-Path $root $name)) -ne $targets[$name]) { throw "Wrong migrated contents: $name" }
    }
    foreach ($name in $files.Keys) { if (Test-Path -LiteralPath (Join-Path $root $name)) { throw "Old file remains: $name" } }
    & $migration -ScriptsDirectory $scripts -BackupDirectory $backup -Apply | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $root 'RainbowPaint')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'RainbowPaint/RainbowPaintSettings.json'),'settings')
    & $migration -ScriptsDirectory $scripts -BackupDirectory $backup -Apply | Out-Null
    if (Test-Path -LiteralPath (Join-Path $root 'RainbowPaint/RainbowPaintSettings.json')) { throw 'Identical repeat did not remove old file.' }
    Write-Host 'PASS: preview, settings, paths, backup, logs, repeat and verified removal.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackDataTests-'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
