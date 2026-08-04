# SETUP.md — End-to-end setup for the Teams Avatar Agent

This is the runbook for taking a fresh Windows Server box with a public IPv4 and a Cloudflare-managed domain and getting Iris up to the point of "the Phase 2 bot is running and reachable from the public internet, ready for Phase 2.3 media bridge work."

It's optimised for **a future Claude Code agent or developer who hasn't lived through the project history**. Every step is one we actually did on 2026-04-26 / 2026-04-27, with the dead-ends pruned. If you find a step that no longer works, update this file in the same PR that fixes it.

---

## 0. Prerequisites

You need:

- **Windows Server 2019, 2022, or 2025** (we're on 2025 Standard) with **direct public IPv4** and admin access. Tested on an Interserver.net VM. Azure VMs work too with ILPIP.
  > **On the host choice:** do your own research before buying. When we priced this out in
  > April 2026, Interserver was the cheapest option that met the hard requirements for this
  > workload — Windows Server with a **direct, dedicated public IPv4** (not a shared VIP or
  > NAT, which `Microsoft.Skype.Bots.Media` will not tolerate) at a monthly price that made
  > sense for a research build. Pricing, plans, and providers move fast; any host meeting
  > those requirements works, and we have no affiliation with them.
- **A domain you control with DNS in Cloudflare.** Other DNS providers can work but the cert script targets Cloudflare's API.
- **Credit card** for: M365 E3 trial, LiveAvatar Starter ($19/mo), Beyond Presence Starter (€49/mo trial, optional fallback). Other accounts (LiveKit, Anthropic, Deepgram, ElevenLabs) are free or covered by [Lenny's Product Pass](https://www.lennysproductpass.com/) — check what you already have before paying.
- **A residential / consumer ISP IP** *available somewhere* (not the VM) for the M365 signup step. Microsoft fraud-blocks signups from datacenter IPs.

What you do **not** need: VS Pro subscription, Microsoft Partner Network, Azure VM (any public IPv4 works), Microsoft 365 Developer Program (it's no longer free).

---

## 1. Clone repo + run preflight

```powershell
git clone <repo-url> C:\code\LiveOnTeams
cd C:\code\LiveOnTeams

# Verifies + installs PS7, .NET 8, Python 3.12, uv, Node, Azure CLI, FFmpeg,
# VS 2022 Build Tools (C++ workload). Idempotent.
pwsh -File .\scripts\preflight-check.ps1 -Mode Install
```

When it finishes: `pwsh --version` shows ≥7.4, `dotnet --list-sdks` shows 8.0.x, `python --version` shows 3.12.x, `uv --version`, `node --version` (≥20), `az --version`, `ffmpeg -version` all work in a fresh shell.

---

## 2. Domain + DNS (Cloudflare, ~5 min)

1. Pick a subdomain. We use `iris.<your-domain>` — short, leaves room for siblings.
2. Cloudflare Dashboard → your domain → **DNS → Records → Add record**:
   - **Type:** `A`
   - **Name:** `iris`
   - **IPv4:** the bot host's public IP (`curl -s https://api.ipify.org` from the host)
   - **Proxy status:** **DNS only (gray cloud)** — *do not enable proxy*. Cloudflare's HTTP proxy terminates TLS itself (breaks Graph cert chain) and doesn't pass UDP at all (Teams media is SRTP/UDP).
   - **TTL:** Auto
3. Verify: `nslookup iris.<your-domain> 1.1.1.1` returns the host's IP.

### Cloudflare API token (for cert renewal)

1. Cloudflare → **My Profile → API Tokens → Create Token → Use template "Edit zone DNS"**
2. Permissions: `Zone — DNS — Edit` (template default)
3. Zone Resources: **Include → Specific zone →** your domain
4. Client IP filter: leave empty
5. TTL: leave default
6. Copy the token, you'll need it for `.env` `CLOUDFLARE_API_TOKEN` later.

---

## 3. TLS cert (Posh-ACME via Cloudflare DNS-01, ~3 min)

Add `BOT_PUBLIC_FQDN=iris.<your-domain>` and `CLOUDFLARE_API_TOKEN=<token>` to `.env` (copy `.env.example` first if needed), then:

```powershell
pwsh -File .\scripts\install-tls-cert.ps1
```

Outputs:
- `certs/iris.pfx` (PFX with password `changeit`; override with `CERT_PFX_PASSWORD` env var)
- `certs/iris.fullchain.pem`
- `certs/iris.key.pem`

Cert is valid for 90 days. Re-run the same script anytime to renew (idempotent — skips if expiry > 30 days).

To schedule daily automatic renewal via Windows Task Scheduler (one-time setup from an **elevated** pwsh session):

```powershell
pwsh -File .\scripts\schedule-cert-renewal.ps1
```

The task runs as the current user (S4U logon, no password stored), once daily at 03:00 local. The install script is idempotent so the daily check is a fast no-op until expiry approaches. Output goes to `logs/cert-renewal.log`. Override time with `-TriggerTime 02:30`, or remove the task with `-Remove`.

---

## 4. M365 E3 trial *(human-painful step)*

⚠️ **Do not use M365 Family / Personal / Business Basic / Business Standard / Business Premium / Office 365 E1.** None of these have working access to Graph Communications calling APIs in 2026. The Microsoft 365 Developer Program is no longer free either (requires VS Pro subscription).

⚠️ **Anti-fraud:** Sign up from a residential / business consumer ISP (not the bot VM). Otherwise you'll see *"We are currently experiencing high demand"* with an `msftncsi.com` reference — this is fraud detection, not real demand.

1. Open <https://go.microsoft.com/fwlink/?linkid=2249967&clcid=0x409&culture=en-us&country=us> from a non-VM browser. This is the official fwlink to the M365 E3 trial. (`compare-all-plans` returns 404 in 2026.)
2. Sign up: 30-day free trial, **credit card required**, auto-converts to **$43.20/mo** month-to-month or **$36/mo** annual.
3. **Set a calendar reminder for ~day 28** to cancel if you don't intend to keep paying.
4. Pick a short `<orgname>.onmicrosoft.com` domain (we used `contoso`). Save the admin email + password.
5. **1 user is enough.** The bot is an *application*, not a user.

After signup, three relevant portals:
- M365 admin: <https://admin.microsoft.com>
- Entra (Azure AD): <https://entra.microsoft.com>
- Azure portal: <https://portal.azure.com>

The rest of the setup happens from the bot host — fraud detection only matters at signup.

---

## 5. Entra ID app registration (~10 min)

**Portal:** <https://entra.microsoft.com>

### 5a. Register the app

1. **Identity → Applications → App registrations → + New registration**
2. **Name:** `teams-avatar-agent`
3. **Account types:** Single tenant
4. **Redirect URI:** leave blank
5. **Register**
6. From the Overview page, copy:
   - **Application (client) ID** → `.env` `APP_CLIENT_ID` and `BOT_APP_ID` (same value)
   - **Directory (tenant) ID** → `.env` `TENANT_ID`

### 5b. Client secret

1. **Certificates & secrets → Client secrets → + New client secret**
2. Description: `teams-avatar-agent v1`. Expires: 90 days.
3. **Copy the *Value* column immediately** (not Secret ID). Hidden after navigation.
4. → `.env` `APP_CLIENT_SECRET`

### 5c. Graph application permissions *(load-bearing — may fail)*

1. **API permissions → + Add a permission → Microsoft Graph → Application permissions** (NOT Delegated)
2. Add **all four**:
   - `Calls.JoinGroupCall.All`
   - `Calls.JoinGroupCallAsGuest.All`
   - `Calls.AccessMedia.All`
   - `OnlineMeetings.Read.All`
3. **Add permissions**
4. **✓ Grant admin consent for [your tenant]**
5. **All 4 rows must turn green.** If `Calls.AccessMedia.All` stays orange, file a Microsoft support ticket requesting **Cloud Communications enablement** for your tenant. Cite that this is for an application-hosted media bot. Some tenants need this; ours (`Contoso Ltd`) didn't. Budget 3–5 days if it does.

---

## 6. Azure Bot resource (~5 min)

**Portal:** <https://portal.azure.com>

The E3 trial typically auto-provisions an "Azure subscription 1" tied to the same admin. If yours doesn't, sign up for the Azure free account (separate flow — search "Start with free" in portal.azure.com).

1. Top search → `Azure Bot` → **+ Create**
2. Form:
   - **Bot handle:** `teams-avatar-agent` (globally unique; suffix `-<orgname>` if taken)
   - **Subscription:** your trial subscription
   - **Resource group:** create `teams-avatar-agent-rg`
   - **Location:** `East US` (or closest to bot host)
   - **Pricing tier:** **F0 (Free)**
   - **Type of App:** Single Tenant
   - **Creation type:** Use existing app registration
   - **App ID:** paste `APP_CLIENT_ID`
   - **App tenant ID:** paste `TENANT_ID`
3. Review + create → Create. Wait ~30 seconds → Go to resource.

### 6a. Messaging endpoint

1. **Settings → Configuration**
2. **Messaging endpoint:** `https://<BOT_PUBLIC_FQDN>/api/calling`  *(e.g. `https://bot.example.com/api/calling`)*
3. **Leave "Enable Streaming Endpoint" off** — Bot Framework v4 feature; we use Graph Communications.
4. **Apply**

### 6b. Teams channel

1. **Channels → Microsoft Teams** tile
2. Accept TOS
3. **Messaging tab:** Microsoft Teams Commercial
4. **Calling tab:** tick **Enable calling**, **Webhook (for calling):** same URL as the messaging endpoint
5. **Apply.** The Teams channel should show **Running** with a green status.

(Direct Line and Web Chat will auto-list themselves; harmless, leave them.)

---

## 7. Third-party vendor accounts

In rough order of urgency:

### 7a. LiveKit Cloud — <https://cloud.livekit.io>

1. Sign up. Project name: `teams-avatar-agent-dev`. Region: closest to bot host.
2. Skip "Agent Builder" prompt (we build in code).
3. Disable "Agent Observability" (we log locally).
4. **Project API keys → Create new key.** Copy:
   - WebSocket URL → `.env` `LIVEKIT_URL`
   - API key → `LIVEKIT_API_KEY`
   - API secret → `LIVEKIT_API_SECRET` (shown once)

### 7b. Anthropic — <https://console.anthropic.com>

1. Sign up. **Plans & Billing → Add credits** ($20 minimum).
2. **API Keys → Create Key.** Default permissions. Copy → `.env` `ANTHROPIC_API_KEY`.

### 7c. Deepgram — <https://console.deepgram.com/signup>

1. Sign up. $200 free credit, no card needed for dev.
2. **API Keys → Create new.** Permissions: **Member**. No expiry. Copy → `.env` `DEEPGRAM_API_KEY`.

### 7d. ElevenLabs — <https://elevenlabs.io>

1. Sign up. If you have Lenny's Product Pass, redeem the **Creator** plan (1 year free, ~$22/mo equivalent).
2. **Voice Library** → pick a voice → copy **Voice ID** → `.env` `ELEVEN_VOICE_ID`. We use `DXFkLCBUTmvXpp2QwZjA` (Eryn).
3. **Profile → API Keys → Create.** Set least-privilege scopes:
   - **Text to Speech:** Access
   - **Models:** Access
   - **Voices:** Read
   - **User:** Read
   - All others: No Access
4. Copy → `.env` `ELEVEN_API_KEY` (note: `ELEVEN_`, not `ELEVENLABS_` — plugin reads this name natively).

### 7e. HeyGen LiveAvatar — <https://liveavatar.com>

⚠️ **Do not buy HeyGen Studio (heygen.com).** That's the async video-generation product and won't work for us. Real-time live avatars are at **liveavatar.com**, separate billing.

1. Sign up at liveavatar.com. **Starter $19/mo, 150 credits.** Set cancel reminder.
2. Pick a stock avatar → copy **Avatar ID** → `.env` `LIVEAVATAR_AVATAR_ID`.
3. **API Keys → Create.** Copy → `.env` `LIVEAVATAR_API_KEY`.

`AVATAR_PROVIDER=liveavatar` is the v1 default (set in `.env`).

### 7f. Beyond Presence (optional fallback) — <https://www.beyondpresence.ai>

Kept wired behind `AVATAR_PROVIDER=bey` so we can re-test without code edits. Skip if you don't need an A/B comparison.

1. Sign up. Marketing site is `.ai`; dashboard is `app.bey.dev`.
2. **Avatars** → pick stock → copy avatar id → `.env` `BEY_AVATAR_ID`.
3. **API Keys → Create.** Copy → `.env` `BEY_API_KEY`. Headers use `x-api-key`.
4. Plan: Starter (€49/mo, 1-month free trial — set cancel reminder). Speech-to-Video API gives 280 min/mo at €0.175/min overage.

---

## 8. Populate .env

Copy template + fill in values from the previous steps:

```powershell
copy .env.example .env
notepad .env   # or your editor of choice
```

Then verify nothing is empty (placeholder values are OK to leave for now if they're labelled in the template):

```powershell
awk -F= '/^[A-Z]/ { printf "%-30s %s\n", $1, (length($2)>0 ? "[set]" : "[EMPTY]") }' .env
```

---

## 9. Smoke-test the API keys

```powershell
pwsh -File .\scripts\smoke-test.ps1
```

Hits Anthropic, Deepgram, ElevenLabs, Beyond Presence (if `BEY_API_KEY` set), and LiveKit. **All five must return 200.** Fix any failures before proceeding.

---

## 10. Phase 1 — run the Python agent against the LiveKit playground

```powershell
cd agent
uv sync --extra dev
uv run pytest -v       # 14/14 should pass
uv run python agent.py download-files   # pulls turn-detector ONNX weights, ~30 sec
uv run python agent.py dev              # registers the worker
```

In another browser (any machine), open <https://agents-playground.livekit.io/>, connect using your `LIVEKIT_URL` + key + secret, allow mic, speak. Iris should respond within ~2 seconds (cold cache; ~1 second cache-warm). Test interruption.

If the agent hits "no warmed process" or fails on first message, check the agent.py logs in the same shell — usually a missing env var or LiveKit URL typo.

---

## 11. Phase 2.2.5 — deploy the bot publicly

```powershell
# Open Windows Firewall (admin / elevated PowerShell)
New-NetFirewallRule -DisplayName 'TeamsAvatarBot HTTPS 443' `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443 -Profile Any

cd ..\bot
dotnet build TeamsAvatarBot.sln    # 0 errors expected
dotnet test TeamsAvatarBot.sln     # 1/1 should pass

# Run the bot (must be elevated to bind 443)
dotnet run --project src\TeamsAvatarBot.Host
```

Wait for the log line `Now listening on: https://0.0.0.0:443`.

From any external machine:
```bash
curl https://iris.<your-domain>/healthz
```

You should get `{"status":"ok",...,"azureConfigured":true,...}`.

If `azureConfigured` is `false`, the .env Azure values aren't loading — go back to step 5/6.

If the curl fails:
- TLS handshake fail → cert isn't valid for the FQDN; re-run step 3.
- Connection refused → Windows Firewall rule missing or bot not running on 443.
- DNS resolution fail → step 2 didn't propagate (try `nslookup`).

---

## 12. Sideload the Teams app *(Phase 2.4)*

Once Phase 2.3 is wired (audio + video bridge), build + sideload the Teams app so users can add Iris to a meeting via the standard **+ Add** flow instead of having to hit `/api/join` from a terminal:

```powershell
uv run teams-app/build.py     # produces teams-app/iris-teams-app.zip
```

Upload to Teams Admin Center (or via the Teams client for per-user). Full runbook + troubleshooting: `teams-app/README.md`.

---

## 13. What's next (Phase 3+)

Hardening:
- ✅ JWT validation on `/api/calling` (commit `0990d83`)
- ✅ Per-turn metrics → `logs/turns/{call_id}.jsonl` (commit `0a10b95`)
- ✅ Cert auto-renewal via Windows Task Scheduler (commit `5ac3e38` + one-time `pwsh -File .\scripts\schedule-cert-renewal.ps1`)
- Pending: idle-behaviour avatar fallback, end-to-end metrics aggregation CLI, observability beyond logs (Prometheus / dashboards).

See:
- `docs/PHASE_2_3_HANDOFF.md` — context bundle.
- `teams-avatar-agent-PRD.md` §5.3–5.4 — original spec + known gotchas.
- `docs/architecture.md` and `docs/dataflow.md` — diagrams + metric schema.
- `docs/decisions.md` — every choice we made and why.

---

## Total time estimate for first-time setup

| Step | Wall-clock | Human-in-the-loop |
|---|---|---|
| 1 — preflight + tooling | ~15 min | minimal (one approval) |
| 2 — Cloudflare DNS + token | ~5 min | yes |
| 3 — TLS cert | ~3 min | no (script) |
| 4 — M365 E3 signup | ~10 min | **yes (and on a non-VM IP)** |
| 5 — Entra app + permissions | ~10 min | yes |
| 6 — Azure Bot + Teams channel | ~10 min | yes |
| 7 — vendor accounts | ~30 min | yes (5 signup flows) |
| 8 — populate .env | ~5 min | yes (paste keys) |
| 9 — smoke test | ~1 min | no |
| 10 — agent live test | ~10 min | yes (talk to Iris) |
| 11 — bot deploy + reachability | ~5 min | yes (admin shell + curl) |
| **Total** | **~1.5 hours** | most of it |

After this, you're at the same checkpoint we hit on 2026-04-27 — ready to start Phase 2.3.
