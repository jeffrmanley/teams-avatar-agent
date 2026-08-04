# Bootstrapping a Windows Server 2025 dev environment for a real-time bot in 2026

*Article 01 of the Iris series. Companion to the runbook in [`SETUP.md`](../../SETUP.md).*

**Date:** 2026-04-26
**Author:** Jeff Manley
**TL;DR:** A fresh Windows Server 2025 box needs eleven non-trivial pieces of tooling before you can build a Graph Communications media bot plus a Python LiveKit Agents app. Most of them install cleanly with `winget`, but four of them have sharp edges that cost time on the first try: PowerShell version, the `python.exe` Microsoft Store stub, the choice between `pip+venv` and `uv`, and the C++ workload requirement on Visual Studio Build Tools. This article walks through each.

---

## Why the host is Windows Server in the first place

The C# half of the project — the Graph Communications media bot — depends on `Microsoft.Skype.Bots.Media`, a NuGet package that wraps a native Windows-only DLL. That DLL is the only thing in the entire stack that requires Windows. We considered Linux containers for the Python agent and the LiveKit bridge; we ended up running everything on the same Windows Server 2025 VM (Interserver, $30–50/mo) for v1, because:

- Latency between bot and agent goes to roughly zero when they share a host.
- Operations is one box, not two.
- The Python agent works fine on Windows under `uv`.

Long-term we'll probably split, but the simpler shape was right for now.

The host details:

- **OS:** Windows Server 2025 Standard, build 26100, fresh from Interserver's marketplace image.
- **CPU/RAM:** modest — 4 vCPU, 8 GB RAM. The bot doesn't need much; the LiveKit Agents process steady-states under 500 MB and 15% of one core.
- **Network:** direct public IPv4 (no shared VIP, no NAT). This matters more than it seems — see article 06 on M365 setup and article 07 on Cloudflare.

## The eleven pieces of tooling

Phase 0 of the PRD calls for a preflight script that verifies and installs everything we need. The script ([`scripts/preflight-check.ps1`](../../scripts/preflight-check.ps1)) takes a `-Mode Install` flag and is idempotent — running it on an already-set-up box just confirms each row is green.

Here's the validated tool list. Versions are what we landed on, not necessarily the absolute floor:

| Tool | Required | Why we need it |
|---|---|---|
| **Windows Server 2019/2022/2025** | 2025 in our case | Host the C# bot |
| **PowerShell 7.4+** (`pwsh`) | 7.4.7 | Our scripts target `pwsh`, not `powershell` |
| **.NET 8 SDK** | 8.0.x | Build the Graph Communications bot |
| **Git** | any recent | Clone the repo |
| **Python 3.11 or 3.12** (NOT 3.13) | 3.12.x | LiveKit Agents plugins don't all support 3.13 |
| **uv** | latest | Dependency + venv management for the agent |
| **Node.js 20 LTS+** | 24 in our case | LiveKit CLI; some tooling utilities |
| **Azure CLI 2.60+** | 2.x | Auth + tenant ops |
| **FFmpeg 6.x+** | 7.x in our case | Audio codec transcoding (Opus ↔ PCM) for the LiveKit bridge |
| **Visual Studio 2022 Build Tools, C++ workload** | 2022 | Some Python deps build native extensions on first install |
| **Chocolatey** | optional fallback | A few packages winget misses |

Below: the four of these that bit us.

## Sharp edge #1: PowerShell 5.1 vs 7

Windows Server 2025 ships with **Windows PowerShell 5.1** as the default `powershell` command. It does not ship with PowerShell 7. They're different shells with different parsers, different module compatibility, and — the one that cost us an afternoon — different default text encoding.

PowerShell 5.1's default output encoding is **Windows-1252** (the legacy single-byte ANSI codepage, sometimes called "ANSI"), not UTF-8. Anything you write to a file, pipe to a tool, or interpolate into a here-string with PS 5.1 gets ANSI by default. Modern tooling — `git`, `dotnet`, anything that emits JSON, anything that handles em-dashes or curly quotes — assumes UTF-8.

The specific failure mode we hit: a here-string with em-dashes (`—`) embedded in it, written to a file from PS 5.1, came back out as `` and `` garbage when read by PowerShell 7 or by any UTF-8-aware reader. The em-dashes had been silently downgraded to Windows-1252 codepoints that weren't valid UTF-8 sequences.

The lesson is that **all our scripts target PowerShell 7+**. The preflight script's first job, if `pwsh` is missing or below 7.4, is to install it via `winget install Microsoft.PowerShell`. After that, every other script in `scripts/` has `#Requires -Version 7.0` at the top so it refuses to run under 5.1.

If you're starting from scratch on a new box, do this first:

```powershell
# In Windows PowerShell 5.1 (the default):
winget install Microsoft.PowerShell

# Then close the window and open a new one as "PowerShell 7" from the Start menu.
# Verify:
$PSVersionTable.PSVersion   # should be 7.4.x or higher
```

