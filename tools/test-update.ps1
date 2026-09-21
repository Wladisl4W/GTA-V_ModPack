$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
$config = Get-Content (Join-Path $repo 'modpack.local.json') -Raw | ConvertFrom-Json
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
    foreach ($folder in @('tools','Source Code\Plugins','Source Code\Reloader',
        'Ready To Use\ReloaderPlugins\Plugins','game\scripts\ReloaderPlugins\Plugins')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $temp $folder))
    }
    Copy-Item (Join-Path $repo 'tools\modpack.ps1') (Join-Path $temp 'tools')
    Copy-Item (Join-Path $repo 'Source Code\Reloader\PluginCompiler.cs') (Join-Path $temp 'Source Code\Reloader')
    Copy-Item (Join-Path $config.GameDirectory 'ScriptHookVDotNet3.dll') (Join-Path $temp 'game')
    Copy-Item (Join-Path $config.GameDirectory 'scripts\LemonUI.SHVDN3.dll') (Join-Path $temp 'game\scripts')
    Copy-Item (Join-Path $repo 'Source Code\Reloader\bin\Release\net48\Reloader.dll') (Join-Path $temp 'game\scripts')
    @{GameDirectory=(Join-Path $temp 'game')} | ConvertTo-Json | Set-Content (Join-Path $temp 'modpack.local.json')
    $source = Join-Path $temp 'Source Code\Plugins\Fixture.cs'
    $live = Join-Path $temp 'game\scripts\ReloaderPlugins\Plugins\Fixture.cs'
    $ready = Join-Path $temp 'Ready To Use\ReloaderPlugins\Plugins\Fixture.cs'
    $log = Join-Path $temp 'game\scripts\ReloaderPlugins\Reloader.log'
    $command = Join-Path $temp 'tools\modpack.ps1'
    [IO.File]::WriteAllText($source,'public class Fixture {}')
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
} finally {
    if ($null -ne $job) { Stop-Job $job; Remove-Job $job }
    $resolved = [IO.Path]::GetFullPath($temp)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackUpdateTests-'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
