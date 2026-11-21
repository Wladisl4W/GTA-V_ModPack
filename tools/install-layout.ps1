param([Parameter(Mandatory=$true)][string]$ScriptsDirectory,
      [Parameter(Mandatory=$true)][string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process GTA5 -ErrorAction SilentlyContinue) { throw 'Close GTA first.' }
$repo = Split-Path -Parent $PSScriptRoot
$modules = [IO.Path]::GetFullPath((Join-Path $repo 'modules'))
$scripts = [IO.Path]::GetFullPath($ScriptsDirectory)
$backup = [IO.Path]::GetFullPath($BackupDirectory)
if (Test-Path -LiteralPath $backup) { throw "Backup already exists: $backup" }
$newFiles = @(Get-ChildItem -LiteralPath $modules -File -Recurse)
$oldPlugins = Join-Path $scripts 'ReloaderPlugins/Plugins'
$oldSources = @(Get-ChildItem -LiteralPath $oldPlugins -Filter '*.cs' -File -Recurse)
if ($oldSources.Count -ne 39) { throw "Expected 39 old plugin sources, found $($oldSources.Count). Review before installing." }
$newRelative = @{}
foreach ($file in $newFiles) { $newRelative[$file.FullName.Substring($modules.Length + 1)] = $true }
$oldExtra = @($oldSources | Where-Object {
    !$newRelative.ContainsKey('ReloaderPlugins\Plugins\' + $_.FullName.Substring($oldPlugins.Length + 1))
})
function Hash([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}
$oldFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $oldSources) { [void]$oldFiles.Add($file.FullName) }
foreach ($file in $newFiles) {
    $target = Join-Path $scripts $file.FullName.Substring($modules.Length + 1)
    if (Test-Path -LiteralPath $target -PathType Leaf) { [void]$oldFiles.Add($target) }
}
foreach ($path in $oldFiles) {
    $relative = $path.Substring($scripts.Length + 1)
    $target = Join-Path $backup $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath $path -Destination $target
    if ((Hash $path) -ne (Hash $target)) { throw "Backup differs: $relative" }
}
$created = New-Object 'System.Collections.Generic.List[string]'
try {
    foreach ($file in $newFiles) {
        $relative = $file.FullName.Substring($modules.Length + 1)
        $target = Join-Path $scripts $relative
        if (!(Test-Path -LiteralPath $target)) { $created.Add($target) }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        if ((Hash $file.FullName) -ne (Hash $target)) { throw "Installed file differs: $relative" }
    }
    foreach ($file in $oldExtra) {
        [IO.File]::Delete($file.FullName)
        if (Test-Path -LiteralPath $file.FullName) { throw "Old source remains: $($file.FullName)" }
    }
} catch {
    foreach ($path in $created) { if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::Delete($path) } }
    foreach ($path in $oldFiles) {
        $relative = $path.Substring($scripts.Length + 1)
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination $path -Force
    }
    throw
}
Write-Host "Installed $($newFiles.Count) files, removed $($oldExtra.Count) obsolete sources. Backup: $backup"
