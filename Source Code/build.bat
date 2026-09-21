@echo off
setlocal

call "%~dp0..\modpack.cmd" Check
if errorlevel 1 exit /b 1

echo Building Reloader...

dotnet build "%~dp0Reloader\Reloader.csproj" -c Release
set "buildExit=%errorlevel%"

if %errorlevel% equ 0 (
    echo Success!
) else (
    echo Build failed!
)

pause
exit /b %buildExit%
