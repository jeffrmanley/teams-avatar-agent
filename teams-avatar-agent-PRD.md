# PRD: Real-time AI Avatar Teams Participant

**Target executor:** Claude Code running on Windows Server 2022
**Owner:** Jeff Manley
**Status:** v0.1 — handoff draft
**Related research:** Real-time AI Avatars in Microsoft Teams feasibility report (April 2026)

---

## 0. How to use this PRD

This PRD is written for Claude Code to execute directly on a Windows Server 2022 host. Work the phases in order. Do **not** skip the preflight. Every phase has explicit acceptance criteria; do not proceed to the next phase until the current phase's criteria pass.

**Ground rules for the executor:**
- Before installing anything, check if it's already present at the correct version. Log every `found` / `installed` / `upgraded` decision to `./logs/preflight.log`.
- All shell commands target **PowerShell 7+** (`pwsh`), not Windows PowerShell 5.1. If only 5.1 is present, install 7 first.
- Never hardcode secrets. Write them to `.env` (git-ignored) and load via `dotenv` (Python) / `DotNetEnv` (C#).
- When a step requires a human action (Azure tenant admin consent, DNS record, buying a domain), **stop and print a clearly-labeled `HUMAN ACTION REQUIRED`** block with the exact steps, then wait.
- If a package manager is missing, install it; do not try to work around missing `winget`, `choco`, or `uv`.
- The repo lives at `C:\src\teams-avatar-agent`. Create it if missing.

---

## 1. Objective and scope

**Build an AI agent that joins a Microsoft Teams meeting as a full participant — with a photorealistic lip-synced avatar, real-time voice, and the ability to hear, respond, and be interrupted — driven by Claude Sonnet 4.5.**

### In scope (v1)

- Joining a scheduled Teams meeting by meeting URL or deep link.
- Rendering the agent as a visible participant with live video tile and audio output.
- Hearing other participants (mixed audio, streaming STT).
- Reasoning with Claude Sonnet 4.5 over the rolling conversation.
- Speaking with a streaming TTS voice and lip-synced photoreal avatar.
- Barge-in / interruption handling (<300 ms cancel-and-redirect).
- Disclosure: persistent "AI" badge on the tile, first-turn audible + chat disclosure card (EU AI Act Art. 50 compliance, effective 2 Aug 2026).

### Out of scope (v1)

- Screen-share comprehension (seeing shared content). Flagged as v2.
- Multi-speaker diarization beyond "someone is speaking vs not."
- Cross-tenant compliance recording policies.
- Phone (PSTN) participants.
- Zoom / Google Meet adapters (can share LiveKit Agents core later; not this build).
- Mobile/web embedded avatar UI outside Teams.

### Non-goals

- This build explicitly does **not** use the Recall.ai browser-automation path. That path is on a collision course with Microsoft's May–June 2026 (MC1251206) bot-detection rollout and is not a durable foundation.
- This build explicitly does **not** use Remotion. Remotion is an offline React-based video renderer and is not suitable for real-time streaming output.

---

## 2. Architecture (Architecture B from the feasibility report)

```
  Teams meeting (SFU)
         │  (Opus audio + H.264 video, UDP/SRTP)
         │
  ┌──────┴──────────────────────────────────────────────┐
  │ Graph Communications Media Bot (C# / .NET 8)        │
  │ Host: Windows Server 2022 + ILPIP                   │
  │ ─ IAudioSocket (Teams ↔ bot)                        │
  │ ─ IVideoSocket (bot → Teams, pre-encoded H.264 NAL) │
  │ ─ LiveKit Server SDK peer (joins our LiveKit room)  │
  └──────┬──────────────────────────────────────────────┘
         │  audio Opus / frames  (in-DC or cloud region co-located)
         │
  ┌──────┴──────────────────────────────────────────────┐
  │ LiveKit room "meeting-{id}"                         │
  │ Publishers: bot (audio from Teams), avatar (A/V out)│
  │ Subscribers: bot (A/V to Teams)                     │
  └──────┬──────────────────────────────────────────────┘
         │
  ┌──────┴─────────────────┐    ┌────────────────────────┐
  │ LiveKit Agents (Python)│    │ Beyond Presence avatar │
  │ ─ Deepgram Nova-3 STT  │    │ ─ Joins our LiveKit    │
  │ ─ Claude Sonnet 4.5    │────│   room with            │
  │ ─ Cartesia Sonic-3 TTS │    │   livekit_url+token    │
  │ ─ Turn detector v2     │    │   WE provide           │
  │ ─ Agent loop/memory    │    │ ─ Publishes 1080p A/V  │
  └────────────────────────┘    └────────────────────────┘
```

**Why this shape:**

- Beyond Presence is the only avatar vendor whose API accepts a LiveKit room we control, so the avatar is just another publisher in our room. Every other vendor requires bridging between their LiveKit/Daily room and ours (extra hop, extra latency, extra codec pain).
- The C# bot is the **only** code path that runs on Windows. Everything else is Linux/Python and can run on any box; colocate for latency but it's not required.
- LiveKit is the seam. The bot only cares about a LiveKit room. The agent stack only cares about a LiveKit room. Replace either half without touching the other.

### Target latency (steady state, turn 2+)

| Stage | Budget |
|---|---|
| End-of-turn detection (LiveKit turn-detector v2) | 250–400 ms |
| STT finalize | 50–150 ms (parallel) |
| Claude Sonnet 4.5 TTFT (prompt-cached) | 400–600 ms |
| TTS TTFB (Cartesia Sonic-3) | 75–150 ms |
| Avatar first-frame + Teams egress | 200–450 ms |
| **Total mouth-closed → first syllable** | **~1.1–1.4 s** |

---

## 3. Preflight (Phase 0) — environment verification and setup

Claude Code must complete this phase **before writing any application code**. The goal is a reproducible, fully-verified dev environment.

### 3.1 Host verification

Run `./scripts/preflight-check.ps1` (Claude Code creates this). It must verify and log:

| Check | Required | Remediation |
|---|---|---|
| Windows Server edition | 2019 or 2022 | Stop with `HUMAN ACTION REQUIRED` if unsupported |
| PowerShell | 7.4+ | `winget install Microsoft.PowerShell` |
| .NET SDK | 8.0.x | `winget install Microsoft.DotNet.SDK.8` |
| Git | any recent | `winget install Git.Git` |
| Python | 3.11.x or 3.12.x (**not** 3.13 — LiveKit Agents plugin compatibility) | `winget install Python.Python.3.12` |
| uv | latest | `pip install uv` or `winget install astral-sh.uv` |
| Node.js | 20 LTS | `winget install OpenJS.NodeJS.LTS` |
| Azure CLI | 2.60+ | `winget install Microsoft.AzureCLI` |
| FFmpeg | 6.x+ on PATH | `winget install Gyan.FFmpeg` |
| Visual Studio Build Tools | 2022 with C++ workload (needed for some native Python deps) | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools"` |
| Chocolatey | as fallback for packages winget misses | Install if missing |
| Windows Firewall | profile on, allowlist scripted later | Leave on; preflight just records state |

### 3.2 Network and public endpoint

The Graph Communications media bot **requires**:
- A **public DNS name** with a valid TLS cert (Let's Encrypt via win-acme, or a cert uploaded to the VM).
- **Instance-Level Public IP (ILPIP)** on the Azure VM (if running in Azure), because app-hosted media bots cannot sit behind a shared load-balancer VIP. If running on a different public host, it needs a directly-addressable public IPv4.
- Outbound **UDP 3478–3481** to Microsoft Teams media ranges (`52.112.0.0/14`, `52.120.0.0/14`).
- Inbound **TCP 443** (HTTPS signaling from Graph) and a configured UDP range for media (`8445` default, adjustable).

Claude Code should:
1. Print `HUMAN ACTION REQUIRED: confirm this host has a public DNS name and ILPIP. Enter the FQDN (e.g. bot.yourdomain.com):` and wait for input.
2. Store the FQDN in `.env` as `BOT_PUBLIC_FQDN`.
3. Verify the FQDN resolves to the host's public IP (`Resolve-DnsName`).
4. Run a basic outbound UDP reachability test to `worldaz.relay.teams.microsoft.com:3478` (fail-soft; record result).

### 3.3 Azure and Microsoft registrations

These require human action. Claude Code should generate a **`docs/azure-setup-runbook.md`** with the exact steps, then wait. The human performs them and pastes IDs into `.env`.

Required resources:

1. **Entra ID (Azure AD) app registration**
   - App name: `teams-avatar-agent`
   - Application permissions (not delegated): `Calls.JoinGroupCall.All`, `Calls.JoinGroupCallAsGuest.All`, `Calls.AccessMedia.All`, `OnlineMeetings.Read.All`.
   - Client secret (record `APP_CLIENT_SECRET`).
   - Record `APP_CLIENT_ID` and `TENANT_ID`.
   - **Admin consent required** for every tenant that hosts meetings the bot joins (v1: just your dev tenant).

2. **Azure Bot resource**
   - Resource type: `Microsoft.BotService/botServices`, kind `azurebot`.
   - Messaging endpoint: `https://{BOT_PUBLIC_FQDN}/api/calling`
   - Channels: enable **Microsoft Teams**, and under Teams channel select **Calling** → tick "Enable calling", webhook `https://{BOT_PUBLIC_FQDN}/api/calling`.
   - Record `BOT_APP_ID` (usually == `APP_CLIENT_ID` for app-hosted media).

3. **Teams app manifest**
   - `supportsCalling: true`, `supportsVideo: true`, `isNotificationOnly: false`.
   - `bots[].id` = `BOT_APP_ID`.
   - Sideload via Teams Admin Center → Teams apps → Manage apps → Upload.

4. **Teams calling policy**
   - Assign a policy to the test user that allows private calling and meeting joins from the app.

### 3.4 Third-party accounts and keys

Generate `.env.example` with placeholders and a `docs/accounts-checklist.md` listing each with signup URLs and expected monthly cost at 100 hours of meetings.

| Service | Env var | Purpose | Signup |
|---|---|---|---|
| Anthropic | `ANTHROPIC_API_KEY` | Claude Sonnet 4.5 | console.anthropic.com |
| Deepgram | `DEEPGRAM_API_KEY` | Nova-3 streaming STT | deepgram.com |
| Cartesia | `CARTESIA_API_KEY` | Sonic-3 streaming TTS | cartesia.ai |
| Beyond Presence | `BEY_API_KEY` | Photoreal avatar | bey.dev |
| LiveKit | `LIVEKIT_URL`, `LIVEKIT_API_KEY`, `LIVEKIT_API_SECRET` | WebRTC room | cloud.livekit.io (free tier fine for dev) |

### 3.5 Repository skeleton

Claude Code creates:

```
C:\src\teams-avatar-agent\
├─ .env.example
├─ .gitignore
├─ README.md
├─ docs\
│   ├─ azure-setup-runbook.md
│   ├─ accounts-checklist.md
│   └─ architecture.md
├─ scripts\
│   ├─ preflight-check.ps1
│   ├─ install-win-acme.ps1
│   └─ smoke-test.ps1
├─ agent\                     # Python LiveKit Agents app
│   ├─ pyproject.toml
│   ├─ uv.lock
│   ├─ agent.py
│   ├─ prompts\
│   │   └─ system.md
│   └─ tests\
├─ bot\                       # C# .NET 8 Graph Comms media bot
│   ├─ TeamsAvatarBot.sln
│   ├─ src\
│   │   ├─ TeamsAvatarBot.Host\        # ASP.NET Core host, calling webhook
│   │   ├─ TeamsAvatarBot.Core\        # Call handling, media socket wiring
│   │   └─ TeamsAvatarBot.LiveKitBridge\# LiveKit server-SDK peer + transcoder
│   └─ tests\
└─ logs\
```

### 3.6 Phase 0 acceptance criteria

- `./scripts/preflight-check.ps1` returns exit 0, all rows logged to `./logs/preflight.log`.
- `.env` is populated and loadable via both Python `python-dotenv` and C# `DotNetEnv`.
- `az login` succeeds; `az account show` shows the expected tenant.
- A LiveKit connection test (`lk cloud auth` + `lk room create test-room --url $LIVEKIT_URL ...`) succeeds.
- A Claude API smoke test (one `/messages` call to `claude-sonnet-4-5`) returns a 200.
- A Deepgram, Cartesia, and Beyond Presence auth check each return 200.
- Repository structure exists, git initialized, first commit made.

---

## 4. Phase 1 — LiveKit Agents orchestration (Python)

Build this first. It's fully testable without Teams — you can validate the whole Claude + STT + TTS + avatar loop in a browser against the LiveKit playground before you touch the Graph bot.

### 4.1 Dependencies (`agent/pyproject.toml`)

Use `uv` to manage the venv. Pin these; LiveKit Agents moves fast and breaking changes land in minor bumps.

```toml
[project]
name = "teams-avatar-agent"
requires-python = ">=3.11,<3.13"
dependencies = [
  "livekit-agents[deepgram,cartesia,anthropic,silero,turn-detector,bey]>=1.5.0,<2.0.0",
  "python-dotenv>=1.0.0",
  "structlog>=24.0.0",
]
```

**Verification:** `uv sync`, then `uv run python -c "import livekit.agents; import livekit.plugins.bey; import livekit.plugins.anthropic; import livekit.plugins.deepgram; import livekit.plugins.cartesia; print('ok')"` must print `ok`.

### 4.2 Agent implementation (`agent/agent.py`)

Single-file entrypoint using LiveKit Agents 1.5+ `AgentSession` pattern.

**Hard requirements:**
- LLM: `anthropic.LLM(model="claude-sonnet-4-5", temperature=0.6)` — do **not** enable extended thinking, it kills TTFT.
- STT: `deepgram.STT(model="nova-3", language="multi", endpointing_ms=25)` — let the LiveKit turn-detector do endpointing, not Deepgram.
- TTS: `cartesia.TTS(model="sonic-3", voice="<chosen_voice_id>")`.
- Turn detection: `livekit.plugins.turn_detector.EnglishModel()` — semantic end-of-turn; beats VAD-only.
- Avatar: `bey.AvatarSession(avatar_id="<chosen_bey_avatar_id>")` attached via `session.start(room=..., room_input_options=..., room_output_options=RoomOutputOptions(audio_enabled=False))`. Audio disabled on room output because Beyond Presence republishes synced A/V.
- **Prompt caching enabled** on system prompt: set `cache_control={"type": "ephemeral"}` on the system block via the plugin's cache option. Goal: cache-read on every turn after turn 1, cutting Claude TTFT ~40%.
- Interruption: enabled by default in `AgentSession` v1.5+; verify via `session.on("user_state_changed")` — on `speaking` within TTS window, the plugin should cancel the in-flight TTS and purge queued avatar frames.

**System prompt (`agent/prompts/system.md`):**
- Identity: clearly discloses that the agent is an AI assistant. Name it (e.g., "Iris").
- Turn-taking: short turns, don't monologue, cede on disagreement signal.
- EU AI Act Art. 50 compliance: on first turn of every meeting, the agent **must** audibly say "Hi, I'm Iris, an AI assistant participating in this meeting." This is non-negotiable and tested in `tests/test_disclosure.py`.

### 4.3 Phase 1 acceptance criteria

- `uv run python agent/agent.py dev` starts and connects to LiveKit.
- Using the LiveKit Agents Playground (`https://agents-playground.livekit.io/`), a human can join a room, speak, and see:
  - STT transcript appears in <1 s.
  - Claude response starts streaming in <800 ms after EoT.
  - Cartesia audio is audible with natural prosody.
  - Beyond Presence avatar video is lip-synced, <500 ms A/V skew.
  - Interrupting the avatar mid-sentence cancels playback in <400 ms.
- First-turn disclosure is spoken verbatim.
- `pytest agent/tests/` passes (unit tests for prompt assembly, disclosure injection, and session config).
- Resource usage: steady-state agent process <500 MB RAM, <15% of one vCPU.

---

## 5. Phase 2 — Graph Communications media bot (C#/.NET 8)

This is the hard phase. Two sub-phases: (a) join a meeting and exchange signaling; (b) wire the media sockets.

### 5.1 NuGet packages (`bot/src/TeamsAvatarBot.Host/TeamsAvatarBot.Host.csproj`)

```xml
<PackageReference Include="Microsoft.Graph.Communications.Calls" Version="1.2.0.15690" />
<PackageReference Include="Microsoft.Graph.Communications.Calls.Media" Version="1.2.0.15690" />
<PackageReference Include="Microsoft.Graph.Communications.Client" Version="1.2.0.15690" />
<PackageReference Include="Microsoft.Skype.Bots.Media" Version="1.27.0.19-alpha" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="DotNetEnv" Version="3.*" />
<PackageReference Include="livekit-server-sdk" Version="*" />   <!-- for room tokens -->
```

**The `Microsoft.Skype.Bots.Media` package is native and Windows-only.** It is the reason this project cannot run on Linux.

### 5.2 Reference implementation

Start from `microsoftgraph/microsoft-graph-comms-samples`, specifically `Samples/V1.0Samples/LocalMediaSamples/AudioVideoPlaybackBot`. Claude Code clones this to `./bot/reference/` and uses it as a structural reference — do not copy wholesale, strip the playback logic we don't need.

### 5.3 Key components to build

**`TeamsAvatarBot.Host`** — ASP.NET Core web app exposing:
- `POST /api/calling` — Graph calling webhook. Must respond in <3s or Graph retries.
- `POST /api/incoming/{callId}` — Per-call notification endpoint.
- `GET /healthz` — liveness.
- `GET /api/join` — dev endpoint that accepts a Teams meeting join URL and initiates the bot join flow. Auth with a shared secret header in dev.

**`TeamsAvatarBot.Core`** — call handling:
- `CallHandler` implements `ICallHandler`, subscribes to `OnIncoming`, `OnUpdate`.
- On join, attach a `LocalMediaSession` with one `AudioSocket` (send + receive, stereo 16 kHz PCM) and one `VideoSocket` (send-only, H.264, 1280×720 @ 30 fps — do not try 1080p, Teams caps egress around 720p/1 Mbps anyway).
- Handle `ParticipantsUpdated` to track speaker activity (we mirror this to the Agents side for attention cues).

**`TeamsAvatarBot.LiveKitBridge`** — the engineering-intensive half:
- On bot joining the Teams call, create a LiveKit room `meeting-{callId}`, mint tokens for (a) the bot's server peer and (b) the Beyond Presence publisher. Push the Bey token to the Agents service via a Redis channel or a simple HTTP callback.
- **Bot → LiveKit (inbound audio from Teams):** `AudioSocket.AudioMediaReceived` fires with 20 ms PCM frames. Buffer, re-encode to Opus (use `Concentus` or pipe through FFmpeg via a persistent stdin process — FFmpeg is simpler, measurable latency ~10 ms), publish to LiveKit as an audio track using the LiveKit C# server SDK or a thin Rust-FFI wrapper. **Gotcha:** the server SDK's track-publishing support in C# is thinner than Python — test this early. If it blocks, stand up a small Go or Python sidecar that does only this bridge and IPC over local UDS.
- **LiveKit → Bot (outbound audio to Teams):** subscribe to Beyond Presence's audio track, decode Opus → 16 kHz mono PCM, feed to `AudioSocket.Send`.
- **LiveKit → Bot (outbound video to Teams):** subscribe to Bey's video track. Bey publishes H.264 by default (confirm on session start). Repackage NAL units and submit to `VideoSocket.Send` as pre-encoded H.264 Annex-B. **Do not** decode→re-encode; that blows the latency budget. If Bey negotiates VP8, force H.264 via codec preference on the LiveKit join, or eat the transcode cost (+100–200 ms, measure first).

### 5.4 Known gotchas Claude Code must design around

Lift these from the research; each has cost at least one team a week:

1. **ILPIP is mandatory.** If you see `ServiceUnavailable` or 500s from the media platform on call setup, check this first.
2. **VM-pinned calls.** A call belongs to the exact VM instance that accepted it; no graceful drain. Shut down = call drops. Design your autoscale rules accordingly (scale out, never in while sessions active).
3. **Bot may not render as a video tile.** Even with correct frame submission, app-hosted media bots sometimes render as audio-only unless running under a compliance-recording policy. Flag this as a **Phase 2 exit-criterion risk** — if hit, evaluate: (a) apply a compliance policy, which constrains to single-tenant, or (b) accept audio-only with a static poster image for v1 and revisit.
4. **Consent banner does not fire automatically** for `Calls.AccessMedia.All`. Two-party-consent states may require explicit `updateRecordingStatus` plus your own in-app disclosure. Legal review required before demo to external parties.
5. **H.264 encoding is CPU only.** Pre-encoded NAL submission is the only path that scales beyond 1–2 sessions per core. Do not try to encode in the bot process.
6. **Outbound UDP codec-transcoding latency is real.** Measure on day one; if >150 ms, negotiate codecs harder.

### 5.5 Phase 2 acceptance criteria

- Bot successfully accepts incoming-call notification from Graph and answers with `applicationHostedMedia`.
- Bot joins a scheduled Teams meeting via `/api/join?meetingUrl=...`.
- In Teams UI, the bot appears in the participant roster with its app name and a "Bot" tag.
- Audio bridge is bidirectional: a human in Teams hears the agent; the agent hears the human (verified by Deepgram transcripts appearing in the Agents log).
- Video tile is visible with Beyond Presence avatar video (or the risk in §5.4.3 is explicitly triaged in writing).
- Call teardown is clean: LiveKit room is destroyed, Bey session ended, no orphaned resources (verify via a scripted load test of 10 sequential meetings).
- All p95 latencies logged: end-of-user-speech → first-bot-audio-frame < 1600 ms.

---

## 6. Phase 3 — Integration, disclosure UX, and hardening

### 6.1 Disclosure UX (Art. 50 compliance)

- On join, post a message to the meeting chat via Graph API: "👋 Iris (AI assistant) has joined this meeting. This participant is powered by AI. Audio and transcripts are processed by Deepgram and Anthropic; no recordings are stored unless the meeting organizer enables recording."
- First agent turn begins with the audible disclosure (tested in Phase 1).
- Avatar tile overlay: a small "AI" badge burned into the outgoing video (SVG pre-rendered, composited server-side in the LiveKit Bridge before submit-to-IVideoSocket).
- Maintain a per-meeting audit log: participants, join/leave timestamps, transcript pointer, model+voice versions used. Store in `./logs/meetings/{meetingId}/` for v1; move to managed store later.

### 6.2 Observability

- Structured logs (Serilog C#, structlog Python), correlation ID = `callId`.
- Per-turn metrics: `eot_ms`, `stt_final_ms`, `claude_ttft_ms`, `claude_total_ms`, `tts_ttfb_ms`, `avatar_first_frame_ms`, `total_turn_ms`. Emit as JSON lines to `./logs/turns/`.
- Simple dashboard: a small Python script that tails turn logs and prints p50/p95 rolling. Swap for OpenTelemetry + Grafana in v1.1.

### 6.3 Failure handling

- LLM error → agent says "Sorry, I lost my train of thought — could you repeat that?" and logs.
- STT stall > 3 s → reconnect transparently.
- Avatar session drop → agent continues audio-only with a static poster image via a fallback video track (pre-registered in the LiveKit Bridge at bot startup).
- Bot process crash mid-meeting = unrecoverable (VM-pinned); design the supervisor to restart cleanly and leave a clear log trail. Do **not** attempt auto-rejoin in v1.

### 6.4 Phase 3 acceptance criteria

- A 30-minute scripted meeting runs end to end with:
  - Correct disclosure (audible + chat + badge).
  - p95 turn latency < 1.8 s.
  - Zero memory leaks (RSS stable over 30 min in both bot and agent).
  - Clean teardown artifacts in `./logs/meetings/`.
- Manual interruption test: a human talks over the agent mid-sentence; agent stops speaking within 400 ms and acknowledges ("sorry, go ahead").
- Manual attention test: two humans talking to each other without addressing the agent for 2 minutes; the agent does not interject.

---

## 7. Security and secrets

- `.env` is in `.gitignore`. Pre-commit hook (husky or a plain PowerShell hook) rejects commits containing any string matching `sk_`, `sntrys_`, `AKIA[0-9A-Z]{16}`, or any value present in `.env`.
- Client secrets are rotated every 90 days; document rotation in `docs/runbook-rotate-secrets.md`.
- The bot's HTTPS cert is renewed via win-acme scheduled task; verify renewal succeeds once before calling Phase 0 done.
- No PII in logs beyond the meeting participants' UPNs; transcripts go to `./logs/meetings/{id}/transcript.jsonl` with 30-day retention (v1 manual cleanup script).

---

## 8. Explicit risks Claude Code should not pretend away

1. **Video tile visibility** (§5.4.3). Until Phase 2 demonstrates a visible avatar tile on a non-compliance-recording call, treat this as a project-defining unknown. If it fails, v1 ships audio-only.
2. **Microsoft bot-detection rollout (MC1251206)** targets browser-automation bots, not app-hosted media bots, but the feature's classifier is opaque. There is a non-zero chance Teams organizers will see a warning banner when *any* third-party bot joins. Monitor Microsoft roadmap post-GA.
3. **LiveKit C# server SDK track-publishing maturity** (§5.3). If blocked, the fallback is a Python sidecar; budget 3–5 days if this happens.
4. **EU AI Act Art. 50** is enforceable 2 Aug 2026. The disclosure implementation in §6.1 is our good-faith attempt; legal review is required before any external-customer demo. Add a legal-review gate in the project tracker.

---

## 9. Out-of-scope for the executor (do not do these automatically)

Claude Code must **stop and ask** before doing any of the following:
- Creating Azure resources that incur cost beyond the free tier.
- Buying or registering a domain.
- Uploading a Teams app manifest to production tenants.
- Running any paid API load test beyond $5 cumulative spend across all vendors.
- Opening firewall ports beyond the minimum set in §3.2.
- Pushing to any remote git host.

---

## 10. Done criteria for v1

- A human can schedule a Teams meeting, click "Invite Iris" in a small web UI (dev-only is fine), and see Iris join within 15 seconds.
- Iris participates in a 30-minute conversation with natural turn-taking, interruption handling, and photorealistic avatar.
- Meeting leaves a clean audit trail, no orphaned cloud resources, no secrets in git.
- Total cost per meeting-minute, logged, is within 2× of the $0.35–0.50/min projection from the feasibility study.
- Runbook exists for: (a) rotating secrets, (b) onboarding a new tenant, (c) rolling back a bad deploy, (d) triaging "Iris joined but isn't responding."

---

## 11. First commands Claude Code should run

```powershell
# 1. Make the repo.
New-Item -ItemType Directory -Force -Path C:\src\teams-avatar-agent | Out-Null
Set-Location C:\src\teams-avatar-agent
git init
git branch -M main

# 2. Create the folder skeleton from §3.5.
$dirs = @('docs','scripts','agent\prompts','agent\tests',
         'bot\src\TeamsAvatarBot.Host','bot\src\TeamsAvatarBot.Core',
         'bot\src\TeamsAvatarBot.LiveKitBridge','bot\tests','logs')
$dirs | ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

# 3. Write the preflight script (scripts/preflight-check.ps1) per §3.1.
# 4. Run it.
pwsh -File .\scripts\preflight-check.ps1

# 5. Stop and surface HUMAN ACTION REQUIRED for Azure registrations (§3.3)
#    and accounts (§3.4). Wait for .env to be populated before proceeding.
```
