# Run with Windows PowerShell 5.1 (the same .NET Framework as GTA).
[CmdletBinding()]
param(
    [ValidateSet('Check','Update')][string]$Mode = 'Check',
    [string]$Config = '',
    [int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use powershell.exe, not pwsh.exe.' }
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}
$toolDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path $toolDirectory -Parent
if (!$Config) { $Config = Join-Path $repo 'modpack.local.json' }
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$scripts = Join-Path $settings.GameDirectory 'scripts'
$live = Join-Path $scripts 'ReloaderPlugins\Plugins'
$ready = Join-Path $repo 'Ready To Use\ReloaderPlugins\Plugins'
$source = Join-Path $repo 'Source Code\Plugins'
$shvdn = Join-Path $settings.GameDirectory 'ScriptHookVDotNet3.dll'
$lemon = Join-Path $scripts 'LemonUI.SHVDN3.dll'
foreach ($path in @($source,$live,$ready,$shvdn,$lemon)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Required path missing: $path" }
}
Add-Type -Path (Join-Path $repo 'Source Code\Reloader\PluginCompiler.cs') -ReferencedAssemblies System.Core,Microsoft.CSharp
$fingerprint = ''
$result = [PluginCompiler]::Compile($source,$scripts,$shvdn,$lemon,[ref]$fingerprint)
foreach ($diagnostic in $result.Errors) { Write-Host $diagnostic.ToString() }
if ($result.Errors.HasErrors) { throw 'Compilation failed. No files copied.' }
Write-Host "Plugins compiled successfully. Fingerprint: $fingerprint"
if ($Mode -eq 'Check') { return }
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 300) { throw 'TimeoutSeconds must be between 1 and 300.' }
$loaderPath = Join-Path $scripts 'Reloader.dll'
if (!(Test-Path -LiteralPath $loaderPath) -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($loaderPath).FileMajorPart -lt 2) {
    throw 'Install Reloader file version 2.0.0.0 or newer with GTA closed before using Update. Check works with the old loader.'
}

# Reject unknown live sources: otherwise the verified source set and GTA differ.
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File)
foreach ($destination in @($ready,$live)) {
    $extra = @(Get-ChildItem -LiteralPath $destination -Filter '*.cs' -File |
        Where-Object { $_.Name -notin $sources.Name })
    if ($extra.Count) { throw "Extra sources in ${destination}: $($extra.Name -join ', '). Review manually; nothing deleted." }
}
$changes = @()
foreach ($file in $sources) {
    $hash = Get-Sha256 $file.FullName
    foreach ($destination in @($ready,$live)) {
        $target = Join-Path $destination $file.Name
        if (!(Test-Path -LiteralPath $target) -or
            (Get-Sha256 $target) -ne $hash) {
            $changes += [pscustomobject]@{Source=$file.FullName;Target=$target;Hash=$hash;Live=($destination -eq $live)}
        }
    }
}
if (!$changes.Count) { Write-Host 'Already synchronized. No reload requested.'; return }
$logPath = Join-Path $scripts 'ReloaderPlugins\Reloader.log'
$offset = if (Test-Path -LiteralPath $logPath) { (Get-Item -LiteralPath $logPath).Length } else { 0 }
$lockPath = Join-Path $live '.deploy.lock'
$deploymentLock = $null
$backups = @{}
try {
    $deploymentLock = [IO.File]::Open($lockPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    if ([PluginCompiler]::Fingerprint($source) -ne $fingerprint) { throw 'Sources changed after validation. Retry.' }
    foreach ($change in $changes) {
        $backups[$change.Target] = if (Test-Path -LiteralPath $change.Target) { ,([IO.File]::ReadAllBytes($change.Target)) } else { $null }
    }
    foreach ($change in $changes) {
        Copy-Item -LiteralPath $change.Source -Destination $change.Target -Force
        if ((Get-Sha256 $change.Target) -ne $change.Hash) {
            throw "Hash mismatch: $($change.Target)"
        }
        Write-Host "Updated: $($change.Target)"
    }
    if ([PluginCompiler]::Fingerprint($live) -ne $fingerprint) { throw 'Live source fingerprint mismatch.' }
}
catch {
    foreach ($target in $backups.Keys) {
        if ($null -eq $backups[$target]) {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
        } else { [IO.File]::WriteAllBytes($target,[byte[]]$backups[$target]) }
    }
    throw
}
finally {
    if ($null -ne $deploymentLock) { $deploymentLock.Dispose(); Remove-Item -LiteralPath $lockPath }
}
if (!@($changes | Where-Object Live).Count) { Write-Host 'Package updated; live files were already current.'; return }
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    if (Test-Path -LiteralPath $logPath) {
        $stream = [IO.File]::Open($logPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try {
            if ($stream.Length -lt $offset) { throw 'Reloader log was truncated; reload cannot be verified.' }
            [void]$stream.Seek($offset,[IO.SeekOrigin]::Begin)
            $reader = New-Object IO.StreamReader($stream)
            $fresh = $reader.ReadToEnd()
        } finally { $stream.Dispose() }
        if ($fresh.Contains("Reload OK: $fingerprint")) {
            Write-Host 'Verified: GTA loaded this exact source version.'
            return
        }
        if ($fresh -match 'Reload failed:|Compile error|Reload incomplete:') { throw "Live reload failed. See $logPath" }
    }
    Start-Sleep -Milliseconds 250
}
throw 'Files synchronized, but live reload NOT confirmed. Start/unpause GTA, install the updated loader if needed, then press F5 and inspect Reloader.log.'
