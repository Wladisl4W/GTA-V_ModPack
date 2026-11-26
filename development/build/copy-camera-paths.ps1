param([Parameter(Mandatory=$true)][string]$ScriptsDirectory,
      [Parameter(Mandatory=$true)][string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
$scripts = [IO.Path]::GetFullPath($ScriptsDirectory)
$backup = [IO.Path]::GetFullPath($BackupDirectory)
$old = Join-Path $scripts 'ReloaderPlugins/Plugins/Camera/Data/Saves/Paths'
$new = Join-Path $scripts 'ReloaderPlugins/Plugins/Camera/Data/Paths'
if (!(Test-Path -LiteralPath $old -PathType Container)) { throw "Camera paths not found: $old" }
if (Test-Path -LiteralPath $backup) { throw "Backup directory already exists: $backup" }
$files = @(Get-ChildItem -LiteralPath $old -File -Recurse)
if (!$files.Count) { throw 'Camera paths directory is empty.' }
function Hash([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($old.Length + 1)
    $target = Join-Path $new $relative
    if ((Test-Path -LiteralPath $target) -and (Hash $file.FullName) -ne (Hash $target)) {
        throw "Camera path conflict: $relative"
    }
}
$created = New-Object 'System.Collections.Generic.List[string]'
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($old.Length + 1)
        $backupFile = Join-Path $backup $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $backupFile)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $backupFile
        if ((Hash $file.FullName) -ne (Hash $backupFile)) { throw "Backup mismatch: $relative" }
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($old.Length + 1)
        $target = Join-Path $new $relative
        if (Test-Path -LiteralPath $target) { continue }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $created.Add($target)
        if ((Hash $file.FullName) -ne (Hash $target)) { throw "Target mismatch: $relative" }
    }
} catch {
    foreach ($path in $created) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    throw
}
Write-Host "Verified $($files.Count) camera files; backup: $backup"
