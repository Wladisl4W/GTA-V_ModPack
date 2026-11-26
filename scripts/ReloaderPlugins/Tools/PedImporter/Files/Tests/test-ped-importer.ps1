$ErrorActionPreference = 'Stop'
$repo = (Split-Path (Split-Path (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent) -Parent) -Parent)
[void][Reflection.Assembly]::LoadFrom((Join-Path $repo 'scripts\ReloaderPlugins\Tools\PedImporter\Files\Runtime\PedImporter.exe'))
if ([ModPack.PedImporter.Catalog]::Hash('player_zero') -ne 225514697) { throw 'Hash mismatch' }
if (![ModPack.PedImporter.EditorAutomation]::IsRebuildSuccess('Rebuild completed successfully!')) { throw 'Success not recognized' }
foreach ($message in @('Rebuild failed', 'Rebuild not successful', 'Rebuild completed with errors', 'Completed', 'Unknown dialog')) {
    if ([ModPack.PedImporter.EditorAutomation]::IsRebuildSuccess($message)) { throw "False success: $message" }
}
$root = Join-Path $env:TEMP ('PedImporterTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
foreach ($ext in @('ydd','ytd','yft','ymt','extra')) { [IO.File]::WriteAllText((Join-Path $root "example.$ext"), 'test') }
New-Item -ItemType Directory -Path (Join-Path $root 'example') | Out-Null
$models = [ModPack.PedImporter.Catalog]::Scan($root)
if ($models.Count -ne 1 -or !$models[0].Valid -or !$models[0].Streamed -or $models[0].Files -ne 5) { throw 'Grouping failed' }
New-Item -ItemType Directory -Path (Join-Path $root 'menyooStuff') | Out-Null
[ModPack.PedImporter.Catalog]::UpdateFavoriteStatuses($root, $models)
if ($models[0].FavoriteStatus -ne 'Будет добавлен') { throw 'New favorite status missing' }
if ([ModPack.PedImporter.Catalog]::AddFavorites($root, $models) -ne 1) { throw 'Favorite insertion failed' }
[ModPack.PedImporter.Catalog]::UpdateFavoriteStatuses($root, $models)
if ($models[0].FavoriteStatus -ne 'Уже в избранном') { throw 'Existing favorite status missing' }
if ([ModPack.PedImporter.Catalog]::AddFavorites($root, $models) -ne 0) { throw 'Duplicate insertion' }
[IO.File]::WriteAllText((Join-Path $root 'menyooStuff\FavouritePeds.xml'), '<broken')
$rejected = $false
try { [ModPack.PedImporter.Catalog]::AddFavorites($root, $models) } catch { $rejected = $true }
if (!$rejected) { throw 'Invalid XML accepted' }

$realFavorites = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\menyooStuff\FavouritePeds.xml'
if (Test-Path -LiteralPath $realFavorites) {
    $copyRoot = Join-Path $env:TEMP ('PedImporterFavorites-' + [guid]::NewGuid().ToString('N'))
    $copyFolder = Join-Path $copyRoot 'menyooStuff'
    New-Item -ItemType Directory -Path $copyFolder -Force | Out-Null
    $copyPath = Join-Path $copyFolder 'FavouritePeds.xml'
    Copy-Item -LiteralPath $realFavorites -Destination $copyPath
    $original = [xml](Get-Content -LiteralPath $copyPath -Raw)
    $originalCount = @($original.FavouritePeds.Ped).Count
    $modelA = New-Object ModPack.PedImporter.PedModel
    $modelA.Name = 'codex_test_ped_a'; $modelA.Selected = $true; $modelA.Valid = $true
    $modelB = New-Object ModPack.PedImporter.PedModel
    $modelB.Name = 'codex_test_ped_b'; $modelB.Selected = $true; $modelB.Valid = $true
    $single = [System.Collections.Generic.List[ModPack.PedImporter.PedModel]]::new()
    $single.Add($modelA)
    $batch = [System.Collections.Generic.List[ModPack.PedImporter.PedModel]]::new()
    $batch.Add($modelA); $batch.Add($modelB)
    if ([ModPack.PedImporter.Catalog]::AddFavorites($copyRoot, $single) -ne 1) { throw 'Real XML copy: single insert failed' }
    if ([ModPack.PedImporter.Catalog]::AddFavorites($copyRoot, $batch) -ne 1) { throw 'Real XML copy: batch insert failed' }
    if ([ModPack.PedImporter.Catalog]::AddFavorites($copyRoot, $batch) -ne 0) { throw 'Real XML copy: repeat insert failed' }
    $updated = [xml](Get-Content -LiteralPath $copyPath -Raw)
    if (@($updated.FavouritePeds.Ped).Count -ne $originalCount + 2) { throw 'Real XML copy: existing entries changed' }
    if (@(Get-ChildItem -LiteralPath $copyFolder -Filter 'FavouritePeds.xml.*.bak').Count -ne 2) { throw 'Real XML copy: backups missing' }
    $beforeFailure = (Get-FileHash -LiteralPath $copyPath).Hash
    $lock = [IO.File]::Open($copyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $modelC = New-Object ModPack.PedImporter.PedModel
        $modelC.Name = 'codex_test_ped_c'; $modelC.Selected = $true; $modelC.Valid = $true
        $lockedBatch = [System.Collections.Generic.List[ModPack.PedImporter.PedModel]]::new()
        $lockedBatch.Add($modelC)
        $failed = $false
        try { [ModPack.PedImporter.Catalog]::AddFavorites($copyRoot, $lockedBatch) | Out-Null } catch { $failed = $true }
        if (!$failed) { throw 'Locked favorite file was replaced' }
    } finally { $lock.Dispose() }
    if ((Get-FileHash -LiteralPath $copyPath).Hash -ne $beforeFailure) { throw 'Failed write changed the original' }
}
Write-Host 'PASS: grouping, favorite status, real XML copy, batch, duplicates, backups, invalid XML and failed write.'
