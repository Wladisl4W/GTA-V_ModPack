$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
$project = Join-Path $repo 'Source Code\CrashWatcher\CrashWatcher.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'CrashWatcher build failed.' }
$watcherExe = Join-Path $repo 'Source Code\CrashWatcher\bin\Release\net48\CrashWatcher.exe'

function Invoke-WatcherCase([int]$ExitCode, [bool]$ExpectReport) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('CrashWatcherTest-' + [Guid]::NewGuid().ToString('N'))
    $root = Join-Path $temp 'scripts\ReloaderPlugins\CrashLogger'
    [void][IO.Directory]::CreateDirectory($root)
    try {
        if ($ExpectReport) {
            $reportsRoot = Join-Path $root 'CrashReports'
            1..22 | ForEach-Object {
                $directory = Join-Path $reportsRoot ('old-' + $_.ToString('00'))
                [void][IO.Directory]::CreateDirectory($directory)
                [IO.Directory]::SetCreationTimeUtc($directory, [DateTime]::UtcNow.AddMinutes(-$_))
            }
        }
        $context = Join-Path $root 'CrashSession-test.log'
        [IO.File]::WriteAllText($context, 'isolated watcher test')
        $target = Start-Process cmd.exe -ArgumentList '/c',("ping 127.0.0.1 -n 3 > nul & exit /b $ExitCode") `
            -PassThru -WindowStyle Hidden
        $arguments = @('--pid',$target.Id,'--game-dir',$temp,'--scripts-dir',(Join-Path $temp 'scripts'),
            '--session-id','test-session','--session-start',[DateTime]::UtcNow.ToString('O'),'--context',$context)
        $watcher = Start-Process $watcherExe -ArgumentList $arguments -PassThru
        if (!$watcher.WaitForExit(20000)) { throw "Watcher timeout for exit code $ExitCode" }
        $reports = @(Get-ChildItem (Join-Path $root 'CrashReports') -Filter CrashReport.txt -Recurse -ErrorAction SilentlyContinue)
        if (($reports.Count -gt 0) -ne $ExpectReport) { throw "Report expectation failed for exit code $ExitCode" }
        if ($reports.Count -and !(Select-String -LiteralPath $reports[0].FullName -SimpleMatch "Exit code: $ExitCode")) {
            throw 'Crash report does not contain the target exit code.'
        }
        if ($ExpectReport -and @(Get-ChildItem (Join-Path $root 'CrashReports') -Directory).Count -ne 20) {
            throw 'Crash report retention did not keep exactly 20 directories.'
        }
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($temp)
        $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\CrashWatcherTest-'
        if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test directory.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}

Invoke-WatcherCase 0 $false
Invoke-WatcherCase 5 $true
Write-Host 'CrashWatcher normal-exit and unexpected-exit tests passed.'
