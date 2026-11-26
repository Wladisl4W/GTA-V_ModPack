# Run with Windows PowerShell 5.1 (the same .NET Framework as GTA).
[CmdletBinding()]
param(
    [ValidateSet('Check','Update','CrashDumps','CrashCapture','SilentExit')][string]$Mode = 'Check',
    [ValidateSet('','On','Off')][string]$State = '',
    [string]$Config = '',
    [int]$TimeoutSeconds = 30,
    [switch]$Offline,
    [string]$Module = ''
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
$repo = Split-Path (Split-Path $toolDirectory -Parent) -Parent
if (!$Config) { $Config = Join-Path $repo 'development/config/modpack.local.json' }
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$scripts = Join-Path $settings.GameDirectory 'scripts'
$live = Join-Path $scripts 'ReloaderPlugins\Plugins'
$ready = Join-Path $repo 'development\.build\ReloaderPlugins\Plugins'
$source = Join-Path $repo 'development\.build\SourcePlugins'
$watcherProject = Join-Path $repo 'development/Crash\src\CrashWatcher.csproj'
$watcherOutput = Join-Path $repo 'development/Crash\src\bin\Release\net48\CrashWatcher.exe'
$watcherConfig = $watcherOutput + '.config'
$dumpInstaller = Join-Path $repo 'development/Crash\src\ConfigureCrashDumps.ps1'
$dumpInstallerCommand = Join-Path $repo 'development/Crash\src\InstallCrashDumps.cmd'
$captureInstaller = Join-Path $repo 'development/Crash\src\ConfigureCrashCapture.ps1'
$captureInstallCommand = Join-Path $repo 'development/Crash\src\InstallCrashCapture.cmd'
$captureDisableCommand = Join-Path $repo 'development/Crash\src\DisableCrashCapture.cmd'
$silentInstaller = Join-Path $repo 'development/Crash\src\ConfigureSilentExit.ps1'
$silentInstallCommand = Join-Path $repo 'development/Crash\src\InstallSilentExit.cmd'
$silentDisableCommand = Join-Path $repo 'development/Crash\src\DisableSilentExit.cmd'
$shvdn = Join-Path $settings.GameDirectory 'ScriptHookVDotNet3.dll'
$lemon = Join-Path $scripts 'LemonUI.SHVDN3.dll'
if ($Mode -eq 'CrashDumps') {
    if (!$State) { throw 'Use: modpack.cmd CrashDumps On|Off' }
    & $dumpInstaller -State $State -ScriptsDirectory $scripts
    if (!$?) { throw 'Crash dump setup failed.' }
    return
}
if ($Mode -eq 'CrashCapture') {
    if (!$State) { throw 'Use: modpack.cmd CrashCapture On|Off' }
    & $captureInstaller -State $State -ScriptsDirectory $scripts
    if (!$?) { throw 'Crash capture setup failed.' }
    return
}
if ($Mode -eq 'SilentExit') {
    if (!$State) { throw 'Use: modpack.cmd SilentExit On|Off' }
    & $silentInstaller -State $State -ScriptsDirectory $scripts
    if (!$?) { throw 'Silent exit setup failed.' }
    return
}
if ($Offline -and $Mode -ne 'Update') { throw 'Offline is only valid with Update.' }
if ($Module -and $Mode -ne 'Update') { throw 'Module is only valid with Update.' }
if ($Offline -and (Get-Process -Name GTA5 -ErrorAction SilentlyContinue)) {
    throw 'Close GTA5.exe before an offline update.'
}
& (Join-Path $PSScriptRoot 'stage-plugins.ps1') -Repository $repo -Destination $source
if (!$?) { throw 'Plugin staging failed.' }
[IO.Directory]::CreateDirectory($ready) | Out-Null
foreach ($path in @($source,$live,$ready,$shvdn,$lemon,$watcherProject,$dumpInstaller,$dumpInstallerCommand,
    $captureInstaller,$captureInstallCommand,$captureDisableCommand,$silentInstaller,$silentInstallCommand,$silentDisableCommand)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Required path missing: $path" }
}
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($dumpInstaller, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Crash dump installer syntax error: $($parseErrors[0])" }
[void][Management.Automation.Language.Parser]::ParseFile($captureInstaller, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Crash capture installer syntax error: $($parseErrors[0])" }
[void][Management.Automation.Language.Parser]::ParseFile($silentInstaller, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Silent exit installer syntax error: $($parseErrors[0])" }
dotnet build $watcherProject -c Release --nologo
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $watcherOutput) -or !(Test-Path -LiteralPath $watcherConfig)) {
    throw 'CrashWatcher build failed. No files copied.'
}
Write-Host "CrashWatcher built: $watcherOutput"
Add-Type -Path (Join-Path $repo 'development\Loader\src\PluginCompiler.cs') -ReferencedAssemblies System.Core,Microsoft.CSharp
$fingerprint = ''
$result = [PluginCompiler]::Compile($source,$scripts,$shvdn,$lemon,[ref]$fingerprint)
foreach ($diagnostic in $result.Errors) { Write-Host $diagnostic.ToString() }
if ($result.Errors.HasErrors) { throw 'Compilation failed. No files copied.' }
& (Join-Path $PSScriptRoot 'stage-plugins.ps1') -Repository $repo -Destination $source | Out-Null
if ([PluginCompiler]::Fingerprint($source) -ne $fingerprint) { throw 'Module sources changed during validation. Retry.' }
Write-Host "Plugins compiled successfully. Fingerprint: $fingerprint"
if ($Mode -eq 'Check') { return }
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 300) { throw 'TimeoutSeconds must be between 1 and 300.' }
$loaderPath = Join-Path $scripts 'Reloader.dll'
if (!(Test-Path -LiteralPath $loaderPath) -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($loaderPath).FileMajorPart -lt 2) {
    throw 'Install Reloader file version 2.0.0.0 or newer with GTA closed before using Update. Check works with the old loader.'
}

# Reject unknown live sources: otherwise the verified source set and GTA differ.
$sources = @([PluginCompiler]::GetSources($source) | ForEach-Object { Get-Item -LiteralPath $_ })
$selected = @{}
if ($Module) {
    $manifest = Get-Content -LiteralPath (Join-Path $repo 'development/registry/modules.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $entry = @($manifest.modules | Where-Object { $_.id -eq $Module })
    if ($entry.Count -ne 1 -or !$entry[0].sources.Count) { throw "Unknown or non-plugin module: $Module" }
    foreach ($name in $entry[0].sources) {
        $relative = $name.Replace('/', '\')
        $selected[$relative] = $true
    }
}
$expected = @{}
foreach ($file in $sources) { $expected[$file.FullName.Substring($source.Length + 1)] = $true }
$renamePath = Join-Path $repo 'development/registry/source-renames.json'
$renamedSources = @{}
if (Test-Path -LiteralPath $renamePath) {
    $renameCatalog = Get-Content -LiteralPath $renamePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($renameCatalog.schemaVersion -ne 1) { throw 'Unsupported source rename schema.' }
    foreach ($property in $renameCatalog.renames.PSObject.Properties) {
        $oldRelative = $property.Name.Replace('/', '\')
        $newRelative = ([string]$property.Value).Replace('/', '\')
        if (!$expected.ContainsKey($newRelative)) { continue }
        if ($expected.ContainsKey($oldRelative) -or
            $renamedSources.ContainsKey($oldRelative)) { throw "Invalid source rename: $oldRelative" }
        $renamedSources[$oldRelative] = $newRelative
    }
}
$legacyMoves = @()
foreach ($destination in @($ready,$live)) {
    $extra = @()
    $installed = @(Get-ChildItem -LiteralPath $destination -Filter '*.cs' -File -Recurse)
    foreach ($file in $installed) {
        $relative = $file.FullName.Substring($destination.Length + 1)
        if ($expected.ContainsKey($relative)) { continue }
        $matches = @($sources | Where-Object { $_.Name -eq $file.Name -and
            $_.FullName.Substring($source.Length + 1) -ne $_.Name })
        if ($renamedSources.ContainsKey($relative) -and
            (!$Module -or $selected.ContainsKey($renamedSources[$relative]))) {
            $legacyMoves += $file.FullName
        } elseif ($relative -eq $file.Name -and $matches.Count -eq 1 -and
            (!$Module -or $selected.ContainsKey($matches[0].FullName.Substring($source.Length + 1)))) {
            $legacyMoves += $file.FullName
        } else { $extra += $relative }
    }
    if ($extra.Count) { throw "Extra sources in ${destination}: $($extra -join ', '). Review manually; nothing deleted." }
}
$changes = @()
foreach ($file in $sources) {
    $hash = Get-Sha256 $file.FullName
    $relative = $file.FullName.Substring($source.Length + 1)
    foreach ($destination in @($ready,$live)) {
        $target = Join-Path $destination $relative
        if ($Module -and !$selected.ContainsKey($relative)) {
            if (!(Test-Path -LiteralPath $target) -or (Get-Sha256 $target) -ne $hash) {
                throw "Other module differs in ${destination}: $relative. Synchronize it first; nothing copied."
            }
            continue
        }
        if (!(Test-Path -LiteralPath $target) -or
            (Get-Sha256 $target) -ne $hash) {
            $changes += [pscustomobject]@{Source=$file.FullName;Target=$target;Hash=$hash;Live=($destination -eq $live);Reload=$true}
        }
    }
}
$watcherArtifacts = @(
    [pscustomobject]@{Source=$watcherOutput;Name='CrashWatcher.exe'},
    [pscustomobject]@{Source=$watcherConfig;Name='CrashWatcher.exe.config'},
    [pscustomobject]@{Source=$dumpInstaller;Name='ConfigureCrashDumps.ps1'},
    [pscustomobject]@{Source=$dumpInstallerCommand;Name='InstallCrashDumps.cmd'},
    [pscustomobject]@{Source=$captureInstaller;Name='ConfigureCrashCapture.ps1'},
    [pscustomobject]@{Source=$captureInstallCommand;Name='InstallCrashCapture.cmd'},
    [pscustomobject]@{Source=$captureDisableCommand;Name='DisableCrashCapture.cmd'},
    [pscustomobject]@{Source=$silentInstaller;Name='ConfigureSilentExit.ps1'},
    [pscustomobject]@{Source=$silentInstallCommand;Name='InstallSilentExit.cmd'},
    [pscustomobject]@{Source=$silentDisableCommand;Name='DisableSilentExit.cmd'}
)
foreach ($artifact in $(if ($Module) { @() } else { $watcherArtifacts })) {
    $artifactHash = Get-Sha256 $artifact.Source
    foreach ($destination in @($ready,$live)) {
        $target = Join-Path $destination (Join-Path '_System\Diagnostics\Tools' $artifact.Name)
        if (!(Test-Path -LiteralPath $target) -or (Get-Sha256 $target) -ne $artifactHash) {
            $changes += [pscustomobject]@{Source=$artifact.Source;Target=$target;Hash=$artifactHash;Live=($destination -eq $live);Reload=$false}
        }
        $old = Join-Path $destination $artifact.Name
        if (Test-Path -LiteralPath $old -PathType Leaf) { $legacyMoves += $old }
    }
}
if (!$changes.Count -and !$legacyMoves.Count) { Write-Host 'Already synchronized. No reload requested.'; return }
$logPath = Join-Path $scripts 'ReloaderPlugins\Plugins\_System\Diagnostics\Data\Loader\Logs\Reloader.log'
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
    foreach ($old in $legacyMoves) { $backups[$old] = ,([IO.File]::ReadAllBytes($old)) }
    foreach ($change in $changes) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $change.Target)) | Out-Null
        Copy-Item -LiteralPath $change.Source -Destination $change.Target -Force
        if ((Get-Sha256 $change.Target) -ne $change.Hash) {
            throw "Hash mismatch: $($change.Target)"
        }
        Write-Host "Updated: $($change.Target)"
    }
    foreach ($old in $legacyMoves) {
        Remove-Item -LiteralPath $old
        Write-Host "Migrated legacy source: $old"
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
if (!@($changes | Where-Object { $_.Live -and $_.Reload }).Count) {
    Write-Host 'Artifacts updated. CrashWatcher update takes effect on the next GTA start.'
    return
}
if ($Offline) {
    Write-Host 'Offline update verified. GTA will load these sources on its next start.'
    return
}
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
throw 'Files synchronized, but live reload NOT confirmed. Start/unpause GTA and inspect Reloader.log; F5 only opens the catalog.'
