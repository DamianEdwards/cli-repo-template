$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$projectFile = Join-Path $scriptDir 'src\templatecli\templatecli.csproj'

& dotnet build $projectFile --no-logo @args
exit $LASTEXITCODE
