@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%src\templatecli"
if not defined TEMPLATECLI_HOME set "TEMPLATECLI_HOME=%SCRIPT_DIR%.templatecli-home"

dotnet run --project "%PROJECT_DIR%" --no-build -- %*
set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%
