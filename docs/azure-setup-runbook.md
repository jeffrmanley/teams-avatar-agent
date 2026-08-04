# Azure / Microsoft 365 setup runbook

Everything in this doc is **HUMAN ACTION REQUIRED**. Claude Code cannot do these steps — Entra app consent and Teams channel configuration require tenant-admin interaction in the portal.

This document reflects what we actually did on 2026-04-26 / 2026-04-27, not the original v0.1 PRD plan.

---

## 0. Tenant prerequisites *(read carefully — these are show-stoppers)*

### Plans that DO NOT WORK

| Plan | Why it fails |
|---|---|
| Microsoft 365 Family / Personal | Consumer Microsoft Account — no Azure AD tenant, no admin portal, no app registrations possible |
| Microsoft 365 Business Basic / Standard / Premium | Returns "Error 7504: Insufficient enterprise tenant permissions" when the bot tries to join a meeting; Cloud Communications calling APIs are not provisioned for Business plans |
| Office 365 E1 | No authoritative Microsoft confirmation; community signals lean negative; treat as unverified |
| Microsoft 365 Developer Program (free E5 sandbox) | **Free signup retired in 2024.** Now requires an active Visual Studio Pro/Enterprise subscription (~$1199/yr) or Microsoft Partner Network membership. Not viable as a "free dev tenant" path anymore. |

### Plans that DO work

| Plan | Cost | Notes |
|---|---|---|
| **Microsoft 365 E3** | $36/mo annual, **$43.20/mo month-to-month** | **Cheapest path with authoritative confirmation.** What we used. |
| Microsoft 365 E5 | $57/mo annual, ~$68/mo month-to-month | Adds Power BI Pro, Phone System (PSTN), advanced compliance — none needed for Iris |
| Visual Studio Pro/Enterprise subscription | $1199/yr+ | Re-enables the free E5 dev sandbox; only worth it if you'd buy the IDE anyway |

### Anti-fraud signup workaround

When you sign up for the M365 E3 trial, Microsoft's fraud detection blocks signups coming from datacenter / hosting-provider IPs (AWS, Azure, Interserver, etc.). You'll see *"We are currently experiencing high demand"* with a `msftncsi.com` reference — this is the fraud block, not actual demand.

**Sign up from a residential / business consumer ISP.** For us this meant signing up on a personal laptop instead of from the RDP'd VM. Once the tenant exists, all subsequent admin work (this runbook, etc.) can happen from any IP, including the VM.

### Cloud Communications tenant enablement

Even on E3/E5, individual tenants sometimes need a Microsoft support ticket to enable Cloud Communications calling. You'll find out when you click **"Grant admin consent"** for `Calls.AccessMedia.All` in §1b — if it fails, file a ticket; if it turns green, you got lucky. **We got lucky** — `Contoso Ltd` tenant enabled it on first try. Budget 3–5 days of round-trip if your tenant doesn't.

---

## 1. M365 E3 trial signup *(the human-painful part)*

1. From a non-datacenter IP (your laptop, not the bot VM), open: <https://go.microsoft.com/fwlink/?linkid=2249967&clcid=0x409&culture=en-us&country=us>
   This is Microsoft's official fwlink to the current E3 trial signup. (Don't use `compare-all-plans` — that page returns 404 in 2026.)
2. **30-day free trial.** Credit card required to activate (auto-converts to paid at $36–43.20/user/month on day 31).
3. **Set a calendar reminder for ~day 28** to cancel if you don't plan to keep paying.
4. Pick an `<orgname>.onmicrosoft.com` domain — short and recognizable. Example: `contoso.onmicrosoft.com`.
5. Save the admin email + password somewhere durable.
6. **1 user is enough.** The bot is an *application*, not a user account, so it doesn't consume a license. You need exactly one licensed user: yourself, acting as both tenant admin and the test "human" who joins meetings to talk to Iris.

After signup, the M365 admin center is at <https://admin.microsoft.com>; the Entra (Azure AD) admin center is at <https://entra.microsoft.com>; the Azure portal is at <https://portal.azure.com>. You'll use all three.

---

## 2. Entra ID (Azure AD) app registration

**Portal:** <https://entra.microsoft.com>

1. Left nav: **Identity → Applications → App registrations → + New registration**.
2. **Name:** `teams-avatar-agent`
3. **Supported account types:** **Accounts in this organizational directory only** (Single tenant).
4. **Redirect URI:** leave blank (not used for app-hosted media).
5. **Register.**
6. From the new app's Overview page, copy:
   - **Application (client) ID** → `.env` `APP_CLIENT_ID` and `BOT_APP_ID` (same value)
   - **Directory (tenant) ID** → `.env` `TENANT_ID`

### 2a. Client secret

1. Left nav (within the app): **Certificates & secrets → Client secrets → + New client secret**.
2. Description: `teams-avatar-agent v1`. Expires: 90 days.
3. **Copy the *Value* column immediately** — it's hidden after you navigate away. The "Secret ID" column is *not* what you need.
4. Paste into `.env` `APP_CLIENT_SECRET`.

### 2b. Graph application permissions *(the load-bearing step)*

1. Left nav: **API permissions → + Add a permission**.
2. **Microsoft Graph → Application permissions** (very important — *not* Delegated permissions).
3. Search and check **all four**:
   - `Calls.JoinGroupCall.All`
   - `Calls.JoinGroupCallAsGuest.All`
   - `Calls.AccessMedia.All`
   - `OnlineMeetings.Read.All`
