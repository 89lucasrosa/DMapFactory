@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
dotnet run --project "%~dp0tools\DMapFactory.TestServer" -- --port 48400 --tags 50 --interval 100 %*
