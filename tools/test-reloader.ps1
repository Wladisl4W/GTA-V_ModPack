$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ModPackTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
    dotnet build (Join-Path $repo 'tools\tests\ReloaderTests.csproj') -o $temp --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & (Join-Path $temp 'ReloaderTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackTests-'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
