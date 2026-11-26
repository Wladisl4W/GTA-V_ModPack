$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent) -Parent
$config = Get-Content (Join-Path $repo 'development/config/modpack.local.json') -Raw | ConvertFrom-Json
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ModPackUpdateTests-' + [Guid]::NewGuid().ToString('N'))
$job = $null
function Require($value, $message) {
    if (!$value) { throw $message }
    Write-Host "PASS: $message"
}
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}
try {
    foreach ($folder in @('development/build','development/registry','development/config','scripts/ReloaderPlugins/Plugins/Optimization/Code/Cleanup','development\Loader\src','development/Crash\src',
        'development\.build\ReloaderPlugins\Plugins','game\scripts\ReloaderPlugins\Plugins')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $temp $folder))
    }
    Copy-Item (Join-Path $repo 'development\build\modpack.ps1') (Join-Path $temp 'development/build')
    Copy-Item (Join-Path $repo 'development\build\stage-plugins.ps1') (Join-Path $temp 'development/build')
    Copy-Item (Join-Path $repo 'development\Loader\src\PluginCompiler.cs') (Join-Path $temp 'development\Loader\src')
    Get-ChildItem -LiteralPath (Join-Path $repo 'development/Crash\src') -File |
        Copy-Item -Destination (Join-Path $temp 'development/Crash\src')
    Copy-Item (Join-Path $config.GameDirectory 'ScriptHookVDotNet3.dll') (Join-Path $temp 'game')
    Copy-Item (Join-Path $config.GameDirectory 'scripts\LemonUI.SHVDN3.dll') (Join-Path $temp 'game\scripts')
    Copy-Item (Join-Path $repo 'development\Loader\src\bin\Release\net48\Reloader.dll') (Join-Path $temp 'game\scripts')
    @{GameDirectory=(Join-Path $temp 'game')} | ConvertTo-Json | Set-Content (Join-Path $temp 'development/config/modpack.local.json')
    '{"schemaVersion":1,"modules":[{"id":"Cleanup","codeDirectory":"ReloaderPlugins/Plugins","sources":["Optimization/Code/Cleanup/Fixture.cs"]}]}' |
        Set-Content (Join-Path $temp 'development\registry\modules.json')
    $source = Join-Path $temp 'scripts/ReloaderPlugins/Plugins/Optimization/Code/Cleanup\Fixture.cs'
    $live = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\Optimization\Code\Cleanup\Fixture.cs'
    $ready = Join-Path $temp 'development\.build\ReloaderPlugins\Plugins\Optimization\Code\Cleanup\Fixture.cs'
    $log = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\_System\Diagnostics\Data\Loader\Logs\Reloader.log'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $log)) | Out-Null
    $command = Join-Path $temp 'development\build\modpack.ps1'
    [IO.File]::WriteAllText($source,'public class Fixture {}')
    $legacyWatcher = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\CrashWatcher.exe'
    [IO.File]::WriteAllText($legacyWatcher,'old watcher fixture')
    $check = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Check 2>&1
    Require ($LASTEXITCODE -eq 0) 'Check compiles without deploying'
    Require (!(Test-Path $live)) 'Check creates no live source'
    $fingerprint = [regex]::Match(($check -join ' '),'[A-F0-9]{64}').Value
    Require ($fingerprint.Length -eq 64) 'Check returns a source fingerprint'
    $job = Start-Job -ArgumentList $live,$log,$fingerprint -ScriptBlock {
        param($live,$log,$fingerprint)
        $end = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $end) {
            if ((Test-Path $live) -and !(Test-Path (Join-Path (Split-Path $live) '.deploy.lock'))) {
                [IO.File]::AppendAllText($log,"[test] Reload OK: $fingerprint" + [Environment]::NewLine)
                return
            }
            Start-Sleep -Milliseconds 100
        }
        throw 'Fixture update did not arrive.'
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Update -TimeoutSeconds 10
    Require ($LASTEXITCODE -eq 0) 'Update recognizes a fresh matching log confirmation'
    Receive-Job -Job $job -Wait -ErrorAction Stop
    Remove-Job $job
    $job = $null
    Require (((Get-Sha256 $source) -eq (Get-Sha256 $live)) -and
        ((Get-Sha256 $source) -eq (Get-Sha256 $ready))) 'Both copies match'
    $newWatcher = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\_System\Diagnostics\Tools\CrashWatcher.exe'
    Require ((Test-Path $newWatcher) -and !(Test-Path $legacyWatcher)) 'CrashWatcher moved from flat root'
    Require ((Get-Sha256 $newWatcher) -eq (Get-Sha256 (Join-Path $temp 'development\.build\ReloaderPlugins\Plugins\_System\Diagnostics\Tools\CrashWatcher.exe'))) 'CrashWatcher packaged copies match'
    [IO.File]::WriteAllText($source,'broken source')
    $ErrorActionPreference = 'Continue'
    $failure = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Update 2>&1
    $ErrorActionPreference = 'Stop'
    Require ($LASTEXITCODE -ne 0) 'Invalid source fails validation'
    Require ([IO.File]::ReadAllText($live) -eq 'public class Fixture {}') 'Invalid source is not deployed'
    [IO.File]::WriteAllText($source,'public class Fixture {}')
    [IO.File]::WriteAllText($live,'public class OlderFixture {}')
    $ErrorActionPreference = 'Continue'
    $timeout = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Update -TimeoutSeconds 1 2>&1
    $ErrorActionPreference = 'Stop'
    Require ($LASTEXITCODE -ne 0) 'Old matching log entry does not confirm a new update'
    Require (!(Test-Path (Join-Path (Split-Path $live) '.deploy.lock'))) 'Deployment lock is released'

    $nestedSource = $source
    $legacyLive = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\Fixture.cs'
    $legacyReady = Join-Path $temp 'development\.build\ReloaderPlugins\Plugins\Fixture.cs'
    Copy-Item -LiteralPath $source -Destination $legacyLive
    Copy-Item -LiteralPath $source -Destination $legacyReady
    $nestedLive = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\Optimization\Code\Cleanup\Fixture.cs'
    $nestedReady = Join-Path $temp 'development\.build\ReloaderPlugins\Plugins\Optimization\Code\Cleanup\Fixture.cs'
    $check = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Check 2>&1
    Require ($LASTEXITCODE -eq 0) 'Check compiles nested module source'
    $fingerprint = [regex]::Match(($check -join ' '),'[A-F0-9]{64}').Value
    $job = Start-Job -ArgumentList $nestedLive,$log,$fingerprint -ScriptBlock {
        param($nestedLive,$log,$fingerprint)
        $end = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $end) {
            if (Test-Path -LiteralPath $nestedLive) {
                $lock = Join-Path (Split-Path (Split-Path (Split-Path (Split-Path $nestedLive)))) '.deploy.lock'
                if (!(Test-Path -LiteralPath $lock)) {
                    [IO.File]::AppendAllText($log,"[test] Reload OK: $fingerprint" + [Environment]::NewLine)
                    return
                }
            }
            Start-Sleep -Milliseconds 100
        }
        throw 'Nested fixture update did not arrive.'
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Update -TimeoutSeconds 10 | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Update migrates nested module source'
    Receive-Job -Job $job -Wait -ErrorAction Stop
    Remove-Job $job
    $job = $null
    Require (!(Test-Path -LiteralPath $legacyLive) -and !(Test-Path -LiteralPath $legacyReady)) 'Legacy flat files removed after verified copy'
    Require (((Get-Sha256 $nestedSource) -eq (Get-Sha256 $nestedLive)) -and
        ((Get-Sha256 $nestedSource) -eq (Get-Sha256 $nestedReady))) 'Nested copies match source'

    $oldNestedLive = Join-Path (Split-Path $nestedLive) 'OldFixture.cs'
    $oldNestedReady = Join-Path (Split-Path $nestedReady) 'OldFixture.cs'
    [IO.File]::WriteAllText($oldNestedLive, 'public class OldFixture {}')
    [IO.File]::WriteAllText($oldNestedReady, 'public class OldFixture {}')
    '{"schemaVersion":1,"renames":{"Optimization/Code/Cleanup/OldFixture.cs":"Optimization/Code/Cleanup/Fixture.cs"}}' |
        Set-Content (Join-Path $temp 'development\registry\source-renames.json')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Update -TimeoutSeconds 10 | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Update accepts declared nested source rename'
    Require (!(Test-Path $oldNestedLive) -and !(Test-Path $oldNestedReady)) 'Old nested names removed from both destinations'

    [IO.File]::WriteAllText($nestedSource,'public class Fixture { public int Value; }')
    $check = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Check 2>&1
    Require ($LASTEXITCODE -eq 0) 'Check validates selective source change'
    $fingerprint = [regex]::Match(($check -join ' '),'[A-F0-9]{64}').Value
    $job = Start-Job -ArgumentList $nestedLive,$log,$fingerprint -ScriptBlock {
        param($nestedLive,$log,$fingerprint)
        $end = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $end) {
            $containsValue = $false
            try {
                if (Test-Path -LiteralPath $nestedLive) {
                    $containsValue = [IO.File]::ReadAllText($nestedLive).Contains('Value')
                }
            } catch [IO.IOException] {
                # The updater may still be replacing the file; retry on the next poll.
            }
            if ($containsValue -and
                !(Test-Path (Join-Path (Split-Path (Split-Path (Split-Path (Split-Path $nestedLive)))) '.deploy.lock'))) {
                [IO.File]::AppendAllText($log,"[test] Reload OK: $fingerprint" + [Environment]::NewLine)
                return
            }
            Start-Sleep -Milliseconds 100
        }
        throw 'Selective fixture update did not arrive.'
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Mode Update -Module Cleanup -TimeoutSeconds 10 | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Selective update succeeds'
    Receive-Job -Job $job -Wait -ErrorAction Stop
    Remove-Job $job
    $job = $null
    Require (((Get-Sha256 $nestedSource) -eq (Get-Sha256 $nestedLive)) -and
        ((Get-Sha256 $nestedSource) -eq (Get-Sha256 $nestedReady))) 'Selective update changes only selected source'
} finally {
    if ($null -ne $job) { Stop-Job $job; Remove-Job $job }
    $resolved = [IO.Path]::GetFullPath($temp)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackUpdateTests-'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
