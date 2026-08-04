#Requires -Version 7.0
<#
.SYNOPSIS
    Issue / renew the Let's Encrypt TLS certificate for the Graph media bot
    via DNS-01 challenge against Cloudflare DNS.

.DESCRIPTION
    Idempotent: if a cert exists for the configured FQDN and is not within 30
    days of expiry, this exits 0 without re-issuing. Otherwise it talks to
    Let's Encrypt, drops a TXT record via the Cloudflare API to satisfy the
    DNS-01 challenge, fetches the cert, and copies the PFX into ./certs/.

    Reads from .env:
      BOT_PUBLIC_FQDN        - e.g. bot.example.com
      CLOUDFLARE_API_TOKEN   - Zone:DNS:Edit token for the parent zone
      CERT_CONTACT_EMAIL     - falls back to you@example.com

    Output:
      .\certs\iris.pfx                (PFX, password = $CERT_PFX_PASSWORD or "changeit")
      .\certs\iris.fullchain.pem      (cert + intermediate, for non-Windows hosts)
      .\certs\iris.key.pem            (private key)

.PARAMETER Force
    Re-issue even if a current valid cert exists.

.PARAMETER Staging
    Use Let's Encrypt staging server (untrusted certs but no rate limits).
    Useful while debugging the DNS-01 plugin.
#>

[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Staging
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$certsDir = Join-Path $repoRoot 'certs'
$envPath  = Join-Path $repoRoot '.env'

if (-not (Test-Path $envPath)) { throw ".env not found at $envPath" }
if (-not (Test-Path $certsDir)) { New-Item -ItemType Directory -Force -Path $certsDir | Out-Null }

# Load .env into the process
Get-Content $envPath | ForEach-Object {
    if ($_ -match '^\s*#') { return }
    if ($_ -match '^\s*([A-Z0-9_]+)=(.*)$') {
        $name = $Matches[1]
        $val  = $Matches[2].Trim().Trim('"').Trim("'")
        if ($val) { [Environment]::SetEnvironmentVariable($name, $val, 'Process') }
    }
}

$fqdn    = $env:BOT_PUBLIC_FQDN
$token   = $env:CLOUDFLARE_API_TOKEN
$contact = if ($env:CERT_CONTACT_EMAIL) { $env:CERT_CONTACT_EMAIL } else { 'you@example.com' }
$pfxPwd  = if ($env:CERT_PFX_PASSWORD)  { $env:CERT_PFX_PASSWORD }  else { 'changeit' }

if (-not $fqdn)  { throw 'BOT_PUBLIC_FQDN missing from .env' }
if (-not $token) { throw 'CLOUDFLARE_API_TOKEN missing from .env' }

Write-Host "Cert host:        $fqdn" -ForegroundColor Cyan
Write-Host "Contact:          $contact" -ForegroundColor Cyan
Write-Host "Output dir:       $certsDir" -ForegroundColor Cyan
Write-Host "Server:           $($Staging ? 'Lets Encrypt STAGING' : 'Lets Encrypt PROD')" -ForegroundColor Cyan

# Install posh-acme if missing
if (-not (Get-Module -ListAvailable -Name Posh-ACME)) {
    Write-Host "installing Posh-ACME (one-time)" -ForegroundColor Yellow
    Install-Module -Name Posh-ACME -Scope CurrentUser -Force -AllowClobber
}
Import-Module Posh-ACME

# Pick server
Set-PAServer ($Staging ? 'LE_STAGE' : 'LE_PROD')

# Account (one per server)
$account = Get-PAAccount
if (-not $account) {
    Write-Host "creating ACME account for $contact" -ForegroundColor Yellow
    New-PAAccount -Contact $contact -AcceptTOS | Out-Null
}

# Skip if a current valid cert already exists and -Force not set
$pfxPath  = Join-Path $certsDir 'iris.pfx'
$chainPem = Join-Path $certsDir 'iris.fullchain.pem'
$keyPem   = Join-Path $certsDir 'iris.key.pem'
if (-not $Force -and (Test-Path $pfxPath)) {
    try {
        $pwd  = ConvertTo-SecureString $pfxPwd -AsPlainText -Force
        $cert = Get-PfxCertificate -FilePath $pfxPath -Password $pwd
        $daysLeft = ($cert.NotAfter - (Get-Date)).Days
        if ($daysLeft -gt 30) {
            Write-Host ("existing cert ok, {0} days until expiry — skipping (use -Force to re-issue)" -f $daysLeft) -ForegroundColor Green
            exit 0
        }
        Write-Host ("existing cert expires in {0} days, renewing" -f $daysLeft) -ForegroundColor Yellow
    } catch {
        Write-Host "existing pfx not readable, re-issuing" -ForegroundColor Yellow
    }
}

# Cloudflare DNS plugin args
$pluginArgs = @{
    CFToken = (ConvertTo-SecureString $token -AsPlainText -Force)
}

Write-Host "requesting cert via DNS-01..." -ForegroundColor Cyan
$cert = New-PACertificate `
    -Domain $fqdn `
    -Plugin Cloudflare `
    -PluginArgs $pluginArgs `
    -PfxPass $pfxPwd `
    -AcceptTOS `
    -Contact $contact `
    -Force

# Posh-ACME drops PFX/PEM in its own store; copy into ./certs/ for our app to consume.
Copy-Item -Path $cert.PfxFile      -Destination $pfxPath  -Force
Copy-Item -Path $cert.FullChainFile -Destination $chainPem -Force
Copy-Item -Path $cert.KeyFile      -Destination $keyPem   -Force

Write-Host "" -ForegroundColor Green
Write-Host "issued cert for $fqdn" -ForegroundColor Green
Write-Host ("  pfx:        {0}" -f $pfxPath)
Write-Host ("  fullchain:  {0}" -f $chainPem)
Write-Host ("  key:        {0}" -f $keyPem)
Write-Host ("  not after:  {0}" -f $cert.NotAfter)
Write-Host ""
Write-Host "next: schedule renewal — pwsh -File .\scripts\schedule-cert-renewal.ps1" -ForegroundColor Cyan
