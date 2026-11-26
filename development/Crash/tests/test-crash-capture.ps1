[CmdletBinding()]
param([switch]$Simulated)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent) -Parent) -Parent
$settings = Get-Content -LiteralPath (Join-Path $repo 'development/config/modpack.local.json') -Raw | ConvertFrom-Json
$tool = Join-Path $settings.GameDirectory 'scripts\ReloaderPlugins\Plugins\_System\Diagnostics\Data\Crash\Tools\procdump64.exe'
if (!$Simulated -and !(Test-Path -LiteralPath $tool)) { throw 'Enable CrashCapture first to install signed ProcDump.' }
$watcher = Join-Path $repo 'development/Crash\src\bin\Release\net48\CrashWatcher.exe'
if (!(Test-Path -LiteralPath $watcher)) { throw 'Run modpack.cmd Check first.' }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('CrashCaptureTest-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $temp 'scripts\ReloaderPlugins\Plugins\_System\Diagnostics\Data\Crash'
$probe = Join-Path $temp 'CrashCaptureProbe.exe'
[void][IO.Directory]::CreateDirectory((Join-Path $root 'Tools'))
try {
    if ($Simulated) {
        $fake = @'
using System;
using System.Diagnostics;
using System.IO;
public static class FakeProcDump {
    public static int Main(string[] args) {
        int pid = int.Parse(args[args.Length - 2]);
        string folder = args[args.Length - 1];
        Console.WriteLine("Attached to PID " + pid);
        using (var process = Process.GetProcessById(pid)) process.WaitForExit();
        File.WriteAllText(Path.Combine(folder, "synthetic.dmp"), "test only");
        Console.WriteLine("Termination dump written");
        return 0;
    }
}
'@
        Add-Type -TypeDefinition $fake -OutputAssembly (Join-Path $root 'Tools\procdump64.exe') `
            -OutputType ConsoleApplication
    } else {
        Copy-Item -LiteralPath $tool -Destination (Join-Path $root 'Tools\procdump64.exe')
    }
    [IO.File]::WriteAllText((Join-Path $root 'CrashCapture.enabled'), 'test')
    $code = @'
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
public static class CrashCaptureProbe {
    public static int Main(string[] args) {
        Thread.Sleep(3000);
        if (args[0] == "normal") return 0;
        if (args[0] == "exit") return 5;
        if (args[0] == "exception") throw new InvalidOperationException("CrashCapture test exception");
        var form = new Form { Text = "CrashCapture test", Size = new Size(80, 80),
            StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000), ShowInTaskbar = false };
        form.Shown += (sender, e) => { Thread.Sleep(25000); form.Close(); };
        Application.Run(form);
        return 0;
    }
}
'@
    Add-Type -TypeDefinition $code -ReferencedAssemblies 'System.Windows.Forms','System.Drawing' `
        -OutputAssembly $probe -OutputType ConsoleApplication

    $modes = if ($Simulated) { @('normal','exit','normal','exit') } else { @('normal','exit','exception','hang') }
    foreach ($mode in $modes) {
        $target = Start-Process -FilePath $probe -ArgumentList $mode -PassThru -WindowStyle Hidden
        $context = Join-Path $root ('CrashSession-' + $target.Id + '.log')
        [IO.File]::WriteAllText($context, 'isolated capture test')
        $session = $target.Id.ToString() + '-' + [DateTime]::UtcNow.Ticks.ToString()
        $arguments = @('--pid',$target.Id,'--game-dir',$temp,'--scripts-dir',(Join-Path $temp 'scripts'),
            '--session-id',$session,'--session-start',[DateTime]::UtcNow.ToString('O'),'--context',$context)
        $monitor = Start-Process -FilePath $watcher -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (!$monitor.WaitForExit(90000)) { throw "Watcher timed out: $mode" }
        $folder = Join-Path $root ('CrashDumps\Capture-' + $session)
        $dumps = @(Get-ChildItem -LiteralPath $folder -Filter '*.dmp' -File -ErrorAction SilentlyContinue)
        if (!$dumps.Count) { throw "No dump captured for $mode. $(Get-Content (Join-Path $folder 'ProcDump.log') -ErrorAction SilentlyContinue | Out-String)" }
        $report = Join-Path $folder 'CrashReport.txt'
        if (!(Test-Path -LiteralPath $report)) { throw "Missing report for $mode" }
        if ($mode -eq 'normal' -and !(Select-String -LiteralPath (Join-Path $root 'CrashMonitor.log') `
            -SimpleMatch "Normal GTA shutdown. PID=$($target.Id)")) {
            throw 'Normal exit was classified as a crash.'
        }
        if (!$Simulated -and $mode -eq 'hang' -and
            !(Select-String -LiteralPath (Join-Path $folder 'ProcDump.log') -Pattern 'Hung Window')) {
            throw 'Hung-window trigger did not fire.'
        }
        Write-Host "Capture $mode passed: $($dumps.Count) dump(s)."
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $root 'CrashDumps') -Directory -Filter 'Capture-*').Count -ne 3) {
        throw 'Capture retention did not keep exactly three sessions.'
    }
    Write-Host "Crash capture $(if ($Simulated) { 'plumbing' } else { 'real ProcDump' }) and three-session retention passed."
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\CrashCaptureTest-'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
