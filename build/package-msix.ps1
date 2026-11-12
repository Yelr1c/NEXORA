<#
.SYNOPSIS
  Builds a signed MSIX package of NEXORA (the main distribution format).
.EXAMPLE
  $pw = Read-Host -AsSecureString
  .\build\new-dev-certificate.ps1 -Password $pw -Trust          # once, elevated
  .\build\package-msix.ps1 -CertificatePath artifacts\certificate\NEXORA_Dev.pfx -CertificatePassword $pw
.NOTES
  Uses MSBuild from Visual Studio 2022/2026 (located with vswhere) because MSIX packaging targets ship with VS.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')] [string[]] $Platforms = @('x64'),
    [string] $CertificatePath,
    [securestring] $CertificatePassword,
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\msix')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Nexora.App\Nexora.App.csproj'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio with the "Windows application development" workload is required for MSIX packaging.' }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild.exe not found.' }

$signing = @('/p:AppxPackageSigningEnabled=false')
if ($CertificatePath) {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword))
    $signing = @('/p:AppxPackageSigningEnabled=true', "/p:PackageCertificateKeyFile=$((Resolve-Path $CertificatePath).Path)", "/p:PackageCertificatePassword=$plain")
}

foreach ($platform in $Platforms) {
    Write-Host "==> Packaging NEXORA for $platform" -ForegroundColor Cyan
    & $msbuild $project /restore /m /v:minimal `
        /p:Configuration=Release /p:Platform=$platform `
        /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=SideloadOnly `
        /p:AppxBundle=Never "/p:AppxPackageDir=$OutputDirectory\" @signing
    if ($LASTEXITCODE) { throw "MSIX packaging failed for $platform" }
}
Write-Host "MSIX package(s) written to $OutputDirectory" -ForegroundColor Green
Write-Host 'Install by double-clicking the .msix, or: Add-AppxPackage -Path <file>.msix'
