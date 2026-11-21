@echo off
if /I "%~1"=="Map" goto map
if /I "%~1"=="Update" if not "%~2"=="" goto maybeModule
:default
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\modpack.ps1" -Mode Update
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\modpack.ps1" %*
)
exit /b %errorlevel%
:maybeModule
set "modpack_second=%~2"
if "%modpack_second:~0,1%"=="-" goto default
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\modpack.ps1" -Mode Update -Module "%~2" %3 %4 %5 %6 %7 %8 %9
exit /b %errorlevel%
:map
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-project-map.ps1"
if errorlevel 1 exit /b %errorlevel%
start "" "%~dp0.modpack-local\project-map.html"
exit /b %errorlevel%
