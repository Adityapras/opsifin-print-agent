[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionArguments = if ([string]::IsNullOrWhiteSpace($Version)) {
    @()
} else {
    @("/p:Version=$Version")
}

Push-Location $repositoryRoot
try {
    if (-not $SkipTests) {
        dotnet test -c Release @versionArguments
        if ($LASTEXITCODE -ne 0) {
            throw 'Automated tests failed.'
        }
    }

    dotnet publish src\Agent.Service -c Release -r win-x64 -o artifacts\package\agent @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

    dotnet publish src\Agent.Desktop -c Release -r win-x64 -o artifacts\package\control @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

    dotnet publish src\Agent.Updater -c Release -r win-x64 -o artifacts\package\updater @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }

    dotnet build installer\Agent.Installer.wixproj -c Release @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

    $resolvedVersion = $Version
    if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
        [xml]$buildProperties = Get-Content -LiteralPath 'Directory.Build.props'
        $resolvedVersion = $buildProperties.Project.PropertyGroup.Version
    }

    $installerPath = Join-Path $repositoryRoot "dist\OpsifinPrintAgent-Setup-$resolvedVersion-win-x64.msi"
    if (-not (Test-Path -LiteralPath $installerPath)) {
        throw "The expected installer was not created: $installerPath"
    }

    $hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
    Write-Host ''
    Write-Host 'Installer is ready:' -ForegroundColor Green
    Write-Host $installerPath
    Write-Host "SHA-256: $($hash.Hash.ToLowerInvariant())"
} finally {
    Pop-Location
}
