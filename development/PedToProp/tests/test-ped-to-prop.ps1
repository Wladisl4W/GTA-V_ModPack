param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$exe = Join-Path $repo 'development\PedToProp\src\bin\Release\net48\PedToProp.exe'
$source = 'P:\GTA V\Peds\MinionsYellowRainbowJacket\!ready'
$name = 'minionYellowBlueJacket'
$testRoot = Join-Path $env:TEMP ('PedToPropTests-' + [guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::GetFullPath($testRoot).StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test path.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    $inputDir = Join-Path $testRoot 'input'
    New-Item -ItemType Directory -Path $inputDir | Out-Null
    foreach ($ext in @('ydd','ytd','yft','ymt')) {
        Copy-Item -LiteralPath (Join-Path $source "$name.$ext") -Destination $inputDir
    }
    $files = @('ydd','ytd','yft','ymt') | ForEach-Object { Join-Path $inputDir "$name.$_" }
    function Check([string[]]$paths, [bool]$expected) {
        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = '--check ' + (($paths | ForEach-Object { '"' + $_ + '"' }) -join ' ')
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $process = [Diagnostics.Process]::Start($psi)
        $text = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if (($process.ExitCode -eq 0) -ne $expected) { throw "Unexpected check result: $text" }
    }
    Check $files $true
    Check $files[0..2] $false
    Check @($files[0],$files[1],$files[2],$files[2]) $false
    $bad = Join-Path $testRoot 'bad'
    New-Item -ItemType Directory -Path $bad | Out-Null
    foreach ($path in $files) { Copy-Item -LiteralPath $path -Destination $bad }
    $badYtd = Join-Path $bad "$name.ytd"
    $bytes = [IO.File]::ReadAllBytes($badYtd)
    $bytes[0] = 0
    [IO.File]::WriteAllBytes($badYtd, $bytes)
    Check (@('ydd','ytd','yft','ymt') | ForEach-Object { Join-Path $bad "$name.$_" }) $false
    Write-Output 'Input validation: OK'

    if ($Integration) {
        $blender = 'C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe'
        $dependencies = Join-Path $env:LOCALAPPDATA 'ModPack\PedToProp\Dependencies\2.9.0'
        if (-not (Test-Path -LiteralPath (Join-Path $dependencies 'ready.txt'))) { throw 'Run setup.ps1 first.' }
        $env:PEDTOPROP_SOLLUMZ_PARENT = Join-Path $dependencies 'addon'
        $env:PEDTOPROP_SOLLUMZ_LIB = Join-Path $dependencies 'lib'
        $worker = Join-Path $repo 'development\PedToProp\src\convert_blender.py'
        $result = Join-Path $testRoot 'result'
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = & $blender -b --factory-startup --python $worker -- $inputDir $name $result 2>&1
        $ErrorActionPreference = $previousPreference
        if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'PEDTOPROP_OK') { throw "Conversion failed: $($output -join "`n")" }
        foreach ($ext in @('.ydr','_preview.png')) {
            if (-not (Test-Path -LiteralPath (Join-Path $result ('prop_p2p_' + $name.ToLowerInvariant() + $ext)))) { throw "Missing output $ext" }
        }
        if (Test-Path -LiteralPath (Join-Path $result ('prop_p2p_' + $name.ToLowerInvariant() + '.ytd'))) { throw 'Unexpected external texture dictionary.' }
        if (Test-Path -LiteralPath (Join-Path $result ('prop_p2p_' + $name.ToLowerInvariant() + '_typ.ytyp'))) { throw 'Unexpected archetype file.' }
        $verify = Join-Path $repo 'development\PedToProp\src\verify_blender.py'
        $ErrorActionPreference = 'Continue'
        $verified = & $blender -b --factory-startup --python $verify -- $result ('prop_p2p_' + $name.ToLowerInvariant()) 2>&1
        $ErrorActionPreference = $previousPreference
        if ($LASTEXITCODE -ne 0 -or ($verified -join "`n") -notmatch 'VERIFY_RESULT.*meshes 4 images [1-9]') { throw 'Exported model did not re-import correctly.' }
        $result2 = Join-Path $testRoot 'repeat'
        $ErrorActionPreference = 'Continue'
        $output2 = & $blender -b --factory-startup --python $worker -- $inputDir $name $result2 2>&1
        $ErrorActionPreference = $previousPreference
        if ($LASTEXITCODE -ne 0 -or ($output2 -join "`n") -notmatch 'PEDTOPROP_OK') { throw 'Repeat conversion failed.' }
        Write-Output 'Blender conversion and repeat: OK'
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
