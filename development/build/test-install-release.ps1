$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$package = Join-Path $repo 'development/.build/package-check-install'
$manifest = Get-Content -LiteralPath (Join-Path $package 'InstallManifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ModPackInstallTests-' + [Guid]::NewGuid().ToString('N'))
function Require($condition, $message) { if (!$condition) { throw $message }; Write-Host "PASS: $message" }
try {
    $game = Join-Path $temp 'game'
    $scripts = Join-Path $game 'scripts'
    $plugins = Join-Path $scripts 'ReloaderPlugins/Plugins'
    $backup = Join-Path $temp 'backups'
    [IO.Directory]::CreateDirectory($plugins) | Out-Null
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-ModPack.ps1') -GameDirectory $game -BackupDirectory $backup | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Fresh installation succeeds'
    foreach ($entry in $manifest.files) {
        $target = Join-Path $scripts ([string]$entry.path).Replace('/','\')
        Require ((Test-Path -LiteralPath $target -PathType Leaf) -and
            (Get-FileHash -LiteralPath $target).Hash -eq [string]$entry.sha256) "Installed: $($entry.path)"
    }
    $old = Join-Path $plugins ([string]$manifest.legacyFiles[0])
    $other = Join-Path $plugins ([string]$manifest.legacyFiles[1])
    [IO.File]::WriteAllText($old,'old mod source')
    [IO.File]::WriteAllText($other,'old mod artifact')
    $data = Join-Path $scripts 'ReloaderPlugins/RainbowPaint/RainbowPaintSettings.json'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $data)) | Out-Null
    [IO.File]::WriteAllText($data,'saved settings')
    $loader = Join-Path $scripts 'Reloader.dll'
    [IO.File]::WriteAllText($loader,'old loader')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-ModPack.ps1') -GameDirectory $game -BackupDirectory $backup | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Legacy installation succeeds'
    Require (!(Test-Path -LiteralPath $old) -and !(Test-Path -LiteralPath $other)) 'Only known legacy files removed'
    Require ([IO.File]::ReadAllText($data) -eq 'saved settings') 'User settings untouched'
    $backups = @(Get-ChildItem -LiteralPath $backup -Directory | Sort-Object LastWriteTime -Descending)
    $recent = $backups[0].FullName
    Require ([IO.File]::ReadAllText((Join-Path $recent 'Reloader.dll')) -eq 'old loader') 'Previous loader backed up'
    Require ([IO.File]::ReadAllText((Join-Path $recent ('ReloaderPlugins/Plugins/' + [IO.Path]::GetFileName($old)))) -eq 'old mod source') 'Legacy source backed up'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-ModPack.ps1') -GameDirectory $game -BackupDirectory $backup | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Repeated installation succeeds'
    $unknown = Join-Path $plugins 'ThirdParty.cs'
    [IO.File]::WriteAllText($unknown,'third-party fixture')
    $before = (Get-FileHash -LiteralPath $loader).Hash
    $ErrorActionPreference = 'Continue'
    try {
        $errorText = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Install-ModPack.ps1') -GameDirectory $game -BackupDirectory $backup 2>&1
        $failedCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = 'Stop' }
    Require ($failedCode -ne 0 -and ($errorText -join ' ').Contains('Unknown root-level')) 'Unknown source blocks update'
    Require ((Get-FileHash -LiteralPath $loader).Hash -eq $before -and (Test-Path -LiteralPath $unknown)) 'Blocked update changes nothing'
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackInstallTests-'
    if (!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
