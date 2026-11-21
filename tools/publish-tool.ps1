function Publish-ModPackTool {
    param([hashtable]$Files, [string]$Destination)
    if (!$Files -or !$Destination) { throw 'Files and Destination are required.' }
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('ModPackPublish-' + [Guid]::NewGuid().ToString('N'))
    $backups = @{}
    $touched = @()
    try {
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        foreach ($name in $Files.Keys) {
            if ($name -ne [IO.Path]::GetFileName($name)) { throw "Invalid file name: $name" }
            $source = [string]$Files[$name]
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Build output missing: $source" }
            $staged = Join-Path $stage $name
            Copy-Item -LiteralPath $source -Destination $staged
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash) { throw "Staging hash mismatch: $name" }
            $target = Join-Path $Destination $name
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                try {
                    $handle = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                    $handle.Dispose()
                } catch { throw "Close the running program or file using $target, then build again." }
                $backups[$target] = ,([IO.File]::ReadAllBytes($target))
            } elseif (Test-Path -LiteralPath $target) { throw "Destination is not a file: $target" }
            else { $backups[$target] = $null }
        }
        [IO.Directory]::CreateDirectory($Destination) | Out-Null
        foreach ($name in $Files.Keys) {
            $target = Join-Path $Destination $name
            $touched += $target
            Copy-Item -LiteralPath (Join-Path $stage $name) -Destination $target -Force
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash) {
                throw "Delivery hash mismatch: $name"
            }
        }
        Write-Host "Ready: $Destination"
    } catch {
        $failure = $_
        foreach ($target in $touched) {
            if ($null -eq $backups[$target]) {
                if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
            } else { [IO.File]::WriteAllBytes($target, [byte[]]$backups[$target]) }
        }
        throw $failure
    } finally {
        $resolved = [IO.Path]::GetFullPath($stage)
        $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\ModPackPublish-'
        if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected staging path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
