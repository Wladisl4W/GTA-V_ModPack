param([string]$Repository = (Split-Path -Parent $PSScriptRoot),
      [string]$Destination = (Join-Path $Repository 'dist/SourcePlugins'))
$ErrorActionPreference = 'Stop'
function Get-StageHash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$manifest = Get-Content -LiteralPath (Join-Path $Repository 'catalog/modules.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported catalog schema.' }
$expected = @{}
foreach ($module in $manifest.modules) {
    foreach ($name in $module.sources) {
        $relative = $name
        if ($expected.ContainsKey($relative)) { throw "Duplicate source: $relative" }
        $source = Join-Path $Repository "modules/$($module.codeDirectory)/$name"
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source: $source" }
        $expected[$relative] = $source
    }
}
$actual = @(Get-ChildItem -LiteralPath (Join-Path $Repository 'modules/ReloaderPlugins/Plugins') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -match '[\\/]Code[\\/]' })
if ($actual.Count -ne $expected.Count) { throw "Source count mismatch: $($actual.Count) vs $($expected.Count)" }
foreach ($file in $actual) {
    if (@($expected.Values) -notcontains $file.FullName) { throw "Unlisted plugin source: $($file.FullName)" }
}
[IO.Directory]::CreateDirectory($Destination) | Out-Null
foreach ($relative in $expected.Keys) {
    $target = Join-Path $Destination $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath $expected[$relative] -Destination $target -Force
    if ((Get-StageHash $expected[$relative]) -ne (Get-StageHash $target)) {
        throw "Staging mismatch: $relative"
    }
}
foreach ($file in Get-ChildItem -LiteralPath $Destination -Filter '*.cs' -File -Recurse) {
    $relative = $file.FullName.Substring($Destination.Length + 1) -replace '\\','/'
    if (!$expected.ContainsKey($relative)) {
        # Staging is generated output; old names must not survive a source rename.
        $file.Delete()
    }
}
Write-Host "Staged $($expected.Count) plugin sources."
