@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ConfigureSilentExit.ps1" -State On
set "result=%errorlevel%"
pause
exit /b %result%
