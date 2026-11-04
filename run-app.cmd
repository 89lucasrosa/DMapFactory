@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
dotnet run --project "%~dp0src\DMapFactory.App" %*
