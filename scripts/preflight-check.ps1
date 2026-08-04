#Requires -Version 5.1
<#
.SYNOPSIS
    Verify the Windows host has the tooling required to build and run the Teams Avatar Agent.

.DESCRIPTION
    Checks for: PowerShell 7+, .NET 8 SDK, Git, Python 3.11/3.12, uv, Node 20, Azure CLI 2.60+,
    FFmpeg, VS 2022 Build Tools (C++ workload), Chocolatey, Windows Firewall profile state.
    Logs every "found / missing / upgraded" decision to logs/preflight.log.

.PARAMETER Mode
    Check   - (default) report only, no installs, exits non-zero if anything is missing.
    Install - install anything missing via winget (falls back to choco when noted).

.EXAMPLE
    pwsh -File .\scripts\preflight-check.ps1
    pwsh -File .\scripts\preflight-check.ps1 -Mode Install
#>

[CmdletBinding()]
param(
    [ValidateSet('Check','Install')]
    [string]$Mode = 'Check'
)

$ErrorActionPreference = 'Continue'
$script:repoRoot = Split-Path -Parent $PSScriptRoot
$script:logDir   = Join-Path $repoRoot 'logs'
$script:logPath  = Join-Path $logDir  'preflight.log'
$script:issues   = @()
$script:rows     = @()

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
"`n=== preflight run $(Get-Date -Format o) mode=$Mode ===" | Out-File -FilePath $logPath -Append -Encoding utf8

function Write-Log {
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

function Add-Row {
    param([string]$Tool, [string]$Required, [string]$Found, [string]$Status, [string]$Note = '')
    $script:rows += [pscustomobject]@{
        Tool = $Tool; Required = $Required; Found = $Found; Status = $Status; Note = $Note
    }
}

function Test-Cmd {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    return [bool]$cmd
}

function Invoke-Installer {
    param(
        [string]$Tool,
        [string]$WingetId = '',
        [string]$ChocoId  = '',
        [string]$ExtraArgs = ''
    )
    if ($Mode -ne 'Install') {
        $script:issues += $Tool
        Write-Log 'WARN' ("missing: {0} (re-run with -Mode Install to install)" -f $Tool)
        return $false
    }
    if ($WingetId -and (Test-Cmd 'winget')) {
        Write-Log 'INFO' ("installing {0} via winget ({1})" -f $Tool, $WingetId)
        $wgArgs = @('install','--id',$WingetId,'-e','--accept-package-agreements','--accept-source-agreements','--silent')
        if ($ExtraArgs) { $wgArgs += $ExtraArgs.Split(' ') }
        & winget @wgArgs | Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -eq 0) { Write-Log 'OK' ("installed: {0}" -f $Tool); return $true }
        Write-Log 'WARN' ("winget install failed for {0} (exit {1}); trying choco" -f $Tool, $LASTEXITCODE)
    }
    if ($ChocoId -and (Test-Cmd 'choco')) {
        Write-Log 'INFO' ("installing {0} via choco ({1})" -f $Tool, $ChocoId)
        & choco install $ChocoId -y | Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -eq 0) { Write-Log 'OK' ("installed: {0}" -f $Tool); return $true }
    }
    Write-Log 'FAIL' ("could not install {0} automatically" -f $Tool)
    $script:issues += "$Tool (install failed)"
    return $false
}

function Parse-Version {
    param([string]$Text)
    if ($Text -match '(\d+)\.(\d+)\.(\d+)') { return [version]$Matches[0] }
    if ($Text -match '(\d+)\.(\d+)')        { return [version]($Matches[0] + '.0') }
    return $null
}

