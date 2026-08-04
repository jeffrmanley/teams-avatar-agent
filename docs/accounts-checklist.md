# Accounts checklist

Sign up for each service below and paste the resulting keys into `.env` (use `.env.example` as the template).

This document reflects what we **actually did** as of 2026-04-27, not what the v0.1 PRD originally said. See `docs/decisions.md` for the ADR-level reasoning behind each choice.

## Status tracker

| Service | Signup | Env var(s) | Cost / 30-min meeting | Status |
|---|---|---|---|---|
| LiveKit Cloud | <https://cloud.livekit.io> | `LIVEKIT_URL`, `LIVEKIT_API_KEY`, `LIVEKIT_API_SECRET` | ~$0.45 (free tier covers dev) | ☐ |
| Anthropic | <https://console.anthropic.com> | `ANTHROPIC_API_KEY` | ~$0.22 (with prompt caching) | ☐ |
| Deepgram | <https://console.deepgram.com> | `DEEPGRAM_API_KEY` | ~$0.13 (Nova-3, $0.0043/min) | ☐ |
| ElevenLabs | <https://elevenlabs.io> | `ELEVEN_API_KEY`, `ELEVEN_VOICE_ID` | $0 in 2026 (Lenny's Pass — Creator, 1 yr free) | ☐ |
| **HeyGen LiveAvatar** *(primary)* | <https://liveavatar.com> | `LIVEAVATAR_API_KEY`, `LIVEAVATAR_AVATAR_ID` | ~$3.00 ($19/mo Starter for dev, $100/mo Essential for ~280 min/mo) | ☐ |
| Beyond Presence *(fallback)* | <https://www.beyondpresence.ai> | `BEY_API_KEY`, `BEY_AVATAR_ID` | ~€5.25/30-min on overages above plan; €49/mo Starter for 280 min | ☐ |
| Cloudflare *(DNS + cert renewal)* | <https://cloudflare.com> | `CLOUDFLARE_API_TOKEN` | $0 | ☐ |
| Microsoft 365 + Azure | see `docs/azure-setup-runbook.md` | `TENANT_ID`, `APP_CLIENT_ID`, `APP_CLIENT_SECRET`, `BOT_APP_ID`, `BOT_PUBLIC_FQDN` | ~$43.20/mo M365 E3 + $0 Azure Bot F0 + $0 free Azure VM credit | ☐ |
| **Total per 30-min meeting** | | | **~$4.80** (`docs/vendor-costs.md`) | |

---

## Per-service setup notes

### 1. LiveKit Cloud

1. Sign up at cloud.livekit.io.
2. Create a project named `teams-avatar-agent-dev`. Pick a region close to where your bot host lives (latency-sensitive).
3. **Skip the "Agent Builder" prompt** — we build in code, not their no-code tool.
4. **Disable Agent Observability** during onboarding — we capture per-turn metrics locally per `docs/dataflow.md`. Costs money beyond free tier, and adds a privacy compliance variable we don't need.
5. Project Overview → **Project API keys → Create new key**. Name `teams-avatar-agent-dev`.
6. Copy the **WebSocket URL** (`wss://<project>-<id>.livekit.cloud`) → `LIVEKIT_URL`.
7. Copy **API Key** → `LIVEKIT_API_KEY`, **API Secret** → `LIVEKIT_API_SECRET`. The secret is shown once; if you lose it, generate a new pair.

### 2. Anthropic

1. Sign up at console.anthropic.com.
2. **Plans & Billing → Add credits** — drop at least $20. Without a balance the API key returns 400s.
3. **API Keys → Create Key**. Name: `teams-avatar-agent-dev`. Workspace: default. Permission: All.
4. Copy the key (`sk-ant-...`) → `.env` `ANTHROPIC_API_KEY`. Shown once.

**Model used:** `claude-sonnet-4-5`. Do **not** enable extended thinking (kills TTFT).

### 3. Deepgram

