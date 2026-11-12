<#
.SYNOPSIS
  Restores, builds and tests the complete NEXORA solution.
.EXAMPLE
  .\build\build.ps1                      # Release, x64
  .\build\build.ps1 -Platform ARM64
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "==> Restoring" -ForegroundColor Cyan
    dotnet restore NEXORA.sln -p:Platform=$Platform
    if ($LASTEXITCODE) { throw "Restore failed" }

    Write-Host "==> Building $Configuration|$Platform" -ForegroundColor Cyan
    dotnet build NEXORA.sln -c $Configuration -p:Platform=$Platform --no-restore
    if ($LASTEXITCODE) { throw "Build failed" }

    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test tests/Nexora.Core.Tests/Nexora.Core.Tests.csproj -c $Configuration
    if ($LASTEXITCODE) { throw "Tests failed" }

    Write-Host "Build succeeded." -ForegroundColor Green
}
finally { Pop-Location }
