[CmdletBinding()]
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$manifestPath = Join-Path $repo 'development/registry/modules.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported catalog schema.' }
$sourceRoot = Join-Path $repo 'scripts'
$seen = @{}
$sourceNames = @{}
$ids = @{}
foreach ($module in $manifest.modules) {
    if ($module.id -notmatch '^[A-Za-z][A-Za-z0-9]*$' -or $ids.ContainsKey($module.id)) {
        throw "Invalid or duplicate module ID: $($module.id)"
    }
    $ids[$module.id] = $true
    $codeDirectory = if ($module.codeDirectory) { [string]$module.codeDirectory } else { '' }
    if ($codeDirectory -and $codeDirectory -ne 'ReloaderPlugins/Plugins') {
        throw "Invalid code directory for $($module.id)"
    }
    foreach ($name in $module.sources) {
        if ($name -notmatch '^(?:[A-Za-z_][A-Za-z0-9_]*/)*[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*\.cs$' -or $sourceNames.ContainsKey($name)) {
            throw "Invalid or duplicate source: $name"
        }
        $sourceNames[$name] = $true
        $relative = if ($codeDirectory) { "$codeDirectory/$name" } else { $name }
        if (!(Test-Path -LiteralPath (Join-Path $sourceRoot $relative) -PathType Leaf)) {
            throw "Missing source: $name"
        }
        $seen[$relative.Replace('/', '\')] = $module.id
    }
}
$allSources = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'ReloaderPlugins/Plugins') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -match '[\\/]Code[\\/]' })
$unlisted = @($allSources | Where-Object {
    !$seen.ContainsKey($_.FullName.Substring($sourceRoot.Length + 1))
} | Select-Object -ExpandProperty FullName)
if ($unlisted.Count) { throw "Unlisted plugin sources: $($unlisted -join ', ')" }
if ($Check) { Write-Host "Catalog OK: $($manifest.modules.Count) modules, $($seen.Count) plugin sources."; return }

$moduleRoot = Join-Path $repo 'development'
foreach ($module in $manifest.modules) {
    $directory = if ($module.kind -eq 'tool') { Join-Path $repo "scripts/ReloaderPlugins/Tools/$($module.id)/Files" }
        elseif ($module.kind -eq 'loader' -or $module.id -eq 'Crash') { Join-Path $moduleRoot $module.id }
        else { Join-Path $repo 'docs/modules' }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $readme = if ($module.kind -eq 'tool') { Join-Path $directory 'DEVELOPMENT.md' }
    elseif ($module.kind -eq 'loader' -or $module.id -eq 'Crash') {
        Join-Path $directory 'README.md'
    } else { Join-Path $directory "$($module.id).md" }
    if (Test-Path -LiteralPath $readme) { continue }
    $sourceLines = if ($module.sources.Count) {
        @($module.sources | ForEach-Object {
            $relative = if ($module.codeDirectory) { "$($module.codeDirectory)/$_" } else { $_ }
            "- ``scripts/$relative``"
        }) -join "`n"
    } elseif ($module.kind -eq 'loader') { '- `development/Loader/src`' }
    elseif ($module.kind -eq 'tool') { "- ``scripts/ReloaderPlugins/Tools/$($module.id)/Files/Source``" }
    else { "- ``scripts/ReloaderPlugins/Plugins``" }
    $dataLines = if ($module.data.Count) {
        @($module.data | ForEach-Object { "- ``scripts/ReloaderPlugins/$_``" }) -join "`n"
    } else { '- Постоянных данных в `ReloaderPlugins` нет.' }
    $key = if ($module.key) { $module.key } else { 'Нет отдельной клавиши.' }
    $body = @"
# $($module.name) ($($module.id))

## Назначение

$($module.description)

## Управление

$key

## Исходники

$sourceLines

## Данные и журналы

$dataLines

## Зависимости

- Игровые плагины используют Reloader, ScriptHookVDotNet3 и LemonUI, если вызывают его меню.
- Для отдельного инструмента см. README рядом с исходниками.

## Проверка

- Для игрового плагина: ``modpack.cmd Check``.
- Игровое поведение проверяется отдельно после установки в GTA.

## Обновление и откат

- Перед переносом сверяйте исходники и резервную копию.
- Не заменяйте ``Reloader.dll`` при обновлении обычного плагина.
"@
    [IO.File]::WriteAllText($readme, $body + "`n", [Text.UTF8Encoding]::new($false))
}

$gameDirectory = $null
$configPath = Join-Path $repo 'development/config/modpack.local.json'
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $gameDirectory = $config.GameDirectory
}
$entries = @()
foreach ($module in $manifest.modules) {
    $sourceFiles = @($module.sources | ForEach-Object {
        $relative = if ($module.codeDirectory) { "$($module.codeDirectory)/$_" } else { $_ }
        $path = Join-Path $sourceRoot $relative
        [pscustomobject]@{ name = $_; path = ([Uri]$path).AbsoluteUri }
    })
    $liveFiles = @()
    if ($gameDirectory -and $module.sources.Count) {
        $liveRoot = Join-Path $gameDirectory 'scripts/ReloaderPlugins/Plugins'
        $liveFiles = @($module.sources | ForEach-Object {
            $relative = if ($module.codeDirectory) { "$($module.codeDirectory)/$_" } else { $_ }
            $path = Join-Path $liveRoot $_
            [pscustomobject]@{ name = $_; exists = (Test-Path -LiteralPath $path -PathType Leaf) }
        })
    }
    $entryReadme = if ($module.kind -eq 'tool') { Join-Path $repo "scripts/ReloaderPlugins/Tools/$($module.id)/Files/DEVELOPMENT.md" }
        elseif ($module.kind -eq 'loader' -or $module.id -eq 'Crash') { Join-Path $moduleRoot "$($module.id)/README.md" }
        else { Join-Path $repo "docs/modules/$($module.id).md" }
    $entries += [pscustomobject]@{
        id = $module.id; name = $module.name; kind = $module.kind
        key = $module.key; description = $module.description
        readme = ([Uri]$entryReadme).AbsoluteUri
        sources = $sourceFiles; live = $liveFiles; data = @($module.data)
    }
}
$json = ConvertTo-Json -InputObject $entries -Depth 8 -Compress
$json = $json.Replace('<', '\u003c').Replace('>', '\u003e').Replace('&', '\u0026')
$template = Get-Content -LiteralPath (Join-Path $repo 'development/registry/map.template.html') -Raw -Encoding UTF8
$html = $template.Replace('/*MODULE_DATA*/[]', $json)
$output = Join-Path $repo 'development/.local/project-map.html'
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
[IO.File]::WriteAllText($output, $html, [Text.UTF8Encoding]::new($false))
Write-Host "Catalog OK: $($entries.Count) modules, $($seen.Count) plugin sources."
Write-Host "Map: $output"
