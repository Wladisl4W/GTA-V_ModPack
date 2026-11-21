param([Parameter(Mandatory=$true)][string]$BlenderExe)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '2.9.0'
$expectedZipHash = 'C30F96D4C05CA74CA075EB5FDBCFBF08441498CA03328EB10FFF595B4A4ACD49'
$data = Join-Path $env:LOCALAPPDATA 'ModPack\PedToProp'
$dependencies = Join-Path $data ('Dependencies\' + $version)
$ready = Join-Path $dependencies 'ready.txt'
if (Test-Path -LiteralPath $ready) { Write-Output 'Sollumz 2.9.0 already configured.'; exit 0 }
if (-not (Test-Path -LiteralPath $BlenderExe -PathType Leaf)) { throw 'blender.exe not found.' }
$python = Join-Path (Split-Path -Parent $BlenderExe) '5.2\python\bin\python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw 'Blender 5.2 with Python 3.13 is required.' }
New-Item -ItemType Directory -Path $data -Force | Out-Null
$stage = Join-Path $data ('setup-' + [guid]::NewGuid().ToString('N'))
$resolvedData = [IO.Path]::GetFullPath($data).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedStage = [IO.Path]::GetFullPath($stage)
if (-not $resolvedStage.StartsWith($resolvedData, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe setup path.' }
if (-not [IO.Path]::GetFullPath($dependencies).StartsWith($resolvedData, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe dependency path.' }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $zip = Join-Path $stage 'Sollumz.zip'
    Write-Output 'Downloading Sollumz 2.9.0 from official GitHub...'
    Invoke-WebRequest -Uri 'https://github.com/Sollumz/Sollumz/releases/download/v2.9.0/Sollumz.zip' -OutFile $zip
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expectedZipHash) { throw 'Sollumz checksum mismatch.' }
    $expanded = Join-Path $stage 'expanded'
    Expand-Archive -LiteralPath $zip -DestinationPath $expanded
    $addonParent = Join-Path $stage 'addon'
    New-Item -ItemType Directory -Path $addonParent -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $expanded 'Sollumz') -Destination (Join-Path $addonParent 'sollumz') -Recurse
    $lib = Join-Path $stage 'lib'
    Write-Output 'Installing private PedToProp dependencies...'
    & $python -m pip install --disable-pip-version-check --target $lib 'szio==1.3.0.dev9'
    if ($LASTEXITCODE -ne 0) { throw 'Could not install szio.' }
    & $python -m pip install --disable-pip-version-check --target $lib --index-url 'https://static.cfx.re/whl/' --extra-index-url 'https://pypi.org/simple' 'pymateria==0.2.0'
    if ($LASTEXITCODE -ne 0) { throw 'Could not install PyMateria.' }
    $env:PEDTOPROP_SOLLUMZ_PARENT = $addonParent
    $env:PEDTOPROP_SOLLUMZ_LIB = $lib
    $probe = "import os,sys,bpy; sys.path.extend([os.environ['PEDTOPROP_SOLLUMZ_PARENT'],os.environ['PEDTOPROP_SOLLUMZ_LIB']]); p=bpy.context.preferences.addons.new(); p.module='sollumz'; import sollumz; sollumz.register(); print('PEDTOPROP_SETUP_OK',sollumz.bl_info['version'])"
    $output = & $BlenderExe -b --factory-startup --python-expr $probe 2>&1
    $output | ForEach-Object { Write-Output $_ }
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'PEDTOPROP_SETUP_OK \(2, 9, 0\)') { throw 'Sollumz Blender check failed.' }
    Set-Content -LiteralPath (Join-Path $stage 'ready.txt') -Value 'Sollumz 2.9.0 / szio 1.3.0.dev9 / PyMateria 0.2.0' -Encoding UTF8
    Remove-Item -LiteralPath $zip -Force
    Remove-Item -LiteralPath $expanded -Recurse -Force
    $parent = Split-Path -Parent $dependencies
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $dependencies) { throw 'Unverified dependency folder already exists.' }
    Move-Item -LiteralPath $stage -Destination $dependencies
    Write-Output 'PEDTOPROP_SETUP_COMPLETE'
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
