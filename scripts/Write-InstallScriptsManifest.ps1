[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$SourceCommit,

    [string]$SourceRef = 'refs/heads/main'
)

$ErrorActionPreference = 'Stop'

$directory = [System.IO.Path]::GetFullPath($Directory)
$installPs1Path = Join-Path $directory 'install.ps1'
$installShPath = Join-Path $directory 'install.sh'

if (-not (Test-Path $installPs1Path -PathType Leaf))
{
    throw "Install script '$installPs1Path' was not found."
}

if (-not (Test-Path $installShPath -PathType Leaf))
{
    throw "Install script '$installShPath' was not found."
}

$installPs1Sha = (Get-FileHash -Path $installPs1Path -Algorithm SHA256).Hash.ToLowerInvariant()
$installShSha = (Get-FileHash -Path $installShPath -Algorithm SHA256).Hash.ToLowerInvariant()

$checksumsPath = Join-Path $directory 'checksums.txt'
@(
    "$installPs1Sha  install.ps1"
    "$installShSha  install.sh"
) | Set-Content -Path $checksumsPath

$manifest = [ordered]@{
    version = $Version
    sourceCommit = $SourceCommit
    sourceRef = $SourceRef
    publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
    scripts = @(
        [ordered]@{
            name = 'install.ps1'
            sha256 = $installPs1Sha
        }
        [ordered]@{
            name = 'install.sh'
            sha256 = $installShSha
        }
    )
}

$manifestPath = Join-Path $directory 'install-scripts.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath
