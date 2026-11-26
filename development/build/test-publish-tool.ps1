$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publish-tool.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('ModPackPublishTest-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$destination = Join-Path $root 'ready'
try {
    [IO.Directory]::CreateDirectory($source) | Out-Null
    $exe = Join-Path $source 'Tool.exe'
    $readme = Join-Path $source 'README.md'
    [IO.File]::WriteAllText($exe, 'version 1')
    [IO.File]::WriteAllText($readme, 'instructions 1')
    $files = @{'Tool.exe'=$exe; 'README.md'=$readme}
    Publish-ModPackTool -Files $files -Destination $destination
    Publish-ModPackTool -Files $files -Destination $destination
    if ([IO.File]::ReadAllText((Join-Path $destination 'Tool.exe')) -ne 'version 1') { throw 'Repeat delivery changed executable.' }
    [IO.File]::WriteAllText($exe, 'version 2')
    $handle = [IO.File]::Open((Join-Path $destination 'Tool.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $failed = $false
        try { Publish-ModPackTool -Files $files -Destination $destination } catch { $failed = $true }
        if (!$failed) { throw 'Locked executable accepted.' }
    } finally { $handle.Dispose() }
    if ([IO.File]::ReadAllText((Join-Path $destination 'Tool.exe')) -ne 'version 1' -or
        [IO.File]::ReadAllText((Join-Path $destination 'README.md')) -ne 'instructions 1') {
        throw 'Locked delivery changed previous files.'
    }
    Remove-Item -LiteralPath $readme
    $failed = $false
    try { Publish-ModPackTool -Files $files -Destination $destination } catch { $failed = $true }
    if (!$failed -or [IO.File]::ReadAllText((Join-Path $destination 'Tool.exe')) -ne 'version 1') {
        throw 'Missing build output changed previous delivery.'
    }
    [IO.File]::WriteAllText($readme, 'instructions 2')
    Publish-ModPackTool -Files $files -Destination $destination
    if ([IO.File]::ReadAllText((Join-Path $destination 'Tool.exe')) -ne 'version 2' -or
        [IO.File]::ReadAllText((Join-Path $destination 'README.md')) -ne 'instructions 2') {
        throw 'Updated delivery did not match sources.'
    }
    Write-Host 'PASS: repeat, locked executable, missing output, and complete update.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackPublishTest-'
    if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
