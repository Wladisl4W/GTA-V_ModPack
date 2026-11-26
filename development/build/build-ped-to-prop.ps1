$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'publish-tool.ps1')
$source = Join-Path $repo 'scripts\ReloaderPlugins\Tools\PedToProp\Files\Source'
$buildRoot = Join-Path $repo 'development\.build\tool-build\PedToProp'
$buildRootArg = $buildRoot.Replace('\', '/')
dotnet build (Join-Path $source 'PedToProp.csproj') -c Release --nologo "-p:BaseOutputPath=$buildRootArg/bin/" "-p:BaseIntermediateOutputPath=$buildRootArg/obj/"
if ($LASTEXITCODE -ne 0) { throw 'PedToProp build failed. Previous delivery retained.' }
$files = @{
    'PedToProp.exe' = Join-Path $buildRoot 'bin\Release\net48\PedToProp.exe'
    'convert_blender.py' = Join-Path $source 'convert_blender.py'
    'setup.ps1' = Join-Path $source 'setup.ps1'
}
Publish-ModPackTool -Files $files -Destination (Join-Path $repo 'scripts\ReloaderPlugins\Tools\PedToProp\Files\Runtime')
