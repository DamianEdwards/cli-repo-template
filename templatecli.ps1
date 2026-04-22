$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$projectDir = Join-Path $scriptDir 'src\templatecli'

if (-not $env:TEMPLATECLI_HOME) {
    $env:TEMPLATECLI_HOME = Join-Path $scriptDir '.templatecli-home'
}

& dotnet run --project $projectDir --no-build -- @args
exit $LASTEXITCODE
