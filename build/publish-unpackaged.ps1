<#
.SYNOPSIS
  Produces a portable (unpackaged, self-contained) build of NEXORA: a folder with NEXORA.exe that runs
  without installation and without the Windows App Runtime being installed.
.EXAMPLE
  .\build\publish-unpackaged.ps1                 # win-x64, zipped
  .\build\publish-unpackaged.ps1 -Platform ARM64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [switch] $NoZip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rid = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$out = Join-Path $root "artifacts\portable\$rid"

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\Nexora.App\Nexora.App.csproj') -c Release -r $rid `
    -p:Platform=$Platform -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    --self-contained true -o $out
if ($LASTEXITCODE) { throw 'Publish failed' }

if (-not (Test-Path (Join-Path $out 'NEXORA.exe'))) { throw 'NEXORA.exe was not produced.' }
Write-Host "Portable build: $out\NEXORA.exe" -ForegroundColor Green

if (-not $NoZip) {
    $zip = Join-Path $root "artifacts\NEXORA-portable-$rid.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "Zip: $zip" -ForegroundColor Green
}
