[CmdletBinding()]
param(
    [ValidateSet('On','Off')][string]$State = 'On',
    [string]$ScriptsDirectory = ''
)
$ErrorActionPreference = 'Stop'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\GTA5.exe'
$names = @('DumpFolder', 'DumpType', 'DumpCount')

if (!$ScriptsDirectory) {
    $ScriptsDirectory = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (!(Test-Path -LiteralPath (Join-Path (Split-Path $ScriptsDirectory -Parent) 'GTA5.exe') -PathType Leaf)) {
        throw 'Copy the installer into GTA V\scripts\ReloaderPlugins\Plugins before running it.'
    }
}
$ScriptsDirectory = [IO.Path]::GetFullPath($ScriptsDirectory)
if (!(Test-Path -LiteralPath $ScriptsDirectory -PathType Container)) {
    throw "Scripts folder not found: $ScriptsDirectory"
}
$dumpFolder = Join-Path $ScriptsDirectory 'ReloaderPlugins\CrashLogger\CrashDumps'

function Test-Configured {
    if (!(Test-Path -LiteralPath $key)) { return $false }
    $values = Get-ItemProperty -LiteralPath $key
    return ($values.DumpFolder -eq $dumpFolder -and $values.DumpType -eq 1 -and $values.DumpCount -eq 3)
}

function Test-Disabled {
    if (!(Test-Path -LiteralPath $key)) { return $true }
    $values = Get-ItemProperty -LiteralPath $key
    foreach ($name in $names) {
        if ($null -ne $values.$name) { return $false }
    }
    return $true
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) {
    if (($State -eq 'On' -and (Test-Configured)) -or ($State -eq 'Off' -and (Test-Disabled))) {
        Write-Host "GTA5 crash dumps already $($State.ToLowerInvariant())."
        return
    }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-State', $State, '-ScriptsDirectory', ('"' + $ScriptsDirectory + '"'))
    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    } catch {
        throw "Administrator approval was denied or failed: $($_.Exception.Message)"
    }
    if ($process.ExitCode -ne 0) { throw "Crash dump setup failed (exit code $($process.ExitCode))." }
} elseif ($State -eq 'On') {
    [void](New-Item -ItemType Directory -Path $dumpFolder -Force)
    [void](New-Item -Path $key -Force)
    New-ItemProperty -LiteralPath $key -Name DumpFolder -Value $dumpFolder -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name DumpType -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name DumpCount -Value 3 -PropertyType DWord -Force | Out-Null
} else {
    if (Test-Path -LiteralPath $key) {
        foreach ($name in $names) {
            Remove-ItemProperty -LiteralPath $key -Name $name -ErrorAction SilentlyContinue
        }
    }
}

if ($State -eq 'On') {
    if (!(Test-Configured)) { throw "GTA5 crash dump registry verification failed: $key" }
    Write-Host "GTA5 minidumps enabled: $dumpFolder"
} else {
    if (!(Test-Disabled)) { throw "GTA5 crash dump registry verification failed: $key" }
    Write-Host 'GTA5 minidumps disabled. Existing dump files were retained.'
}
