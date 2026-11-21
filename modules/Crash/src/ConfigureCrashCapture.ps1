[CmdletBinding()]
param(
    [ValidateSet('On','Off')][string]$State = 'On',
    [string]$ScriptsDirectory = ''
)
$ErrorActionPreference = 'Stop'

if (!$ScriptsDirectory) {
    $ScriptsDirectory = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent
    $game = Split-Path $ScriptsDirectory -Parent
    if (!(Test-Path -LiteralPath (Join-Path $game 'GTA5.exe') -PathType Leaf)) {
        throw 'Run this installer from GTA V\scripts\ReloaderPlugins\Plugins\Crash\Tools.'
    }
}
$ScriptsDirectory = [IO.Path]::GetFullPath($ScriptsDirectory)
if (!(Test-Path -LiteralPath $ScriptsDirectory -PathType Container)) {
    throw "Scripts folder not found: $ScriptsDirectory"
}

$root = Join-Path $ScriptsDirectory 'ReloaderPlugins\Plugins\Crash\Data'
$toolFolder = Join-Path $root 'Tools'
$tool = Join-Path $toolFolder 'procdump64.exe'
$enabled = Join-Path $root 'CrashCapture.enabled'

if ($State -eq 'Off') {
    if (Test-Path -LiteralPath $enabled) { Remove-Item -LiteralPath $enabled }
    Write-Host 'Crash capture disabled. Existing dumps and ProcDump were retained.'
    return
}

Write-Host 'ProcDump is a Microsoft Sysinternals tool:'
Write-Host 'https://learn.microsoft.com/sysinternals/downloads/procdump'
Write-Host 'The diagnostic mode stores up to three dumps locally. A dump may contain private data from GTA memory.'
Write-Host 'ProcDump will monitor GTA only while this optional mode is enabled.'
$answer = Read-Host 'Download and accept the ProcDump license for this diagnostic mode? [y/N]'
if ($answer -notin @('y','Y','yes','YES')) { throw 'Crash capture was not enabled.' }

function Assert-MicrosoftSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
        throw "ProcDump signature is not valid and issued to Microsoft Corporation: $Path"
    }
}

if (Test-Path -LiteralPath $tool) {
    Assert-MicrosoftSignature $tool
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path ([IO.Path]::GetTempPath()) ('ModPack-ProcDump-' + [Guid]::NewGuid().ToString('N') + '.zip')
    $candidate = Join-Path ([IO.Path]::GetTempPath()) ('ModPack-ProcDump-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/Procdump.zip' -OutFile $zip -UseBasicParsing
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.Name -eq 'procdump64.exe' } | Select-Object -First 1
            if (!$entry) { throw 'The Microsoft archive does not contain procdump64.exe.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $candidate, $false)
        } finally { $archive.Dispose() }
        Assert-MicrosoftSignature $candidate
        [void](New-Item -ItemType Directory -Path $toolFolder -Force)
        Move-Item -LiteralPath $candidate -Destination $tool
    } finally {
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
        if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate }
    }
}

[void](New-Item -ItemType Directory -Path $root -Force)
[IO.File]::WriteAllText($enabled, 'ProcDump capture enabled' + [Environment]::NewLine)
Write-Host "Crash capture enabled: $tool"
Write-Host 'It will attach automatically when CrashLogger starts in GTA. No additional UAC request is needed.'
