@echo off
setlocal
call "%~dp0..\modpack.cmd" Check
if errorlevel 1 exit /b 1
dotnet build "%~dp0..\modules\Loader\src\Reloader.csproj" -c Release
if errorlevel 1 exit /b 1
echo Loader built under modules\Loader\src\bin\Release\net48.
