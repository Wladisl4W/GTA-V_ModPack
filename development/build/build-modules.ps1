function Get-ModPackHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [pscustomobject]@{Hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')} }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($env:GITHUB_ACTIONS -eq 'true') {
    & (Join-Path $PSScriptRoot 'build-project-map.ps1') -Check
    if (!$?) { throw 'Catalog check failed.' }
    & (Join-Path $PSScriptRoot 'stage-plugins.ps1') -Repository $repo
    if (!$?) { throw 'Plugin staging failed.' }
    dotnet build (Join-Path $repo 'development/Crash/src/CrashWatcher.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CrashWatcher build failed.' }
    dotnet build (Join-Path $repo 'development/Loader/src/Reloader.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Reloader build failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'development/Loader/src/bin/Release/net48/Reloader.dll') `
        -Destination (Join-Path $repo 'scripts/Reloader.dll') -Force
} else {
    & (Join-Path $PSScriptRoot 'build-loader.cmd')
    if ($LASTEXITCODE -ne 0) { throw 'Reloader build failed.' }
}
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
& (Join-Path $PSScriptRoot 'build-ped-importer.ps1')
if (!$? -or !(Test-Path -LiteralPath (Join-Path $repo 'scripts/ReloaderPlugins/Tools/PedImporter/Files/Runtime/PedImporter.exe'))) {
    throw 'Ped Importer build failed.'
}
& (Join-Path $PSScriptRoot 'build-ped-to-prop.ps1')
if (!$? -or !(Test-Path -LiteralPath (Join-Path $repo 'scripts/ReloaderPlugins/Tools/PedToProp/Files/Runtime/PedToProp.exe'))) {
    throw 'PedToProp build failed.'
}

$source = Join-Path $repo 'development/Crash/src'
$target = Join-Path $repo 'scripts/ReloaderPlugins/Plugins/_System/Diagnostics/Tools'
[IO.Directory]::CreateDirectory($target) | Out-Null
$files = @('CrashWatcher.exe','CrashWatcher.exe.config','ConfigureCrashDumps.ps1','InstallCrashDumps.cmd',
    'ConfigureCrashCapture.ps1','InstallCrashCapture.cmd','DisableCrashCapture.cmd',
    'ConfigureSilentExit.ps1','InstallSilentExit.cmd','DisableSilentExit.cmd')
foreach ($name in $files) {
    $from = if ($name -like 'CrashWatcher.exe*') { Join-Path $source "bin/Release/net48/$name" } else { Join-Path $source $name }
    $to = Join-Path $target $name
    if (!(Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing CrashWatcher file: $from" }
    Copy-Item -LiteralPath $from -Destination $to -Force
    if ((Get-ModPackHash -Path $from).Hash -ne (Get-ModPackHash -Path $to).Hash) { throw "Build copy differs: $to" }
}
Write-Host 'Ready: contents of scripts can be copied into GTA/scripts.'
