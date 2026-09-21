$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
dotnet build (Join-Path $repo 'tools\tests\ModPackCoreTests.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw 'ModPack test build failed.' }
& (Join-Path $repo 'tools\tests\bin\Debug\net48\ModPackCoreTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'ModPack tests failed.' }
