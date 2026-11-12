<#
.SYNOPSIS
  Creates a self-signed code-signing certificate for local MSIX testing.
.DESCRIPTION
  The certificate subject must equal the Publisher in src/Nexora.App/Package.appxmanifest (CN=NEXORA).
  For public distribution, sign with a certificate from a trusted CA (or publish through the Microsoft Store),
  and update the manifest Publisher to match that certificate's subject.

  Installing the .cer into "Trusted People" on the local machine requires an elevated PowerShell.
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=NEXORA',
    [Parameter(Mandatory)] [securestring] $Password,
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\certificate'),
    [switch] $Trust
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -KeyUsage DigitalSignature `
    -FriendlyName 'NEXORA development signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(2)

$pfx = Join-Path $OutputDirectory 'NEXORA_Dev.pfx'
$cer = Join-Path $OutputDirectory 'NEXORA_Dev.cer'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

Write-Host "Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
Write-Host "PFX:        $pfx"
Write-Host "CER:        $cer"

if ($Trust) {
    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Write-Host 'Certificate trusted for MSIX installation on this PC.' -ForegroundColor Green
}
else {
    Write-Host "To install test packages, trust the certificate once (elevated):" -ForegroundColor Yellow
    Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}
