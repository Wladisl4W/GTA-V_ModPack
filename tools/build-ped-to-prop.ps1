$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'publish-tool.ps1')
$source = Join-Path $repo 'development\PedToProp\src'
dotnet build (Join-Path $source 'PedToProp.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'PedToProp build failed. Previous delivery retained.' }
$files = @{
    'PedToProp.exe' = Join-Path $source 'bin\Release\net48\PedToProp.exe'
    'convert_blender.py' = Join-Path $source 'convert_blender.py'
    'setup.ps1' = Join-Path $source 'setup.ps1'
    'README.md' = Join-Path $repo 'development\PedToProp\README_USER.md'
}
Publish-ModPackTool -Files $files -Destination (Join-Path $repo 'modules\ReloaderPlugins\Tools\PedToProp')