You'll know it worked if `[System.Text.Encoding]::Default` reports `UTF-8` rather than `Windows-1252`.

## Sharp edge #2: the `python.exe` Microsoft Store stub

This one wasted two hours the first time.

Modern Windows ships a stub `python.exe` and `python3.exe` in `C:\Users\<you>\AppData\Local\Microsoft\WindowsApps\`. If you type `python --version` in a fresh shell on a clean machine, you get a popup that opens the Microsoft Store to the Python product page. That's the stub.

The stub satisfies `Get-Command python` (the file exists, it's executable, it's on PATH). It satisfies `where.exe python` (it's in WindowsApps, which is on PATH by default). What it does not do is execute Python.

Worse, the stub *does* run when you invoke it from a shell. It just prints `Python was not found; run without arguments to install from the Microsoft Store, or disable this shortcut from Settings > Apps > Advanced app settings > App execution aliases.` and exits non-zero. Some scripts catch the non-zero and report "Python not installed." Some don't, and you end up with cryptic downstream failures because Python *is* on PATH, just not Python.

Our preflight handles this defensively. Rather than trusting `Get-Command python`, it probes each candidate and verifies both that it ran *and* that the reported version matches what we want:

```powershell
$pyFound = $null
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
```

The double check — exit code zero **and** output matches a `3.X.Y` regex — is what differentiates a real Python install from the Store stub.

We picked Python 3.12 specifically. The PRD originally said "3.11 or 3.12, not 3.13" because LiveKit Agents 1.5.x and several of its plugins (most notably `livekit-plugins-bey` and the turn detector ONNX bindings) had not yet shipped 3.13 wheels at the time of writing. If you try 3.13, you get build failures during `uv sync`. Stay on 3.12 for now.

## Sharp edge #3: `winget` vs `choco` vs neither

`winget` is the right primary on Windows Server 2025. It comes preinstalled, it ships with the OS update channel, and most major packages publish to it. Our preflight uses winget by default for every install.

A few packages winget either doesn't have or has at the wrong version. For those, we keep Chocolatey as a documented fallback. The preflight script has a two-tier `Invoke-Installer` function:

```powershell
function Invoke-Installer {
    param([string]$Tool, [string]$WingetId, [string]$ChocoId, [string]$ExtraArgs)
    if ($WingetId -and (Test-Cmd 'winget')) {
        # try winget first
        & winget install --id $WingetId -e --accept-package-agreements --accept-source-agreements --silent
        if ($LASTEXITCODE -eq 0) { return $true }
    }
    if ($ChocoId -and (Test-Cmd 'choco')) {
        & choco install $ChocoId -y
        if ($LASTEXITCODE -eq 0) { return $true }
    }
    # else: surface the issue
}
```

Of the eleven tools, every single one installed cleanly via winget on Windows Server 2025. We never had to fall back to choco. We keep choco wired anyway because earlier Server SKUs occasionally have stale winget package indexes.

The one wart with `winget` on a fresh box: it sometimes gets confused about its source registrations and prints `Failed when searching source: msstore` or `Failed when searching source: winget`. The fix is `winget source reset --force`, which re-registers the default sources. Run that once on a new VM and the rest of the preflight is smooth.

## Sharp edge #4: pip+venv vs uv

The PRD called for `uv` (Astral's Rust-implemented Python package manager) over the canonical `pip` + `venv` toolchain. This was a directional bet at scaffold time that paid off.

Why we picked uv:

1. **Speed.** `uv sync` against the agent's pinned dependency set takes 8–12 seconds; the same install with `pip` + `venv` was 90+ seconds on Windows. On a project where you're iterating on a `pyproject.toml` because LiveKit Agents minor versions ship breaking changes, that 10× speedup compounds.
2. **Lockfile-first.** `uv` writes a `uv.lock` automatically, gets it right, and keeps it consistent across machines. The `pip` ecosystem has three competing answers to this (`requirements.txt`, `pip-tools`, `poetry`) and none is universal.
3. **Single binary.** No PATH ambiguity, no "which Python is this venv pointing at?" question. `uv run python agent.py` runs in the project's environment unambiguously.
4. **It handles the venv invisibly.** You don't `source venv/bin/activate` (or its PowerShell equivalent). You just `uv run` and it does the right thing.

The pyproject.toml stays simple:

```toml
[project]
name = "teams-avatar-agent"
requires-python = ">=3.11,<3.13"
dependencies = [
  "livekit-agents[deepgram,elevenlabs,anthropic,silero,turn-detector,bey]>=1.5.0,<2.0.0",
  "python-dotenv>=1.0.0",
  "structlog>=24.0.0",
]
```

`uv` reads this, resolves, writes `uv.lock`, and you're done.

The thing we did **not** anticipate: the LiveKit turn-detector v2 plugin downloads ONNX model weights at runtime, on first use. They're cached locally after the first download, but the first run of the agent has a one-time ~30-second pause for the download. Our pyproject doesn't capture that; instead, we have `uv run python agent.py download-files` as a documented post-install step. That's a LiveKit Agents convention, not a `uv` thing.

## Sharp edge #5: Visual Studio Build Tools with the C++ workload

A few Python packages in the LiveKit Agents dependency graph build native extensions on first install. On Linux, the system gets a `gcc` toolchain by default and nobody notices. On Windows, `pip` (and `uv` underneath) call out to MSVC, which means you need Visual Studio's C++ build tools installed.

The non-obvious part: installing "Visual Studio 2022 Build Tools" via winget gives you the **shell** of the build tools, but no actual compiler. You also need to add the **C++ workload** explicitly. The preflight calls it like this:

```powershell
winget install --id Microsoft.VisualStudio.2022.BuildTools `
  -e --accept-package-agreements --accept-source-agreements --silent `
  --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools"
