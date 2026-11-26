@echo off
setlocal
call "%~dp0..\..\modpack.cmd" Check
if errorlevel 1 exit /b 1
dotnet build "%~dp0..\..\development\Loader\src\Reloader.csproj" -c Release
if errorlevel 1 exit /b 1
copy /Y "%~dp0..\..\development\Loader\src\bin\Release\net48\Reloader.dll" "%~dp0..\..\scripts\Reloader.dll" >nul
if errorlevel 1 exit /b 1
echo Loader built and copied to scripts\Reloader.dll.
