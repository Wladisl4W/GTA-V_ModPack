[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ScriptsDirectory)
$ErrorActionPreference = 'Stop'
$scripts = [IO.Path]::GetFullPath($ScriptsDirectory)
$old = Join-Path $scripts 'ReloaderPlugins\CrashLogger\CrashDumps'
$newRoot = Join-Path $scripts 'ReloaderPlugins\Plugins\Crash\Data'
$new = Join-Path $newRoot 'CrashDumps'
$localKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\GTA5.exe'
$silentKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\GTA5.exe'
if (Get-Process -Name GTA5 -ErrorAction SilentlyContinue) { throw 'Close GTA5.exe before changing dump paths.' }
if (!(Test-Path -LiteralPath $new -PathType Container) -or
    !(Test-Path -LiteralPath (Join-Path $newRoot 'SilentExitRegistryBackup.json') -PathType Leaf)) {
    throw 'Verified CrashLogger copy or Silent Exit backup is missing.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as administrator.'
}
$localBefore = (Get-ItemProperty -LiteralPath $localKey -ErrorAction Stop).DumpFolder
$silentBefore = (Get-ItemProperty -LiteralPath $silentKey -ErrorAction Stop).LocalDumpFolder
if ($localBefore -notin @($old,$new) -or $silentBefore -notin @((Join-Path $old 'SilentExit'),(Join-Path $new 'SilentExit'))) {
    throw 'Existing registry paths differ from the expected ModPack paths; nothing changed.'
}
$snapshot = Join-Path $newRoot 'RegistryPathsBeforeMigration.json'
if (!(Test-Path -LiteralPath $snapshot)) {
    @{DumpFolder=$localBefore;LocalDumpFolder=$silentBefore} | ConvertTo-Json |
        Set-Content -LiteralPath $snapshot -Encoding UTF8
}
try {
    New-ItemProperty -LiteralPath $localKey -Name DumpFolder -Value $new -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $silentKey -Name LocalDumpFolder -Value (Join-Path $new 'SilentExit') -PropertyType ExpandString -Force | Out-Null
    $actualLocal = Get-ItemProperty -LiteralPath $localKey
    $actualSilent = Get-ItemProperty -LiteralPath $silentKey
    if ($actualLocal.DumpFolder -ne $new -or $actualSilent.LocalDumpFolder -ne (Join-Path $new 'SilentExit')) {
        throw 'Registry path verification failed.'
    }
} catch {
    New-ItemProperty -LiteralPath $localKey -Name DumpFolder -Value $localBefore -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $silentKey -Name LocalDumpFolder -Value $silentBefore -PropertyType ExpandString -Force | Out-Null
    throw
}
Write-Host "Dump paths migrated and verified: $new"
