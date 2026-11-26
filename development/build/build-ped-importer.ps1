$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'publish-tool.ps1')
$project = Join-Path $repo 'scripts\ReloaderPlugins\Tools\PedImporter\Files\Source\PedImporter.csproj'
$buildRoot = Join-Path $repo 'development\.build\tool-build\PedImporter'
$buildRootArg = $buildRoot.Replace('\', '/')
dotnet build $project -c Release --nologo "-p:BaseOutputPath=$buildRootArg/bin/" "-p:BaseIntermediateOutputPath=$buildRootArg/obj/"
if ($LASTEXITCODE -ne 0) { throw 'PedImporter build failed. Previous delivery retained.' }
$output = Join-Path $buildRoot 'bin\Release\net48'
$files = @{
    'PedImporter.exe' = Join-Path $output 'PedImporter.exe'
    'PedImporter.exe.config' = Join-Path $output 'PedImporter.exe.config'
}
Publish-ModPackTool -Files $files -Destination (Join-Path $repo 'scripts\ReloaderPlugins\Tools\PedImporter\Files\Runtime')
