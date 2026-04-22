@echo off
setlocal

dotnet build "%~dp0src\templatecli\templatecli.csproj" --no-logo %*
set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%
