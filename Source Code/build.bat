@echo off
setlocal

echo Building Reloader...

dotnet build "%~dp0Reloader\Reloader.csproj" -c Release

if %errorlevel% equ 0 (
    echo Success!
) else (
    echo Build failed!
)

pause