# ---------------------------------------------------------------------------
# 1. Host OS
# ---------------------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$osCaption = $os.Caption
$osBuild   = $os.BuildNumber
$supportedServer = $osCaption -match 'Windows Server (2019|2022|2025)'
if ($supportedServer) {
    Write-Log 'OK' ("OS: {0} (build {1})" -f $osCaption, $osBuild)
    Add-Row 'Windows Server' '2019/2022/2025' $osCaption 'OK'
} elseif ($osCaption -match 'Windows (10|11)') {
    Write-Log 'WARN' ("OS: {0} - dev OK, but production target is Windows Server 2019/2022/2025" -f $osCaption)
    Add-Row 'Windows Server' '2019/2022/2025' $osCaption 'WARN' 'client OS ok for dev only'
} else {
    Write-Log 'FAIL' ("OS: {0} - unsupported. PRD requires Windows Server 2019+." -f $osCaption)
    Add-Row 'Windows Server' '2019/2022/2025' $osCaption 'FAIL'
    $script:issues += 'Unsupported OS'
}

# ---------------------------------------------------------------------------
# 2. PowerShell 7+
# ---------------------------------------------------------------------------
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCmd) {
    $pwshVer = & pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    $v = Parse-Version $pwshVer
    if ($v -and $v -ge [version]'7.4.0') {
        Write-Log 'OK' ("pwsh {0}" -f $pwshVer); Add-Row 'PowerShell 7' '>=7.4' $pwshVer 'OK'
    } else {
        Write-Log 'WARN' ("pwsh {0} present but PRD wants >=7.4" -f $pwshVer)
        Add-Row 'PowerShell 7' '>=7.4' $pwshVer 'WARN'
        Invoke-Installer -Tool 'PowerShell 7' -WingetId 'Microsoft.PowerShell' | Out-Null
    }
} else {
    Add-Row 'PowerShell 7' '>=7.4' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'PowerShell 7' -WingetId 'Microsoft.PowerShell' | Out-Null
}

# ---------------------------------------------------------------------------
# 3. .NET 8 SDK
# ---------------------------------------------------------------------------
if (Test-Cmd 'dotnet') {
    $sdks = & dotnet --list-sdks 2>$null
    $has8 = $sdks | Where-Object { $_ -match '^8\.' }
    if ($has8) {
        $sdkVer = ($has8 | Select-Object -First 1).Split(' ')[0]
        Write-Log 'OK' (".NET SDK {0}" -f $sdkVer); Add-Row '.NET SDK 8' '8.0.x' $sdkVer 'OK'
    } else {
        $sdkList = ($sdks -join ', ')
        Write-Log 'WARN' (".NET 8 SDK missing. present: {0}" -f $sdkList)
        Add-Row '.NET SDK 8' '8.0.x' $sdkList 'MISSING'
        Invoke-Installer -Tool '.NET 8 SDK' -WingetId 'Microsoft.DotNet.SDK.8' | Out-Null
    }
} else {
    Add-Row '.NET SDK 8' '8.0.x' '(missing)' 'MISSING'
    Invoke-Installer -Tool '.NET 8 SDK' -WingetId 'Microsoft.DotNet.SDK.8' | Out-Null
}

# ---------------------------------------------------------------------------
# 4. Git
# ---------------------------------------------------------------------------
if (Test-Cmd 'git') {
    $gitVer = (& git --version).Trim()
    Write-Log 'OK' $gitVer; Add-Row 'Git' 'any recent' $gitVer 'OK'
} else {
    Add-Row 'Git' 'any recent' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'Git' -WingetId 'Git.Git' | Out-Null
}

