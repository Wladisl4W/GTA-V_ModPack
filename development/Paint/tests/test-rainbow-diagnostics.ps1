$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent) -Parent) -Parent
$settings = Get-Content -LiteralPath (Join-Path $repo 'modpack.local.json') -Raw | ConvertFrom-Json
$scripts = Join-Path $settings.GameDirectory 'scripts'
[void][Reflection.Assembly]::LoadFrom((Join-Path $settings.GameDirectory 'ScriptHookVDotNet3.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $scripts 'LemonUI.SHVDN3.dll'))
Add-Type -Path (Join-Path $repo 'development\Loader\src\PluginCompiler.cs') -ReferencedAssemblies System.Core,Microsoft.CSharp
$fingerprint = ''
& (Join-Path $repo 'tools\stage-plugins.ps1') -Repository $repo
$result = [PluginCompiler]::Compile((Join-Path $repo 'dist\SourcePlugins'),$scripts,
    (Join-Path $settings.GameDirectory 'ScriptHookVDotNet3.dll'),
    (Join-Path $scripts 'LemonUI.SHVDN3.dll'),[ref]$fingerprint)
if ($result.Errors.HasErrors) { throw ($result.Errors | Out-String) }
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$normalize = $result.CompiledAssembly.GetType('ModPack.Paint.PaintPlugin').GetMethod('NormalizePlateText',$flags)
$cases = @(
    @{Input=$null; Expected=''},
    @{Input='  ab12-c  '; Expected='AB12-C'},
    @{Input='123456789'; Expected='12345678'},
    @{Input='A~r~B'; Expected='ARB'},
    @{Input=('A' + [char]0 + 'B'); Expected='AB'},
    @{Input=('A' + [char]0x0411 + 'B'); Expected='AB'},
    @{Input='A B C D E'; Expected='A B C D '}
)
foreach ($case in $cases) {
    $actual = $normalize.Invoke($null,[object[]]@($case.Input))
    if ($actual -cne $case.Expected) { throw "Plate normalization failed: '$actual' != '$($case.Expected)'" }
}
Write-Host 'Plate length, ASCII filtering, whitespace and uppercase tests passed.'
$gate = $result.CompiledAssembly.GetType('ModPack.Core.MenuTextInput')
$public = [Reflection.BindingFlags]'Static,Public'
$begin = $gate.GetMethod('Begin',$public)
$release = $gate.GetMethod('End',$public)
$active = $gate.GetProperty('Active',$public)
$owner = New-Object object
$foreign = New-Object object
if (!$begin.Invoke($null,@($owner)) -or $begin.Invoke($null,@($foreign))) { throw 'Input ownership conflict failed.' }
$release.Invoke($null,@($foreign))
if (!$active.GetValue($null,$null)) { throw 'Foreign owner released input.' }
$release.Invoke($null,@($owner))
$release.Invoke($null,@($owner))
if ($active.GetValue($null,$null)) { throw 'Input release failed.' }
Write-Host 'Input ownership, foreign release and repeated cleanup tests passed.'

$watcher = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'development/Crash\src\bin\Release\net48\CrashWatcher.exe'))
$type = $watcher.GetType('ModPack.Crash.Program')
$find = $type.GetMethod('FindSessionDumps',$flags)
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ModPackPlateTests-' + [Guid]::NewGuid().ToString('N'))
$dumpRoot = Join-Path $temp 'CrashDumps\SilentExit\GTA5.exe-(PID-7868)-12345'
[void][IO.Directory]::CreateDirectory($dumpRoot)
try {
    $start = [DateTime]::UtcNow.AddMinutes(-5)
    $end = [DateTime]::UtcNow
    $own = Join-Path $dumpRoot 'GTA5.exe-(PID-7868).dmp'
    $wrong = Join-Path $dumpRoot 'GTA5.exe-(PID-78680).dmp'
    $other = Join-Path $dumpRoot 'svchost.exe-(PID-7868).dmp'
    $old = Join-Path $temp 'CrashDumps\GTA5.exe.7868.dmp'
    foreach ($path in @($own,$wrong,$other,$old)) { [IO.File]::WriteAllText($path,'filename test fixture') }
    [IO.File]::SetLastWriteTimeUtc($old,$start.AddDays(-1))
    $arguments = New-Object object[] 6
    $arguments[0] = [string]$temp
    $arguments[1] = [int]7868
    $arguments[2] = [DateTime]$start
    $arguments[3] = [DateTime]$end
    $arguments[4] = $null
    $arguments[5] = [string](Join-Path $temp 'monitor.log')
    $matches = $find.Invoke($null,$arguments)
    if ($matches.Count -ne 1 -or $matches[0] -ne $own) { throw 'Session dump matching failed.' }
    if (!(Test-Path -LiteralPath $old)) { throw 'Old dump was unexpectedly removed.' }
    Write-Host 'Real Silent Process Exit filename, PID isolation and session-time tests passed.'
} finally {
    $absolute = [IO.Path]::GetFullPath($temp)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackPlateTests-'
    if (!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test folder.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
