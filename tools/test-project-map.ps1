$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-project-map.ps1') -Check
if (!$?) { throw 'Catalog validation failed.' }
& (Join-Path $PSScriptRoot 'build-project-map.ps1')
if (!$?) { throw 'Map generation failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $repo 'catalog/modules.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$html = Get-Content -LiteralPath (Join-Path $repo '.modpack-local/project-map.html') -Raw -Encoding UTF8
foreach ($module in $manifest.modules) {
    if (!(Test-Path -LiteralPath (Join-Path $repo "development/$($module.id)/README.md"))) {
        throw "Missing README: $($module.id)"
    }
    if (!$html.Contains('"id":"' + $module.id + '"')) { throw "Missing map entry: $($module.id)" }
}
if ($html.Contains('/*MODULE_DATA*/[]')) { throw 'Map still contains template data.' }
$ignored = & git -C $repo check-ignore '.modpack-local/project-map.html'
if ($LASTEXITCODE -ne 0 -or !$ignored) { throw 'Local map is not Git-ignored.' }
Write-Host "Project map test OK: $($manifest.modules.Count) modules."
