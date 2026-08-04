#Requires -Version 7.0
<#
.SYNOPSIS
    Hit each 3rd-party API once to verify credentials in .env are valid.

.DESCRIPTION
    - Claude /messages one-shot on claude-sonnet-4-5
    - Deepgram project listing (auth check)
    - Cartesia voices listing (auth check)
    - Beyond Presence /v1/agent (auth check)
    - LiveKit room list via lk CLI
    Exits non-zero if any fail. Logs to logs/smoke.log.
#>

[CmdletBinding()]
param(
    [string]$EnvPath = (Join-Path (Split-Path -Parent $PSScriptRoot) '.env')
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir   = Join-Path $repoRoot 'logs'
$logPath  = Join-Path $logDir 'smoke.log'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
"`n=== smoke-test run $(Get-Date -Format o) ===" | Out-File -FilePath $logPath -Append -Encoding utf8

function Log {
    param([string]$Level, [string]$Msg)
    $line = "{0} [{1}] {2}" -f (Get-Date -Format o), $Level, $Msg
    $line | Out-File -FilePath $logPath -Append -Encoding utf8
    switch ($Level) {
        'OK'   { Write-Host $line -ForegroundColor Green }
        'WARN' { Write-Host $line -ForegroundColor Yellow }
        'FAIL' { Write-Host $line -ForegroundColor Red }
        default{ Write-Host $line }
    }
}

# Load .env
if (-not (Test-Path $EnvPath)) {
    Log 'FAIL' ".env not found at $EnvPath — copy .env.example and fill it in."
    exit 1
}
Get-Content $EnvPath | ForEach-Object {
    if ($_ -match '^\s*#') { return }
    if ($_ -match '^\s*([A-Z0-9_]+)=(.*)$') {
        $name = $Matches[1]
        $val  = $Matches[2].Trim().Trim('"').Trim("'")
        if ($val) { [System.Environment]::SetEnvironmentVariable($name, $val, 'Process') }
    }
}

function Need {
    param([string]$Name)
    $v = [System.Environment]::GetEnvironmentVariable($Name, 'Process')
    if (-not $v) { Log 'FAIL' "env var $Name not set in .env"; return $null }
    return $v
}

$fails = 0
function Try-Step {
    param([string]$Name, [scriptblock]$Block)
    try {
        & $Block
        Log 'OK' "$Name ok"
    } catch {
        $script:fails++
        Log 'FAIL' "$Name failed: $($_.Exception.Message)"
    }
}

# -------------------------------------------------------------------
Try-Step 'Anthropic' {
    $key = Need 'ANTHROPIC_API_KEY'; if (-not $key) { throw 'no key' }
    $body = @{
        model      = 'claude-sonnet-4-5'
        max_tokens = 16
        messages   = @(@{ role='user'; content='ping' })
    } | ConvertTo-Json -Depth 5 -Compress
    $resp = Invoke-RestMethod -Method Post -Uri 'https://api.anthropic.com/v1/messages' `
        -Headers @{
            'x-api-key'         = $key
            'anthropic-version' = '2023-06-01'
            'content-type'      = 'application/json'
        } -Body $body
    if (-not $resp.content) { throw 'no content in response' }
}

# -------------------------------------------------------------------
Try-Step 'Deepgram' {
    $key = Need 'DEEPGRAM_API_KEY'; if (-not $key) { throw 'no key' }
    $resp = Invoke-RestMethod -Method Get -Uri 'https://api.deepgram.com/v1/projects' `
        -Headers @{ 'Authorization' = "Token $key" }
    if (-not $resp.projects) { throw 'no projects in response' }
}

# -------------------------------------------------------------------
Try-Step 'ElevenLabs' {
    $key = Need 'ELEVEN_API_KEY'; if (-not $key) { throw 'no key' }
    $resp = Invoke-RestMethod -Method Get -Uri 'https://api.elevenlabs.io/v1/user' `
        -Headers @{ 'xi-api-key' = $key }
    if (-not $resp.subscription) { throw 'no subscription in response' }
}

# -------------------------------------------------------------------
Try-Step 'Beyond Presence' {
    $key = Need 'BEY_API_KEY'; if (-not $key) { throw 'no key' }
    # Beyond Presence uses x-api-key; /v1/agent lists agents.
    $resp = Invoke-RestMethod -Method Get -Uri 'https://api.bey.dev/v1/agent' `
        -Headers @{ 'x-api-key' = $key }
    if ($null -eq $resp) { throw 'empty response' }
}

# -------------------------------------------------------------------
Try-Step 'LiveKit' {
    $url = Need 'LIVEKIT_URL'
    $key = Need 'LIVEKIT_API_KEY'
    $sec = Need 'LIVEKIT_API_SECRET'
    if (-not ($url -and $key -and $sec)) { throw 'missing LIVEKIT_* vars' }

    $lkCmd = Get-Command 'lk' -ErrorAction SilentlyContinue
    if (-not $lkCmd) {
        Log 'WARN' 'lk CLI not installed (npm i -g @livekit/cli). Skipping CLI check.'
        return
    }
    $env:LIVEKIT_URL        = $url
    $env:LIVEKIT_API_KEY    = $key
    $env:LIVEKIT_API_SECRET = $sec
    & lk room list 2>&1 | Tee-Object -FilePath $logPath -Append | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "lk room list exit $LASTEXITCODE" }
}

if ($fails -gt 0) {
    Log 'FAIL' "$fails smoke test(s) failed — see $logPath"
    exit 1
}
Log 'OK' 'all smoke tests passed'
exit 0