# ---------------------------------------------------------------------------
# 5. Python 3.11 or 3.12 (not 3.13)
# Windows ships a `python.exe` Store-alias stub that redirects to the store; it
# satisfies Get-Command but prints "Python was not found" and exits non-zero.
# Probe each candidate with its own try/catch and verify both that it ran and
# that the reported version is what we want.
# ---------------------------------------------------------------------------
$pyFound = $null
$pyRawVer = $null
foreach ($c in @('py','python','python3')) {
    if (-not (Test-Cmd $c)) { continue }
    try {
        $out = (& $c -c 'import sys;print(".".join(map(str,sys.version_info[:3])))' 2>$null) | Out-String
        $out = $out.Trim()
        if ($LASTEXITCODE -eq 0 -and $out -match '^\d+\.\d+\.\d+$') {
            $pyRawVer = $out
            if ($out -match '^3\.(11|12)\.') { $pyFound = $c; break }
        }
    } catch { continue }
}
if ($pyFound) {
    Write-Log 'OK' ("Python {0} via {1}" -f $pyRawVer, $pyFound)
    Add-Row 'Python' '3.11.x or 3.12.x' $pyRawVer 'OK'
} elseif ($pyRawVer) {
    Write-Log 'WARN' ("Python version not in 3.11/3.12 range: {0}" -f $pyRawVer)
    Add-Row 'Python' '3.11.x or 3.12.x' $pyRawVer 'WARN' '3.13 breaks LiveKit Agents plugins'
    Invoke-Installer -Tool 'Python 3.12' -WingetId 'Python.Python.3.12' | Out-Null
} else {
    Add-Row 'Python' '3.11.x or 3.12.x' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'Python 3.12' -WingetId 'Python.Python.3.12' | Out-Null
}

# ---------------------------------------------------------------------------
# 6. uv (Astral)
# ---------------------------------------------------------------------------
if (Test-Cmd 'uv') {
    $uvVer = (& uv --version).Trim()
    Write-Log 'OK' $uvVer; Add-Row 'uv' 'latest' $uvVer 'OK'
} else {
    Add-Row 'uv' 'latest' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'uv' -WingetId 'astral-sh.uv' -ChocoId 'uv' | Out-Null
}

# ---------------------------------------------------------------------------
# 7. Node.js 20 LTS
# ---------------------------------------------------------------------------
if (Test-Cmd 'node') {
    $nodeVerRaw = (& node --version).TrimStart('v')
    $nodeVer = Parse-Version $nodeVerRaw
    if ($nodeVer -and $nodeVer.Major -ge 20) {
        Write-Log 'OK' ("Node {0}" -f $nodeVerRaw); Add-Row 'Node.js' '>=20 LTS' $nodeVerRaw 'OK'
    } else {
        Write-Log 'WARN' ("Node {0} < 20" -f $nodeVerRaw)
        Add-Row 'Node.js' '>=20 LTS' $nodeVerRaw 'WARN'
        Invoke-Installer -Tool 'Node.js 20 LTS' -WingetId 'OpenJS.NodeJS.LTS' | Out-Null
    }
} else {
    Add-Row 'Node.js' '>=20 LTS' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'Node.js 20 LTS' -WingetId 'OpenJS.NodeJS.LTS' | Out-Null
}

# ---------------------------------------------------------------------------
# 8. Azure CLI 2.60+
# ---------------------------------------------------------------------------
if (Test-Cmd 'az') {
    $azRaw = (& az version --output json) | ConvertFrom-Json
    $azVer = $azRaw.'azure-cli'
    $v = Parse-Version $azVer
    if ($v -and $v -ge [version]'2.60.0') {
        Write-Log 'OK' ("Azure CLI {0}" -f $azVer); Add-Row 'Azure CLI' '>=2.60' $azVer 'OK'
    } else {
        Write-Log 'WARN' ("Azure CLI {0} < 2.60" -f $azVer)
        Add-Row 'Azure CLI' '>=2.60' $azVer 'WARN'
        Invoke-Installer -Tool 'Azure CLI' -WingetId 'Microsoft.AzureCLI' | Out-Null
    }
} else {
    Add-Row 'Azure CLI' '>=2.60' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'Azure CLI' -WingetId 'Microsoft.AzureCLI' | Out-Null
}

