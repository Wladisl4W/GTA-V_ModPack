$ErrorActionPreference = 'Stop'
$repo = (Split-Path (Split-Path (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent) -Parent) -Parent)
$buildRoot = Join-Path $repo 'development\.build\tool-build\PedImporterWorker'
$output = Join-Path $buildRoot 'bin\Release\net48'
$buildRootArg = $buildRoot.Replace('\', '/')
# Same source, with an asInvoker manifest for a non-elevated pipe-only test.
dotnet build (Join-Path $repo 'scripts\ReloaderPlugins\Tools\PedImporter\Files\Source\PedImporter.csproj') -c Release --nologo '-p:ApplicationManifest=' '-p:AssemblyName=PedImporter.WorkerSmoke' "-p:BaseOutputPath=$buildRootArg/bin/" "-p:BaseIntermediateOutputPath=$buildRootArg/obj/"
if ($LASTEXITCODE -ne 0) { throw 'Worker test build failed.' }
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $output 'PedImporter.WorkerSmoke.exe'
$missing = Join-Path $env:TEMP ('missing-ped-job-' + [guid]::NewGuid().ToString('N') + '.xml')
$start.Arguments = '--worker "' + $missing + '"'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.Encoding]::UTF8
$start.StandardErrorEncoding = [Text.Encoding]::UTF8
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
try {
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(10000)) { $process.Kill(); throw 'Worker startup hung.' }
    $out = $stdout.Result
    $err = $stderr.Result
    if ($process.ExitCode -ne 1) { throw 'Expected a clean error for a nonexistent job.' }
    if ($out -notmatch '\u0414\u0438\u0430\u0433\u043d\u043e\u0441\u0442\u0438\u043a\u0430:') { throw 'UTF-8 output missing or corrupted.' }
    if (!$err.Contains([IO.Path]::GetFileName($missing))) { throw 'Missing job error was not delivered.' }
    if ($err.Contains('set_OutputEncoding') -or $err.Contains([char]0xfffd)) { throw 'Console or encoding failure.' }
    Write-Host 'PASS: console-free WinExe worker startup, UTF-8 output and error delivery.'
} finally { $process.Dispose() }
