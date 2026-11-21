$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent) -Parent) -Parent
dotnet build (Join-Path $repo 'modules\Core\tests\ModPackCoreTests.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw 'ModPack test build failed.' }
& (Join-Path $repo 'modules\Core\tests\bin\Debug\net48\ModPackCoreTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'ModPack tests failed.' }