# ---------------------------------------------------------------------------
# 9. FFmpeg
# ---------------------------------------------------------------------------
if (Test-Cmd 'ffmpeg') {
    $ffVerRaw = (& ffmpeg -version 2>&1 | Select-Object -First 1)
    if ($ffVerRaw -match 'ffmpeg version (\S+)') {
        $ffVer = $Matches[1]
        Write-Log 'OK' ("ffmpeg {0}" -f $ffVer); Add-Row 'FFmpeg' '>=6.x' $ffVer 'OK'
    } else {
        Write-Log 'WARN' "ffmpeg present but version unparsed"; Add-Row 'FFmpeg' '>=6.x' $ffVerRaw 'WARN'
    }
} else {
    Add-Row 'FFmpeg' '>=6.x' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'FFmpeg' -WingetId 'Gyan.FFmpeg' -ChocoId 'ffmpeg' | Out-Null
}

# ---------------------------------------------------------------------------
# 10. VS 2022 Build Tools with C++ workload
# ---------------------------------------------------------------------------
$vsWhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vsWhere) {
    $vcFound = & $vsWhere -products * -requires Microsoft.VisualStudio.Workload.VCTools -property installationPath 2>$null
    if ($vcFound) {
        Write-Log 'OK' ("VS Build Tools (C++) at {0}" -f $vcFound)
        Add-Row 'VS 2022 Build Tools' 'C++ workload' $vcFound 'OK'
    } else {
        Write-Log 'WARN' 'VS Build Tools present but C++ workload missing'
        Add-Row 'VS 2022 Build Tools' 'C++ workload' '(workload missing)' 'MISSING'
        Invoke-Installer -Tool 'VS 2022 Build Tools (C++ workload)' `
            -WingetId 'Microsoft.VisualStudio.2022.BuildTools' `
            -ExtraArgs '--override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools"' | Out-Null
    }
} else {
    Add-Row 'VS 2022 Build Tools' 'C++ workload' '(missing)' 'MISSING'
    Invoke-Installer -Tool 'VS 2022 Build Tools (C++ workload)' `
        -WingetId 'Microsoft.VisualStudio.2022.BuildTools' `
        -ExtraArgs '--override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools"' | Out-Null
}

# ---------------------------------------------------------------------------
# 11. Chocolatey (fallback)
# ---------------------------------------------------------------------------
if (Test-Cmd 'choco') {
    $chocoVer = (& choco --version).Trim()
    Write-Log 'OK' ("choco {0}" -f $chocoVer); Add-Row 'Chocolatey' 'fallback PM' $chocoVer 'OK'
} else {
    Write-Log 'WARN' 'choco missing (winget is primary; choco is a fallback for a few packages)'
    Add-Row 'Chocolatey' 'fallback PM' '(missing)' 'WARN' 'optional; install manually if needed'
}

# ---------------------------------------------------------------------------
# 12. Windows Firewall state (record only; no changes)
# ---------------------------------------------------------------------------
try {
    $fw = Get-NetFirewallProfile -ErrorAction Stop | Select-Object Name, Enabled
    foreach ($p in $fw) {
        Write-Log 'INFO' ("firewall profile {0} enabled={1}" -f $p.Name, $p.Enabled)
    }
    Add-Row 'Windows Firewall' 'profile state recorded' 'see log' 'OK'
} catch {
    Write-Log 'WARN' ("firewall query failed: {0}" -f $_.Exception.Message)
    Add-Row 'Windows Firewall' 'profile state recorded' '(query failed)' 'WARN'
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ''
$rows | Format-Table Tool, Required, Found, Status, Note -AutoSize | Out-String | ForEach-Object {
    Write-Host $_
    $_ | Out-File -FilePath $logPath -Append -Encoding utf8
}

if ($issues.Count -gt 0) {
    Write-Log 'FAIL' ("preflight found {0} issue(s): {1}" -f $issues.Count, ($issues -join ', '))
    if ($Mode -eq 'Check') {
        Write-Host ''
        Write-Host 'Re-run with:  pwsh -File .\scripts\preflight-check.ps1 -Mode Install' -ForegroundColor Yellow
    }
    exit 1
}

Write-Log 'OK' 'preflight passed'
exit 0
