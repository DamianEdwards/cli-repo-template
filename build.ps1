$ErrorActionPreference = 'Stop'

function Get-PowerShellCompletionProfilePath
{
    if ($PROFILE -is [string])
    {
        return $PROFILE
    }

    if ($PROFILE.PSObject.Properties.Name -contains 'CurrentUserAllHosts' -and -not [string]::IsNullOrWhiteSpace($PROFILE.CurrentUserAllHosts))
    {
        return $PROFILE.CurrentUserAllHosts
    }

    return [string]$PROFILE
}

function Test-ProfileContainsCompletionEntry
{
    param(
        [Parameter(Mandatory)][string]$ProfilePath,
        [Parameter(Mandatory)][string]$ScriptPath
    )

    if (-not (Test-Path $ProfilePath))
    {
        return $false
    }

    $content = Get-Content -Path $ProfilePath -Raw
    return $content.Contains($ScriptPath)
}

function Test-NativeArgumentCompleterSupported
{
    $command = Get-Command Register-ArgumentCompleter -ErrorAction SilentlyContinue
    return $null -ne $command -and $command.Parameters.ContainsKey('Native')
}

$scriptDir = Split-Path -Parent $PSCommandPath
$projectFile = Join-Path $scriptDir 'src\templatecli\templatecli.csproj'
$commandPath = Join-Path $scriptDir 'templatecli.ps1'

if (-not $env:TEMPLATECLI_HOME)
{
    $env:TEMPLATECLI_HOME = Join-Path $scriptDir '.templatecli-home'
}

& dotnet build $projectFile --no-logo @args
if ($LASTEXITCODE -ne 0)
{
    exit $LASTEXITCODE
}

if (-not (Test-NativeArgumentCompleterSupported))
{
    Write-Warning "Skipping PowerShell completion registration because Register-ArgumentCompleter -Native is unavailable in this shell."
    exit 0
}

$completionDir = Join-Path $env:TEMPLATECLI_HOME 'completions'
$completionScriptPath = Join-Path $completionDir 'templatecli-completions.ps1'
$commandNames = @(
    'templatecli',
    'templatecli.ps1',
    '.\templatecli.ps1',
    $commandPath
)

New-Item -ItemType Directory -Path $completionDir -Force | Out-Null

$completionScript = & $commandPath completions script pwsh @(
    foreach ($commandName in $commandNames)
    {
        '--command-name'
        $commandName
    }
)

if ($LASTEXITCODE -ne 0)
{
    exit $LASTEXITCODE
}

Set-Content -Path $completionScriptPath -Value ($completionScript -join [Environment]::NewLine) -Encoding utf8

$profilePath = Get-PowerShellCompletionProfilePath
if (-not (Test-ProfileContainsCompletionEntry -ProfilePath $profilePath -ScriptPath $completionScriptPath))
{
    $profileDirectory = Split-Path -Parent $profilePath
    if (-not [string]::IsNullOrWhiteSpace($profileDirectory))
    {
        New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    }

    Add-Content -Path $profilePath -Value ''
    Add-Content -Path $profilePath -Value '# Added by templatecli local build for shell completion'
    Add-Content -Path $profilePath -Value ". '$completionScriptPath'"
}

. $completionScriptPath
