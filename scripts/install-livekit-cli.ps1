#Requires -Version 5.1
<#
.SYNOPSIS
    Install the LiveKit CLI (`lk`) globally via npm.

.DESCRIPTION
    The `lk` CLI is used by scripts/smoke-test.ps1 to check LiveKit auth and
    (later) to inspect rooms/participants during debugging. Separate from the
    preflight because it depends on Node.js being present first.
#>
[CmdletBinding()] param()

$ErrorActionPreference = 'Stop'

if (-not (Get-Command 'npm' -ErrorAction SilentlyContinue)) {
    Write-Host "npm not found — run preflight-check.ps1 -Mode Install first" -ForegroundColor Red
    exit 1
}

Write-Host "Installing @livekit/cli globally..." -ForegroundColor Cyan
& npm install -g @livekit/cli
if ($LASTEXITCODE -ne 0) { Write-Host "npm install failed (exit $LASTEXITCODE)" -ForegroundColor Red; exit 1 }

$lk = Get-Command 'lk' -ErrorAction SilentlyContinue
if (-not $lk) {
    Write-Host "installed but 'lk' not on PATH — open a new shell and try again" -ForegroundColor Yellow
    exit 1
}
Write-Host ("ok: {0}" -f (& lk --version)) -ForegroundColor Green
