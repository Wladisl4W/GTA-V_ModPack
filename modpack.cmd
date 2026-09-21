@echo off
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\modpack.ps1" -Mode Update
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\modpack.ps1" %*
)
exit /b %errorlevel%
