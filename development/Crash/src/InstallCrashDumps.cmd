@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ConfigureCrashDumps.ps1" -State On
exit /b %errorlevel%
