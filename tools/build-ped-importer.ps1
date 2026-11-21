$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'publish-tool.ps1')
$project = Join-Path $repo 'development\PedImporter\src\PedImporter.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'PedImporter build failed. Previous delivery retained.' }
$output = Join-Path $repo 'development\PedImporter\src\bin\Release\net48'
$files = @{
    'PedImporter.exe' = Join-Path $output 'PedImporter.exe'
    'PedImporter.exe.config' = Join-Path $output 'PedImporter.exe.config'
    'README.md' = Join-Path $repo 'development\PedImporter\README_USER.md'
}
Publish-ModPackTool -Files $files -Destination (Join-Path $repo 'modules\ReloaderPlugins\Tools\PedImporter')