4. **Add permissions.**
5. **✓ Grant admin consent for [your tenant]** (button above the table).
6. **All 4 rows must turn green** with "Granted for [tenant]". If any stays orange or errors:
   - **`Calls.AccessMedia.All` failure** = Cloud Communications enablement gate. File a Microsoft support ticket (Microsoft Q&A or paid support) requesting Cloud Communications enablement for your tenant. Cite that the bot is an application-hosted media bot built with `Microsoft.Graph.Communications.Calls.Media`.
   - Other failures usually mean you didn't have admin consent rights on the tenant.

---

## 3. Azure Bot resource

**Portal:** <https://portal.azure.com>

You'll need an Azure subscription. The E3 trial typically auto-provisions one named "Azure subscription 1" with the same admin credentials. If you don't see a subscription, sign up for the Azure free account (separate flow — search "Start with free" inside portal.azure.com).

1. Top search bar: type `Azure Bot` → click **Azure Bot** under Marketplace.
2. **+ Create.**

| Field | Value |
|---|---|
| **Bot handle** | `teams-avatar-agent` (globally unique; append `-<orgname>` if taken) |
| **Subscription** | Azure subscription 1 (or your trial subscription) |
| **Resource group** | Create new: `teams-avatar-agent-rg` |
| **Location** | `East US` (or whichever's closest to your bot host) |
| **Pricing tier** | **F0 (Free)** |
| **Type of App** | **Single Tenant** |
| **Creation type** | **Use existing app registration** |
| **App ID** | paste `APP_CLIENT_ID` |
| **App tenant ID** | paste `TENANT_ID` |

3. **Review + create → Create.** Provisioning ~30 seconds.
4. **Go to resource.**

### 3a. Messaging endpoint

1. Left nav of the bot resource: **Settings → Configuration**.
2. **Messaging endpoint:** `https://<BOT_PUBLIC_FQDN>/api/calling`  *(e.g. `https://bot.example.com/api/calling`)*
3. **Leave "Enable Streaming Endpoint" off** — it's a Bot Framework v4 feature for Direct Line; we use Graph Communications, not Bot Framework activities.
4. **Apply.**

### 3b. Enable Microsoft Teams channel + calling

1. Left nav: **Channels → Microsoft Teams** tile.
2. Accept the Terms of Service.
3. **Messaging** tab: **Microsoft Teams Commercial (most common)**.
4. **Calling** tab: tick **Enable calling**, set **Webhook (for calling)** to the same URL as the messaging endpoint.
5. **Apply.**
6. Back at **Channels**, the Microsoft Teams row should show **Running** with a green status. Direct Line and Web Chat will also be auto-listed; harmless, leave them.

---

## 4. Teams app manifest *(deferred — Phase 2.4+)*

The PRD originally called for sideloading a Teams app manifest in this phase. We **defer** this:

- For dev test, the bot can be programmatically invited to a meeting via `GET /api/join?meetingUrl=...` (Phase 2.3 wires this) using the Graph Communications SDK. No manifest needed.
- The manifest is required only for the UX where a Teams user adds Iris as a chat/meeting participant via the Teams app catalogue.

When we get there, the manifest template is at `bot/manifest/manifest.json` (to be created in Phase 2.4) and sideload happens in Teams Admin Center → Teams apps → Manage apps → Upload.

---

## 5. Calling policy *(deferred — Phase 3 hardening)*

For dev with a single test user (your admin), the Global calling policy is sufficient. Phase 3 hardening will add a per-user `teams-avatar-agent-callers` policy when the user count grows.

---

## 6. Verification

From the bot host (with `.env` populated):

```powershell
# 1. Smoke-test all 3rd-party APIs
pwsh -File .\scripts\smoke-test.ps1
# All 5 (Anthropic, Deepgram, ElevenLabs, LiveAvatar/BP, LiveKit) must be green.

# 2. Build and run the bot
cd bot
dotnet build TeamsAvatarBot.sln
# Open elevated PowerShell to bind port 443:
dotnet run --project src\TeamsAvatarBot.Host

# 3. From any external machine:
curl https://iris.<your-domain>/healthz
# Must return 200 with azureConfigured=true and your tenant/app IDs in the JSON.
```

When the external curl returns 200, Microsoft Graph can also reach `/api/calling`. That's the network/cert/tenant gate cleared.

---

## Known gotchas

| Gotcha | Detail | Mitigation |
|---|---|---|
| **Anti-fraud on signup** | Datacenter IPs blocked | Sign up from non-VM IP |
| **Cloud Communications gate** | Even E3 tenants sometimes need a support ticket | File ticket if `Calls.AccessMedia.All` admin consent fails |
| **Cloudflare proxy breaks Graph** | TLS termination + no UDP | DNS-only (gray cloud), NOT proxied |
| **VM-pinned calls** | Call belongs to the exact host that accepted | Don't scale in while sessions active |
| **Bot video tile visibility** | Some tenants only render bot video under compliance recording | Triage in Phase 2.3 once we wire IVideoSocket |
| **Consent banner doesn't auto-fire for `Calls.AccessMedia.All`** | Two-party-consent jurisdictions need our own in-app disclosure | EU AI Act Art. 50 disclosure already in `agent/prompts/system.md` |
| **MC1251206 bot-detection** | Targets browser-automation bots | Doesn't affect us; we're app-hosted media bot. Monitor post-GA |

ILPIP (Instance-Level Public IP) is **not** required for non-Azure VM hosts (Interserver, etc.); a directly-addressable public IPv4 is enough. ILPIP is an Azure-VM-specific concept.
