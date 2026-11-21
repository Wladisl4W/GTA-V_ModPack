[CmdletBinding()]
param(
    [ValidateSet('On','Off')][string]$State = 'On',
    [string]$ScriptsDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (!$ScriptsDirectory) {
    $ScriptsDirectory = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent
}
$ScriptsDirectory = [IO.Path]::GetFullPath($ScriptsDirectory)
$game = Split-Path $ScriptsDirectory -Parent
if (!(Test-Path -LiteralPath (Join-Path $game 'GTA5.exe') -PathType Leaf)) {
    throw 'Run this installer from GTA V\scripts\ReloaderPlugins\Plugins\Crash\Tools.'
}
$root = Join-Path $ScriptsDirectory 'ReloaderPlugins\Plugins\Crash\Data'
$dumpFolder = Join-Path $root 'CrashDumps\SilentExit'
$backup = Join-Path $root 'SilentExitRegistryBackup.json'
$flagKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\GTA5.exe'
$exitKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\GTA5.exe'
$flag = 0x200
$names = @('ReportingMode','LocalDumpFolder','MaxNumberOfDumpFiles','DumpType')

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),
        '-State',$State,'-ScriptsDirectory',('"' + $ScriptsDirectory + '"'))
    $process = Start-Process powershell.exe -ArgumentList $args -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Silent exit setup failed (exit $($process.ExitCode))." }
    return
}

function Read-Value([string]$Key,[string]$Name) {
    if (!(Test-Path -LiteralPath $Key)) { return @{Exists=$false} }
    $item = Get-Item -LiteralPath $Key
    if ($item.GetValueNames() -notcontains $Name) { return @{Exists=$false} }
    return @{Exists=$true; Value=$item.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);
        Kind=$item.GetValueKind($Name).ToString()}
}
function Restore-Value([string]$Key,[string]$Name,$Record) {
    if ($Record.Exists) {
        [void](New-Item -Path $Key -Force)
        New-ItemProperty -LiteralPath $Key -Name $Name -Value $Record.Value -PropertyType $Record.Kind -Force | Out-Null
    } elseif (Test-Path -LiteralPath $Key) {
        Remove-ItemProperty -LiteralPath $Key -Name $Name -ErrorAction SilentlyContinue
    }
}

if ($State -eq 'On') {
    [void](New-Item -ItemType Directory -Path $dumpFolder -Force)
    if (!(Test-Path -LiteralPath $backup)) {
        $original = @{FlagKeyExists=(Test-Path -LiteralPath $flagKey); ExitKeyExists=(Test-Path -LiteralPath $exitKey)
            GlobalFlag=(Read-Value $flagKey 'GlobalFlag'); Values=@{}}
        foreach ($name in $names) { $original.Values[$name] = Read-Value $exitKey $name }
        $original | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $backup -Encoding UTF8
    }
    [void](New-Item -Path $flagKey -Force)
    [void](New-Item -Path $exitKey -Force)
    $current = Read-Value $flagKey 'GlobalFlag'
    $bits = if ($current.Exists) { [int]$current.Value } else { 0 }
    New-ItemProperty -LiteralPath $flagKey -Name GlobalFlag -Value ($bits -bor $flag) -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $exitKey -Name ReportingMode -Value 2 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $exitKey -Name LocalDumpFolder -Value $dumpFolder -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $exitKey -Name MaxNumberOfDumpFiles -Value 3 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $exitKey -Name DumpType -Value 0x1020 -PropertyType DWord -Force | Out-Null
    $actual = Get-ItemProperty -LiteralPath $exitKey
    $actualFlag = (Get-ItemProperty -LiteralPath $flagKey).GlobalFlag
    if (($actualFlag -band $flag) -eq 0 -or $actual.ReportingMode -ne 2 -or
        $actual.LocalDumpFolder -ne $dumpFolder -or $actual.MaxNumberOfDumpFiles -ne 3 -or
        $actual.DumpType -ne 0x1020) { throw 'Silent exit registry verification failed.' }
    Write-Host "Silent exit capture enabled for GTA5.exe: $dumpFolder"
} else {
    if (!(Test-Path -LiteralPath $backup)) { throw "Registry backup missing: $backup. Nothing changed." }
    $original = Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
    Restore-Value $flagKey 'GlobalFlag' $original.GlobalFlag
    foreach ($name in $names) { Restore-Value $exitKey $name $original.Values.$name }
    if (!$original.FlagKeyExists -and (Test-Path -LiteralPath $flagKey) -and
        (Get-Item -LiteralPath $flagKey).GetValueNames().Count -eq 0) { Remove-Item -LiteralPath $flagKey }
    if (!$original.ExitKeyExists -and (Test-Path -LiteralPath $exitKey) -and
        (Get-Item -LiteralPath $exitKey).GetValueNames().Count -eq 0) { Remove-Item -LiteralPath $exitKey }
    $checks = @(@{Key=$flagKey; Name='GlobalFlag'; Original=$original.GlobalFlag})
    foreach ($name in $names) { $checks += @{Key=$exitKey; Name=$name; Original=$original.Values.$name} }
    foreach ($check in $checks) {
        $restored = Read-Value $check.Key $check.Name
        if ([bool]$restored.Exists -ne [bool]$check.Original.Exists -or
            ($restored.Exists -and ($restored.Value -ne $check.Original.Value -or
                $restored.Kind -ne $check.Original.Kind))) {
            throw "Silent exit restore verification failed for $($check.Name); registry backup retained."
        }
    }
    Remove-Item -LiteralPath $backup
    Write-Host 'Silent exit capture disabled. Existing dumps were retained.'
}
