param([switch]$UsePrebuiltLoader, [string]$Version = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'
$plugins = Join-Path $dist 'ReloaderPlugins/Plugins'
$releases = Join-Path $dist 'releases'
$manifest = Get-Content -LiteralPath (Join-Path $repo 'catalog/modules.json') -Raw -Encoding UTF8 | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'build-project-map.ps1') -Check
if (!$?) { throw 'Catalog check failed.' }
& (Join-Path $PSScriptRoot 'stage-plugins.ps1') -Repository $repo
if (!$?) { throw 'Plugin staging failed.' }
[IO.Directory]::CreateDirectory($plugins) | Out-Null
[IO.Directory]::CreateDirectory($releases) | Out-Null
$expected = @{}
foreach ($module in $manifest.modules) {
    foreach ($name in $module.sources) {
        $relative = "$($module.id)/Code/$name"
        $source = Join-Path $repo "modules/$($module.id)/src/Code/$name"
        $target = Join-Path $plugins $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
        $expected[$relative] = $source
    }
}
$json = Join-Path $repo 'modules/Core/assets/Newtonsoft.Json.dll'
$jsonTarget = Join-Path $plugins 'Newtonsoft.Json.dll'
Copy-Item -LiteralPath $json -Destination $jsonTarget -Force
$expected['Newtonsoft.Json.dll'] = $json
dotnet build (Join-Path $repo 'modules/Crash/src/CrashWatcher.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'CrashWatcher build failed.' }
$crashSource = Join-Path $repo 'modules/Crash/src'
$crashFiles = @('CrashWatcher.exe','CrashWatcher.exe.config','ConfigureCrashDumps.ps1','InstallCrashDumps.cmd',
    'ConfigureCrashCapture.ps1','InstallCrashCapture.cmd','DisableCrashCapture.cmd',
    'ConfigureSilentExit.ps1','InstallSilentExit.cmd','DisableSilentExit.cmd')
foreach ($name in $crashFiles) {
    $source = if ($name -like 'CrashWatcher.exe*') { Join-Path $crashSource "bin/Release/net48/$name" } else { Join-Path $crashSource $name }
    $target = Join-Path $plugins "Crash/Tools/$name"
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    $expected["Crash/Tools/$name"] = $source
}
foreach ($file in Get-ChildItem -LiteralPath $plugins -File -Recurse) {
    $relative = $file.FullName.Substring($plugins.Length + 1).Replace('\','/')
    if (!$expected.ContainsKey($relative)) { throw "Unexpected package file: $relative" }
}
foreach ($relative in $expected.Keys) {
    $target = Join-Path $plugins $relative
    if ((Get-FileHash -LiteralPath $expected[$relative]).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        throw "Package hash mismatch: $relative"
    }
}
if ($UsePrebuiltLoader) {
    $loader = Join-Path $repo 'modules/Loader/assets/Reloader.dll'
    $declared = ([xml](Get-Content -LiteralPath (Join-Path $repo 'modules/Loader/src/Reloader.csproj') -Raw)).Project.PropertyGroup.FileVersion
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($loader).FileVersion
    if ($actualVersion -ne $declared) { throw "Prebuilt loader version $actualVersion differs from source project $declared" }
} else {
    dotnet build (Join-Path $repo 'modules/Loader/src/Reloader.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Reloader build failed.' }
    $loader = Join-Path $repo 'modules/Loader/src/bin/Release/net48/Reloader.dll'
}
Copy-Item -LiteralPath $loader -Destination (Join-Path $dist 'Reloader.dll') -Force
foreach ($tool in @('PedImporter','PedToProp')) {
    & (Join-Path $PSScriptRoot "build-$($tool.ToLowerInvariant() -replace 'pedimporter','ped-importer' -replace 'pedtoprop','ped-to-prop').ps1")
    if (!$?) { throw "$tool build failed." }
}
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'modules/Loader/src/Reloader.csproj') -Raw)).Project.PropertyGroup.FileVersion }
if ($Version -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') { throw "Invalid release version: $Version" }
$version = $Version
$pluginZip = Join-Path $releases "ModPack-Plugins-$version.zip"
if (Test-Path -LiteralPath $pluginZip) { Remove-Item -LiteralPath $pluginZip }
Compress-Archive -Path (Join-Path $plugins '*') -DestinationPath $pluginZip
$expanded = Join-Path $dist 'package-check'
if (Test-Path -LiteralPath $expanded) { Remove-Item -LiteralPath $expanded -Recurse -Force }
Expand-Archive -LiteralPath $pluginZip -DestinationPath $expanded
$actual = @(Get-ChildItem -LiteralPath $expanded -File -Recurse)
if ($actual.Count -ne $expected.Count) { throw "ZIP file count mismatch: $($actual.Count) vs $($expected.Count)" }
foreach ($file in $actual) {
    $relative = $file.FullName.Substring($expanded.Length + 1).Replace('\','/')
    if (!$expected.ContainsKey($relative) -or
        (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $expected[$relative]).Hash) {
        throw "ZIP content mismatch: $relative"
    }
}
$loaderZip = Join-Path $releases "Reloader-$version.zip"
if (Test-Path -LiteralPath $loaderZip) { Remove-Item -LiteralPath $loaderZip }
Compress-Archive -LiteralPath (Join-Path $dist 'Reloader.dll') -DestinationPath $loaderZip
foreach ($tool in @('PedImporter','PedToProp')) {
    $toolRoot = Join-Path $dist "Tools/$tool"
    $toolZip = Join-Path $releases "$tool-$version.zip"
    if (Test-Path -LiteralPath $toolZip) { Remove-Item -LiteralPath $toolZip }
    Compress-Archive -Path (Join-Path $toolRoot '*') -DestinationPath $toolZip
    $toolExpanded = Join-Path $dist "package-check-$tool"
    if (Test-Path -LiteralPath $toolExpanded) { Remove-Item -LiteralPath $toolExpanded -Recurse -Force }
    Expand-Archive -LiteralPath $toolZip -DestinationPath $toolExpanded
    $built = @(Get-ChildItem -LiteralPath $toolRoot -File -Recurse)
    $zipped = @(Get-ChildItem -LiteralPath $toolExpanded -File -Recurse)
    if ($built.Count -ne $zipped.Count) { throw "$tool ZIP file count mismatch." }
    foreach ($file in $zipped) {
        $relative = $file.FullName.Substring($toolExpanded.Length + 1)
        $source = Join-Path $toolRoot $relative
        if (!(Test-Path -LiteralPath $source) -or
            (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "$tool ZIP content mismatch: $relative"
        }
    }
}
$bundle = Join-Path $dist 'install-bundle'
$unpackedBundle = Join-Path $dist 'package-check-install'
foreach ($directory in @($bundle,$unpackedBundle)) {
    $full = [IO.Path]::GetFullPath($directory)
    $prefix = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
    if (!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installer staging path.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
[IO.Directory]::CreateDirectory($bundle) | Out-Null
$installerFiles = @()
foreach ($entry in $expected.GetEnumerator()) {
    $relative = 'ReloaderPlugins/Plugins/' + $entry.Key.Replace('\','/')
    $target = Join-Path $bundle $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $plugins $entry.Key) -Destination $target
    $installerFiles += [pscustomobject]@{path=$relative;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
}
Copy-Item -LiteralPath (Join-Path $dist 'Reloader.dll') -Destination (Join-Path $bundle 'Reloader.dll')
$installerFiles += [pscustomobject]@{path='Reloader.dll';sha256=(Get-FileHash -LiteralPath (Join-Path $bundle 'Reloader.dll') -Algorithm SHA256).Hash}
$installerSource = Join-Path $repo 'tools/Install-ModPack.ps1'
Copy-Item -LiteralPath $installerSource -Destination (Join-Path $bundle 'Install-ModPack.ps1')
$oldFiles = Get-Content -LiteralPath (Join-Path $repo 'catalog/legacy-flat-v1.6.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($oldFiles.schemaVersion -ne 1 -or @($oldFiles.files).Count -lt 20) { throw 'Legacy manifest missing.' }
@{schemaVersion=1;files=@($installerFiles | Sort-Object path);legacyFiles=@($oldFiles.files)} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bundle 'InstallManifest.json') -Encoding UTF8
$installZip = Join-Path $releases "ModPack-Install-$version.zip"
if (Test-Path -LiteralPath $installZip) { Remove-Item -LiteralPath $installZip }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $installZip
Expand-Archive -LiteralPath $installZip -DestinationPath $unpackedBundle
$built = @(Get-ChildItem -LiteralPath $bundle -File -Recurse)
$zipped = @(Get-ChildItem -LiteralPath $unpackedBundle -File -Recurse)
if ($built.Count -ne $zipped.Count) { throw 'Installer ZIP file count mismatch.' }
foreach ($file in $built) {
    $relative = $file.FullName.Substring($bundle.Length + 1)
    $unpacked = Join-Path $unpackedBundle $relative
    if (!(Test-Path -LiteralPath $unpacked -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $unpacked).Hash) {
        throw "Installer ZIP content mismatch: $relative"
    }
}
Write-Host "Verified release: $($expected.Count) plugin files and five ZIP archives in $releases"