1. Sign up at <https://console.deepgram.com/signup>. Free signup includes $200 credit — enough for ~700 hours of Nova-3 STT, no card required for dev.
2. **API Keys → Create a New API Key**.
   - Name: `teams-avatar-agent-dev`
   - Permissions: **Member** (read+write to your project; don't use Admin)
   - Expiration: leave default (no expiry) for dev
3. Copy the key → `.env` `DEEPGRAM_API_KEY`. Shown once.

**Model used:** `nova-3`, `language="multi"`, `endpointing_ms=25` (turn-detection is owned by LiveKit Agents, not Deepgram).

### 4. ElevenLabs

1. Sign up at elevenlabs.io. Activate the **Creator** plan via Lenny's Product Pass (1 year free, ~$22/mo equivalent).
2. **Voice Library** (or My Voices) → pick a voice; copy its **Voice ID** → `ELEVEN_VOICE_ID`. We chose `DXFkLCBUTmvXpp2QwZjA` (Eryn) for v1.
3. **Profile (top-right) → API Keys → Create API Key**.
   - Name: `teams-avatar-agent-dev`
   - **Endpoints to enable (least privilege):**
     - **Text to Speech** → Access (the core API call)
     - **Models** → Access (plugin queries available models on startup)
     - **Voices** → Read (plugin fetches voice settings for the configured voice)
     - **User** → Read (used by `scripts/smoke-test.ps1`)
   - Leave everything else at **No Access** (Speech-to-Speech, Speech-to-Text, Sound Effects, Audio Isolation, Music, Dubbing, ElevenAgents, Projects, Audio Native, Voice Generation, Forced Alignment, History, all admin/workspace/webhook items).
   - Credit Limit: Unlimited.
4. Copy the key → `.env` `ELEVEN_API_KEY`. **Note the env var is `ELEVEN_API_KEY` (no "S")** — the LiveKit plugin reads exactly that name.

**Model used:** `eleven_flash_v2_5` (~75 ms TTFB). The plugin's default `eleven_turbo_v2_5` is more natural but ~150–300 ms TTFB — too slow for real-time turn-taking.

### 5. HeyGen LiveAvatar *(primary avatar provider)*

⚠️ **Do not confuse two HeyGen products:**

| Product | URL | Type | Use for our case? |
|---|---|---|---|
| HeyGen Studio (Free / Creator $29 / Pro) | <https://heygen.com> | **Async** video generation (script → MP4) | **No** |
| HeyGen LiveAvatar (Starter $19 / Essential $100) | <https://liveavatar.com> | **Real-time streaming** (audio in → live video out) | **Yes** |

The HeyGen pricing page most users land on first is for **Studio**, which doesn't work for our use case at any price. Iris needs the LiveAvatar product.

Steps:

1. Sign up at <https://liveavatar.com>. Choose **Starter ($19/mo, 150 credits ≈ 75 min Full mode or 150 min Lite mode)** for development. **Set a calendar reminder to cancel before day 30** if you don't intend to keep paying.
2. Pick a stock avatar; copy its **Avatar ID** → `LIVEAVATAR_AVATAR_ID`.
3. **API Keys → Create API Key**. Copy → `LIVEAVATAR_API_KEY`. The plugin reads this env var natively.

**Mode used:** **LITE** mode (1 credit/min, BYO TTS). We bring our own ElevenLabs Flash v2.5 audio; LiveAvatar just renders lip-synced video.

**Why LiveAvatar over Beyond Presence:** see ADR-002 in `docs/decisions.md`. 4-question A/B test in the LiveKit playground on 2026-04-26; LiveAvatar 15 / BP 10 across empathy, lip-sync, interruption, and idle dimensions; voice held constant.

### 6. Beyond Presence *(fallback / re-test)*

Kept wired behind `AVATAR_PROVIDER=bey` so we can re-test without code changes if LiveAvatar regresses.

1. Sign up at <https://www.beyondpresence.ai>. Marketing site is `.ai`; the API + dashboard are still on `bey.dev`. Once signed up, the dashboard is at `app.bey.dev`.
2. **Avatars** section → pick a stock avatar → copy its **avatar id** → `BEY_AVATAR_ID`.
3. **API Keys → Create API Key**. Copy → `BEY_API_KEY`. Headers use `x-api-key`.
4. Plan: **Starter (€49/mo, 280 min Speech-to-Video included; 1 month free trial that auto-converts to paid)**. **Cancel before day 30 if not used.**

### 7. Cloudflare *(DNS + cert renewal)*

You need a domain you control with DNS managed in Cloudflare.

1. Add an **A record** for the bot's subdomain (we use `iris.<your-domain>`) pointing at the bot host's public IPv4.
2. **Critical:** set **Proxy status to DNS only (gray cloud, NOT orange)**. Cloudflare's HTTP proxy terminates TLS itself (breaks the Graph cert chain) and doesn't pass UDP at all (Teams media is SRTP/UDP).
3. **My Profile → API Tokens → Create Token** with the **"Edit zone DNS"** template:
   - Permissions: `Zone — DNS — Edit` (template default)
   - Zone Resources: Include → Specific zone → your domain
   - Client IP: leave empty
   - TTL: leave default
4. Copy → `.env` `CLOUDFLARE_API_TOKEN`. Used by `scripts/install-tls-cert.ps1` for Let's Encrypt DNS-01.

### 8. Microsoft 365 + Azure

See `docs/azure-setup-runbook.md` for the step-by-step. Output:

- `TENANT_ID` (Directory tenant ID from Entra app registration)
- `APP_CLIENT_ID` (Application client ID from Entra app registration)
- `APP_CLIENT_SECRET`
- `BOT_APP_ID` (= `APP_CLIENT_ID` for app-hosted media bots)
- `BOT_PUBLIC_FQDN` (your `iris.<domain>` from step 7)
- `BOT_INSTANCE_PUBLIC_IP` (the bot host's public IPv4)

⚠️ **M365 Family will not work** for this project — no tenant, no admin portal, no Graph Communications API access. ⚠️ **M365 Developer Program is no longer free** as of 2024 (requires Visual Studio Pro/Enterprise subscription). **The cheapest known-working path is Microsoft 365 E3 trial** ($43.20/mo month-to-month). See the runbook.

---

## Run the smoke test

```powershell
pwsh -File .\scripts\smoke-test.ps1
```

Hits Anthropic, Deepgram, ElevenLabs, Beyond Presence, and LiveKit. All five must return 200 before proceeding.