```

The `--override` argument is what passes through to the VS Installer. Without `--add Microsoft.VisualStudio.Workload.VCTools`, you get the launcher and nothing else, and the first `uv sync` that hits a native extension fails with `error: Microsoft Visual C++ 14.0 or greater is required`.

After install, the preflight verifies the workload is actually present using `vswhere.exe`:

```powershell
$vsWhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vcFound = & $vsWhere -products * -requires Microsoft.VisualStudio.Workload.VCTools `
    -property installationPath
```

If `$vcFound` is empty, the workload isn't installed even if the launcher is. That check has saved us twice when winget reported success but the workload didn't take.

## What the validated environment looks like

After preflight runs cleanly on a fresh box, this is what `pwsh` reports:

```
PS> pwsh --version
PowerShell 7.4.7

PS> dotnet --list-sdks
8.0.404 [C:\Program Files\dotnet\sdk]

PS> python --version
Python 3.12.8

PS> uv --version
uv 0.5.x

PS> node --version
v24.0.0

PS> az --version | Select-Object -First 1
azure-cli                         2.67.0

PS> ffmpeg -version | Select-Object -First 1
ffmpeg version 7.x

PS> & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
       -products * -requires Microsoft.VisualStudio.Workload.VCTools `
       -property installationPath
C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools
```

Total time on a fresh VM, with a working internet connection, including all winget downloads: **~15 minutes** of wall-clock, of which maybe 30 seconds is human-in-the-loop (an "accept license" prompt for Build Tools). Idempotent rerun is under 10 seconds.

## What we'd skip if doing it again

A few things that turned out not to matter that we'd cut on the next pass:

- **The Azure CLI**, in retrospect, didn't earn its slot on the host. We use `az` for `az login` and to confirm tenant context, but every actual Azure resource creation in this project was done in the web portal. If we were rebuilding, we'd push `az` to "install only when needed" and let it not be a Phase 0 dependency.
- **Node.js** is on the list because LiveKit's CLI (`lk`) used to be a Node app. It's not anymore — it's a Go binary distributed as `livekit-cli`. We only kept Node because some auxiliary scripts use it. If you don't need those, you can skip it.
- **A specific Chocolatey install.** We never ended up using it as a fallback. It's fine to leave it off until/unless winget fails.

What we wouldn't change: the choice of `uv`, the PowerShell 7 floor, the explicit C++ workload check, the version-probe defense against the Python Store stub.

## Lessons

1. **Never trust `Get-Command` for Python on Windows.** Probe the binary with a real call and check both exit code and output regex.
2. **Pin to PowerShell 7 from line 1.** PS 5.1's ANSI default encoding silently corrupts non-ASCII characters in scripts written by humans or AIs. Make `pwsh` a hard requirement, not a "nice to have."
3. **`uv` over `pip+venv` for any new Python project on Windows.** The 10× speedup on `sync` and the single-binary simplicity are both real and worth the small habit change. Lockfile correctness is a nice bonus.
4. **VS Build Tools needs the C++ workload, not just the launcher.** Install with `--add Microsoft.VisualStudio.Workload.VCTools` in the override args and verify with `vswhere`. Otherwise you'll find out the hard way on the first native-extension build.
5. **Keep `winget source reset --force` in your back pocket.** Fresh Windows Server boxes occasionally have stale package source registrations that produce confusing "package not found" errors. The reset is harmless and fixes them.

---

*Code referenced in this article: [`scripts/preflight-check.ps1`](../../scripts/preflight-check.ps1), [`agent/pyproject.toml`](../../agent/pyproject.toml). Companion runbook for new contributors: [`SETUP.md`](../../SETUP.md). Next in the series: [vendor stack selection and the LiveKit seam](02-vendor-stack-selection.md).*
