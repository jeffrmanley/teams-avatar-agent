#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Register a Windows Task Scheduler job that runs install-tls-cert.ps1 daily.

.DESCRIPTION
    The cert install script is idempotent — it exits early if the existing
    cert has more than 30 days until expiry. Running it daily at a quiet
    hour means renewals fire well before Let's Encrypt's 90-day cert
    expiry, with no manual intervention.

    Why daily and not weekly: lets us catch any failed renewal attempts
    early (one failed run leaves >29 days of cushion), and the runtime
    overhead is essentially zero on the no-op path.

    The task runs as the current user (so Posh-ACME's per-user config —
    the ACME account, the DNS plugin state — is the same one a manual
    invocation would use) with the S4U logon type, which does NOT require
    a stored password. The user's network credentials remain available so
    outbound HTTPS to Let's Encrypt + Cloudflare works.

    Output goes to logs/cert-renewal.log so failures are diagnosable.

    Idempotent: re-running this script replaces any existing task with
    the same name.

.PARAMETER TaskName
    Name of the scheduled task. Default: TeamsAvatarBot-CertRenewal

.PARAMETER TriggerTime
    Daily run time (HH:mm, 24-hour). Default: 03:00

.PARAMETER Remove
    If set, just unregister the existing task and exit. Useful for cleanup.
#>

[CmdletBinding()]
param(
    [string]$TaskName    = 'TeamsAvatarBot-CertRenewal',
    [string]$TriggerTime = '03:00',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path -Parent $PSScriptRoot
$installPs1 = Join-Path $repoRoot 'scripts\install-tls-cert.ps1'
$logDir     = Join-Path $repoRoot 'logs'
$logPath    = Join-Path $logDir 'cert-renewal.log'

# Cleanup mode
if ($Remove) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "removed scheduled task '$TaskName'" -ForegroundColor Green
    } else {
        Write-Host "no scheduled task '$TaskName' found — nothing to do" -ForegroundColor Yellow
    }
    return
}

if (-not (Test-Path $installPs1)) {
    throw "install-tls-cert.ps1 not found at $installPs1"
}
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
}

# Validate trigger format eagerly so we don't get a confusing error from
# New-ScheduledTaskTrigger if the user typoed.
try {
    [void][DateTime]::Parse($TriggerTime)
} catch {
    throw "invalid -TriggerTime '$TriggerTime' (expected HH:mm, e.g. 03:00): $($_.Exception.Message)"
}

# Build the action: pwsh -NoProfile -File install-tls-cert.ps1 with stdout+stderr
# appended to the renewal log. *>> captures all streams (incl. errors).
$pwshArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$installPs1`" *>> `"$logPath`""

$action = New-ScheduledTaskAction `
    -Execute          (Get-Command pwsh).Source `
    -Argument         $pwshArgs `
    -WorkingDirectory $repoRoot

$trigger = New-ScheduledTaskTrigger -Daily -At $TriggerTime

# Task settings: tolerate battery/sleep, retry on transient failure, cap
# wall-clock so a stuck Posh-ACME doesn't hang forever.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount       3 `
    -RestartInterval    (New-TimeSpan -Minutes 15) `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

# S4U = "Service For User" — task can run when user is not interactively
# logged in, without a stored password. The user's network identity is
# preserved for outbound HTTPS. RunLevel Highest gives admin rights so the
# install script can write into LocalMachine\My if needed.
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal `
    -UserId    $currentUser `
    -LogonType S4U `
    -RunLevel  Highest

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "removing existing task '$TaskName'" -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName    $TaskName `
    -Action      $action `
    -Trigger     $trigger `
    -Settings    $settings `
    -Principal   $principal `
    -Description "Daily Let's Encrypt renewal for $env:BOT_PUBLIC_FQDN. Idempotent — skips when cert has >30d remaining." | Out-Null

$info = Get-ScheduledTask -TaskName $TaskName | Get-ScheduledTaskInfo

Write-Host ''
Write-Host "scheduled task '$TaskName' registered" -ForegroundColor Green
Write-Host "  user:     $currentUser  (S4U, runs offline)" -ForegroundColor DarkGray
Write-Host "  command:  pwsh.exe -File $installPs1" -ForegroundColor DarkGray
Write-Host "  log:      $logPath" -ForegroundColor DarkGray
Write-Host "  schedule: daily at $TriggerTime" -ForegroundColor DarkGray
Write-Host "  next run: $($info.NextRunTime)" -ForegroundColor DarkGray
Write-Host ''
Write-Host "inspect:  Get-ScheduledTask -TaskName $TaskName | Get-ScheduledTaskInfo" -ForegroundColor Cyan
Write-Host "run now:  Start-ScheduledTask -TaskName $TaskName     # then tail $logPath"  -ForegroundColor Cyan
Write-Host "remove:   pwsh -File .\scripts\schedule-cert-renewal.ps1 -Remove" -ForegroundColor Cyan
