[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$BackupDirectory = ''
)
$ErrorActionPreference = 'Stop'
function Hash([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Inside([string]$path, [string]$directory) {
    $full = [IO.Path]::GetFullPath($path)
    $prefix = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
    return $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}
$game = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')
$scripts = Join-Path $game 'scripts'
$package = Split-Path -Parent $MyInvocation.MyCommand.Path
if (!(Test-Path -LiteralPath $scripts -PathType Container)) { throw 'GTA scripts folder not found. Check -GameDirectory.' }
foreach ($running in @(Get-Process -Name GTA5,GTA5_Enhanced -ErrorAction SilentlyContinue)) {
    if (!$running.Path -or [IO.Path]::GetFullPath((Split-Path -Parent $running.Path)).TrimEnd('\') -eq $game) {
        throw 'Close GTA before installing into this game directory.'
    }
}
$manifestPath = Join-Path $package 'InstallManifest.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'InstallManifest.json is missing. Extract the entire archive first.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or @($manifest.files).Count -lt 2) { throw 'Unsupported or empty installer manifest.' }
$sources = @()
$seen = @{}
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ($relative -notmatch '^(Reloader\.dll|ReloaderPlugins/Plugins/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*)$' -or $seen.ContainsKey($relative)) {
        throw "Unsafe or duplicate installer path: $relative"
    }
    $seen[$relative] = $true
    $source = Join-Path $package ($relative.Replace('/', '\'))
    $target = Join-Path $scripts ($relative.Replace('/', '\'))
    if (!(Inside $source $package) -or !(Inside $target $scripts) -or !(Test-Path -LiteralPath $source -PathType Leaf) -or
        (Hash $source) -ne [string]$entry.sha256) { throw "Missing or damaged installer file: $relative" }
    $sources += [pscustomobject]@{Relative=$relative;Source=$source;Target=$target}
}
$plugins = Join-Path $scripts 'ReloaderPlugins\Plugins'
$legacy = @()
$known = @{}
foreach ($name in $manifest.legacyFiles) {
    $name = [string]$name
    if ($name -ne [IO.Path]::GetFileName($name) -or $known.ContainsKey($name) -or $name -eq 'Newtonsoft.Json.dll') {
        throw "Unsafe legacy name: $name"
    }
    $known[$name] = $true
    $old = Join-Path $plugins $name
    if (Test-Path -LiteralPath $old -PathType Leaf) { $legacy += $old }
}
if (Test-Path -LiteralPath $plugins -PathType Container) {
    $unknown = @(Get-ChildItem -LiteralPath $plugins -Filter '*.cs' -File | Where-Object { !$known.ContainsKey($_.Name) })
    if ($unknown.Count) { throw "Unknown root-level .cs file(s): $($unknown.Name -join ', '). Review them before updating." }
}
if (!$BackupDirectory) {
    $BackupDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ModPack\UpgradeBackups'
}
$backup = Join-Path ([IO.Path]::GetFullPath($BackupDirectory)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
if (Inside $backup $game -or Inside $backup $package) { throw 'Backup directory must be outside GTA and the extracted package.' }
$before = @{}
foreach ($item in $sources) {
    if (Test-Path -LiteralPath $item.Target) {
        if (!(Test-Path -LiteralPath $item.Target -PathType Leaf)) { throw "Destination is not a file: $($item.Target)" }
        $before[$item.Target] = $true
    } else { $before[$item.Target] = $false }
}
foreach ($old in $legacy) { $before[$old] = $true }
[IO.Directory]::CreateDirectory($backup) | Out-Null
foreach ($target in $before.Keys) {
    if (!$before[$target]) { continue }
    $relative = $target.Substring($scripts.Length + 1)
    $saved = Join-Path $backup $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $saved)) | Out-Null
    Copy-Item -LiteralPath $target -Destination $saved
    if ((Hash $target) -ne (Hash $saved)) { throw "Backup verification failed: $target" }
}
$changed = @()
try {
    foreach ($item in $sources) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $item.Target)) | Out-Null
        $changed += $item.Target
        Copy-Item -LiteralPath $item.Source -Destination $item.Target -Force
        if ((Hash $item.Target) -ne (Hash $item.Source)) { throw "Installation hash mismatch: $($item.Relative)" }
    }
    foreach ($old in $legacy) {
        $changed += $old
        Remove-Item -LiteralPath $old
    }
} catch {
    $failure = $_
    foreach ($target in ($changed | Select-Object -Unique)) {
        if ($before[$target]) {
            $saved = Join-Path $backup $target.Substring($scripts.Length + 1)
            Copy-Item -LiteralPath $saved -Destination $target -Force
        } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    throw $failure
}
Write-Host "Installed $($sources.Count) files; removed $($legacy.Count) verified legacy file(s)."
Write-Host "Previous files backed up at: $backup"
Write-Host 'Start GTA and check the fresh Reload OK entry in Loader/Data/Logs/Reloader.log.'
