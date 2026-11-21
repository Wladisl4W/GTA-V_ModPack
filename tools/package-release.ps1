param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$modules = Join-Path $repo 'modules'
$dist = Join-Path $repo 'dist'
$releases = Join-Path $dist 'releases'
$expanded = Join-Path $dist 'package-check'

function Get-ModPackHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

if (!$Version) {
    $project = [xml][IO.File]::ReadAllText((Join-Path $repo 'development/Loader/src/Reloader.csproj'))
    $Version = [string]$project.Project.PropertyGroup.FileVersion
}
if ($Version -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') { throw "Invalid version: $Version" }
& (Join-Path $PSScriptRoot 'build-modules.ps1')
if (!$?) { throw 'Module build failed.' }

$files = @(Get-ChildItem -LiteralPath $modules -File -Recurse)
if (!$files.Count) { throw 'modules is empty.' }
foreach ($file in $files) {
    if ($file.FullName -match '[\\/]Data[\\/]' -or $file.Name -match '\.(?:log|bak|dmp)$') {
        throw "User data or diagnostics found in modules: $($file.FullName)"
    }
}
[IO.Directory]::CreateDirectory($releases) | Out-Null
$archive = Join-Path $releases "ModPack-$Version.zip"
$distFull = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
foreach ($path in @($archive,$expanded)) {
    if (!([IO.Path]::GetFullPath($path)).StartsWith($distFull,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe output path: $path"
    }
}
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
if (Test-Path -LiteralPath $expanded) { Remove-Item -LiteralPath $expanded -Recurse -Force }
Compress-Archive -Path (Join-Path $modules '*') -DestinationPath $archive
Expand-Archive -LiteralPath $archive -DestinationPath $expanded
$unpacked = @(Get-ChildItem -LiteralPath $expanded -File -Recurse)
if ($unpacked.Count -ne $files.Count) { throw 'Release file count differs after extraction.' }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($modules.Length + 1)
    $copy = Join-Path $expanded $relative
    if (!(Test-Path -LiteralPath $copy -PathType Leaf) -or
        (Get-ModPackHash $file.FullName) -ne (Get-ModPackHash $copy)) {
        throw "Release content differs: $relative"
    }
}
Write-Host "Verified: $archive ($($files.Count) files)."
