@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ConfigureCrashCapture.ps1" -State On
set "result=%errorlevel%"
pause
exit /b %result%
