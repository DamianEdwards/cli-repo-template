[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install\install-templatecli.ps1') -NoExecute
if ([string]::IsNullOrWhiteSpace($TargetPath) -or -not [System.IO.Path]::IsPathRooted($TargetPath))
{
    throw "The installer's default target path must be available on every host when helpers are loaded with -NoExecute."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("templatecli-payload-test-" + [guid]::NewGuid().ToString('N'))
try
{
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $tempRoot 'templatecli.exe') -Value 'test'
    Set-Content -LiteralPath (Join-Path $tempRoot 'LICENSE') -Value 'test'
    $dataDirectory = Join-Path $tempRoot 'data'
    New-Item -ItemType Directory -Path $dataDirectory | Out-Null
    Set-Content -LiteralPath (Join-Path $dataDirectory 'settings.json') -Value '{}'
    @{ files = @('LICENSE', 'data/settings.json', 'templatecli.exe') } |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $tempRoot 'payload-manifest.json')

    $files = @(Get-PayloadManifestFiles -PayloadRoot $tempRoot -RequireExactInventory)
    if ($files.Count -ne 3 -or $files[0] -ne 'LICENSE' -or $files[1] -ne 'data/settings.json' -or $files[2] -ne 'templatecli.exe')
    {
        throw 'Valid payload manifest was not accepted.'
    }

    @{ files = @('../escape', 'templatecli.exe') } |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $tempRoot 'payload-manifest.json')
    try
    {
        $null = Get-PayloadManifestFiles -PayloadRoot $tempRoot -RequireExactInventory
        throw 'Traversal payload manifest was accepted.'
    }
    catch
    {
        if ($_.Exception.Message -eq 'Traversal payload manifest was accepted.') { throw }
    }

    @{ files = @('templatecli.exe') } |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $tempRoot 'payload-manifest.json')
    try
    {
        $null = Get-PayloadManifestFiles -PayloadRoot $tempRoot -RequireExactInventory
        throw 'Incomplete payload manifest was accepted.'
    }
    catch
    {
        if ($_.Exception.Message -eq 'Incomplete payload manifest was accepted.') { throw }
    }

    Write-Host 'Installer payload contract tests passed.'
}
finally
{
    if (Test-Path -LiteralPath $tempRoot)
    {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
