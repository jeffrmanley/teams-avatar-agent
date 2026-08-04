# The Microsoft 365 tenant odyssey

*Article 06 of the Iris series.*

**Date:** 2026-04-27
**Author:** Jeff Manley
**TL;DR:** Of the eight things you sign up for to build a Teams meeting bot in 2026, the **Microsoft tenant** is by an order of magnitude the most painful. M365 Family doesn't qualify. The Microsoft 365 Developer Program is no longer free as of 2024. M365 Business plans hit "Error 7504" on the calling APIs. Office 365 E1 is unverified at best. The cheapest authoritatively-supported path for application-hosted media bots is **Microsoft 365 E3**, $43.20/month month-to-month after a 30-day trial. Microsoft also fraud-blocks signups from datacenter IPs, so you have to do the signup itself from a residential ISP and then move back to your VM for everything else. And finally, even on E3 some tenants need a support ticket to enable Cloud Communications calling. Ours got lucky and didn't.

---

## Why this article exists

Every Microsoft 365 doc that mentions the calling APIs assumes you already have a tenant with the right plan. None of them lay out the matrix of "which plans actually work, what they cost, what you'll trip over." This article is that matrix, written from one project's experience in late April 2026.

If you're stumbling into this from a search like "Calls.AccessMedia.All Error 7504" or "M365 Developer Program no longer free 2026" — the answer to the second one is yes, and the answer to the first one is in the matrix below.

## The matrix

| Plan | What happens | Cost | Verdict |
|---|---|---|---|
| **Microsoft 365 Family / Personal** | No tenant, no admin portal, no app registrations possible. Consumer Microsoft Account, not an Entra ID directory. | $7–10/mo | ❌ Doesn't work |
| **Microsoft 365 Business Basic / Standard / Premium** | Tenant exists. Admin portal exists. App registration succeeds, permissions can be granted. Bot fails on first call with **`Error 7504: Insufficient enterprise tenant permissions`**. The Cloud Communications calling APIs are not provisioned for Business plans. | $6–22/mo | ❌ Doesn't work |
| **Office 365 E1** | No authoritative Microsoft confirmation that calling APIs work. Community signals lean negative. Unverified. | $10/mo | ⚠️ Don't bet on it |
| **Microsoft 365 Developer Program** (free E5 sandbox) | Used to be the right answer for years. **Free signup retired in 2024.** Now requires an active Visual Studio Pro/Enterprise subscription (~$1,199/yr) or Microsoft Partner Network membership. | "Free" but gated by VS Pro | ❌ Effectively gone |
| **Microsoft 365 E3** | First plan with authoritative Microsoft confirmation for app-hosted media bots. Trial works. Paid works. | **$36/mo annual or $43.20/mo month-to-month** | ✅ The cheapest known-working path |
| **Microsoft 365 E5** | Same as E3 plus Power BI Pro, Phone System (PSTN), advanced compliance. None needed for this project. | $57/mo annual, ~$68/mo MoM | ✅ Works but overkill |
| **Visual Studio Pro/Enterprise subscription** | Re-enables the free E5 dev sandbox via the Developer Program | $1,199/yr+ | Worth it only if you'd buy the IDE anyway |

We landed on **M365 E3 trial → $43.20/mo month-to-month**, on a dedicated dev tenant `contoso.onmicrosoft.com`, one user license. The bot is an *application*, not a user, so it doesn't consume a license — you only need exactly one human user (you, as both tenant admin and the test "human" who joins meetings to talk to Iris).

## What didn't work, in chronological order

The journey to "E3 trial is the answer" took the better part of a day.

### Attempt 1: M365 Family

I was already paying for M365 Family for personal email. Surely that counts? It does not. M365 Family creates a consumer Microsoft Account, not an Entra ID tenant. There's no admin portal at `admin.microsoft.com`, no app registration surface, no way to consent to Graph application permissions. You don't even get the *opportunity* to fail; the right portals don't exist for Family users.

**Verdict in ~10 minutes:** can't proceed.

### Attempt 2: Microsoft 365 Developer Program

The PRD specced the M365 Developer Program. Until 2024, this was the canonical "free dev sandbox" — an E5-equivalent tenant with 25 fake user licenses, admin rights, and the full enterprise feature set, free for any developer who registered. It had been free for a decade.

In 2024 Microsoft retired the free tier. The signup flow now requires either:

- **An active Visual Studio Pro or Enterprise subscription** ($1,199/year and up). The dev tenant is included as a perk.
- **Microsoft Partner Network membership.** Higher bar, paid, mostly aimed at consulting partners.

If you have neither, the signup page will tell you "your account is not eligible for this program" and offer to help you start a Visual Studio subscription. Some older blog posts still reference the program as free; those are stale.

**Verdict in ~15 minutes:** still gone in 2026; not viable as a free path.

### Attempt 3: M365 Business Basic / Standard / Premium

Business plans ($6/$12/$22 per user per month) seemed like the next-cheapest option. We signed up for a Business Basic trial, created the tenant, registered the app, granted admin consent for `Calls.JoinGroupCall.All`, `Calls.JoinGroupCallAsGuest.All`, `Calls.AccessMedia.All`, and `OnlineMeetings.Read.All`. All four permissions turned green in the Entra portal. Looked good.

Then we tried to programmatically create a call. The bot got a polite-looking error from Graph:

```
{
  "error": {
    "code": "7504",
    "message": "Insufficient enterprise tenant permissions"
  }
}
```

"Insufficient enterprise tenant permissions" doesn't mean "you didn't grant the permissions" — it means "your tenant SKU doesn't include access to this API surface at all." Cloud Communications calling APIs are not provisioned on Business-tier plans. The Entra portal will let you grant the permissions; the runtime call will reject.

This is a documented behavior, but documented in a way that requires you to already know what you're looking for. The Microsoft Graph API permission reference page for `Calls.AccessMedia.All` doesn't say "requires E3 or higher." You find that out from the runtime error code.

**Verdict in ~2 hours, including support-page reading and forum-archaeology:** Business plans are unusable for app-hosted media bots in 2026.

### Attempt 4: Office 365 E1

Office 365 E1 is the cheapest *enterprise* SKU at $10/user/month. Older forum threads (2022–2023) report mixed results — some people say `Calls.AccessMedia.All` works, others say it hits the same 7504 error. There's no authoritative Microsoft documentation either way.

We didn't try it. The signal-to-noise on community reports was bad enough that paying $10/mo to find out experimentally felt worse than paying $43/mo for a path with documented support.

**Verdict: skipped.** If anyone tries this and either confirms or refutes it in 2026, please leave a note somewhere findable.

### Attempt 5: M365 E3 — the answer

E3 is officially supported by Microsoft for application-hosted media bots. The trial is 30 days free, credit card required, auto-converts to the paid tier on day 31 unless you cancel.

Pricing options:

- **$36/user/month** on annual commitment.
- **$43.20/user/month** month-to-month (a 20% premium for the option to cancel any time).

We took the month-to-month for now. At a single license, the marginal monthly cost ($7.20 over the annual) felt worth the optionality. We can collapse to annual once the project's commitment passes the bar.

The signup itself, ignoring fraud detection (next section), is straightforward:

1. From a non-datacenter IP, open the official trial fwlink: `https://go.microsoft.com/fwlink/?linkid=2249967&clcid=0x409&culture=en-us&country=us`
2. Sign up: 30-day free trial, credit card to activate, auto-converts.
3. Pick a short `<orgname>.onmicrosoft.com` domain. Mine: `contoso`.
4. **Calendar reminder for ~day 28** to cancel if you don't intend to keep paying.
5. 1 user is enough.

After signup, three relevant portals:

- **M365 admin:** `admin.microsoft.com` — billing, license assignment, Teams admin (linked from here).
- **Entra (Azure AD):** `entra.microsoft.com` — app registration, permissions, admin consent.
- **Azure portal:** `portal.azure.com` — Azure Bot resource, channels, Teams calling configuration.

You'll use all three across the setup. None of them link to each other in obvious ways.

## The anti-fraud signup workaround

Halfway through trying to sign up for the E3 trial — from the Interserver VM's RDP session, because that's where I was working — I hit:

> *"We are currently experiencing high demand. Please try again later."*

Tried again ten minutes later. Same message. Tried with a different browser. Same. Searched for the error and found a Microsoft Q&A thread with a well-known workaround: **Microsoft fraud-blocks Microsoft 365 trial signups from datacenter and hosting-provider IPs.** The "high demand" message is a polite cover. The actual block has nothing to do with capacity.

The reference clue: when fraud-blocked, the underlying request that fails calls out to `msftncsi.com` (Microsoft Network Connectivity Status Indicator) as part of the IP-reputation check. If you're on a known datacenter range — AWS, Azure, GCP, Interserver, Linode, Hetzner, etc. — the check fails and you're rerouted to the "high demand" page. There's no human-resolvable appeal, no "click here to confirm you're a developer." You just have to come from a non-datacenter IP.

The fix:

1. **Sign up on a personal device, on a residential or business consumer ISP.** I did this on my laptop at home, on Comcast, after work hours.
2. **Once the tenant exists, all subsequent admin work is fine from the VM.** Microsoft only checks IP reputation on the initial signup. Tenant ops, app registration, admin consent, Azure Bot creation — all work from the VM's datacenter IP.

So the workflow ends up being a brief detour through a residential network, with a consumer ISP IP and a personal device, to get the tenant created. After that, everything moves back to the VM.

This is not documented anywhere on Microsoft's official pages. Microsoft Q&A community threads document it as folklore. The first time you hit it, you'll waste an hour assuming you've misconfigured something.

## The Cloud Communications enablement gate

Even after E3 is set up and the four Graph application permissions are granted, you can hit one more gate.

The four permissions are:

- `Calls.JoinGroupCall.All`
- `Calls.JoinGroupCallAsGuest.All`
- `Calls.AccessMedia.All`
- `OnlineMeetings.Read.All`

When you click **"Grant admin consent for [tenant]"** in the Entra portal, you want all four to turn green within a few seconds. **`Calls.AccessMedia.All` is the load-bearing one** — it's the permission that requires Cloud Communications enablement on your specific tenant.

For some tenants, the consent click works immediately and the permission turns green. For others, the consent click appears to succeed in the UI but the permission stays orange or shows "Granted, but with errors." The error message, if you can find it, says something about Cloud Communications not being enabled for the tenant.

The fix is to file a Microsoft support ticket. From the M365 admin center → Help & support → New service request, with these specifics:

- Category: Microsoft Teams
- Subcategory: Bot Framework / Calling
- Request: "Please enable Cloud Communications calling APIs for our tenant. Tenant ID: `<your tenant id>`. We're building an application-hosted media bot using `Microsoft.Graph.Communications.Calls.Media`. The four Graph application permissions have been granted but `Calls.AccessMedia.All` is not currently functional."

Round-trip on this is reportedly 2–5 business days. Some people get it in 24 hours; some get bounced between teams for a week. You can't rush it from outside.

**We got lucky.** Our tenant (`Contoso Ltd`, the legal entity, on the `contoso.onmicrosoft.com` domain) had `Calls.AccessMedia.All` go green on first click. No support ticket needed. We don't know why some tenants need the ticket and others don't; the suspicion is that it's based on tenant age, region, or how the trial signup was flagged in Microsoft's risk model.

If you go through this and your tenant doesn't get lucky, file the ticket the same day and use the wait-time productively on the Cloudflare/DNS/TLS work (article 07) and the C# bot scaffolding (article 08). Those don't depend on the calling APIs.

## What the working setup looks like

After all of the above, the runbook in `docs/azure-setup-runbook.md` produces:

- A dedicated dev tenant: `contoso.onmicrosoft.com` on M365 E3.
- One licensed user: the admin (me).
- An Entra app registration named `teams-avatar-agent`, single tenant.
- A client secret with a 90-day expiry, copied into `.env` as `APP_CLIENT_SECRET`.
- Four Graph application permissions granted and admin-consented (all four green).
- An Azure Bot resource named `teams-avatar-agent`, F0 (Free) pricing tier.
- The Microsoft Teams channel enabled on the bot, with calling enabled and the webhook pointing at `https://bot.example.com/api/calling`.
- The bot resource shows **Running** with green status.

`.env` ends up with these new values:

```
TENANT_ID=<directory-tenant-id>
APP_CLIENT_ID=<application-client-id>
APP_CLIENT_SECRET=<the-secret-value-not-the-id>
BOT_APP_ID=<same-as-APP_CLIENT_ID-for-app-hosted-media>
```

Total Microsoft-side setup time, once you know what to do: about 25 minutes of clicking, plus the up-to-5-day wait for a Cloud Communications support ticket if you need one.

Total Microsoft-side setup time on the first attempt without this article: most of a workday.

## What we deferred

Two things the PRD originally called for in this phase that we explicitly defer:

**Teams app manifest sideload.** The manifest is what lets a Teams user add Iris from the Teams app store / app catalogue. For dev testing, the bot can be programmatically invited to a meeting via `GET /api/join?meetingUrl=...` (Phase 2.3 wires this), which uses the Graph Communications SDK directly and doesn't need a manifest. We'll do the manifest sideload in Phase 2.4 alongside the user-facing UX.

**Per-user calling policy.** The default Global calling policy works for a single test user (the admin). Phase 3 hardening will create a `teams-avatar-agent-callers` policy if/when the user count grows.

## Lessons

1. **M365 Family doesn't have a tenant, period.** If you're paying for it for personal email, that doesn't help you build anything bot-related. You need a separate enterprise SKU.
2. **The Microsoft 365 Developer Program is no longer free** as of 2024. Anyone telling you it's the "free path" for dev work is referencing stale docs. Visual Studio Pro/Enterprise re-enables it; otherwise you're paying for something else.
3. **M365 E3 ($43.20/mo month-to-month) is the cheapest authoritatively-supported path** for application-hosted media bots in 2026. Business plans hit Error 7504 at runtime; E1 is unverified; E3 is the floor.
4. **Sign up for the trial from a residential IP**, not from your bot host. Microsoft fraud-blocks signups from datacenter ranges with a "high demand" cover message. Once the tenant exists, all subsequent work is fine from your VM.
5. **`Calls.AccessMedia.All` admin consent is the gate.** If it goes green on first click, you got lucky. If it doesn't, file a Microsoft support ticket the same day requesting Cloud Communications enablement, and use the wait time on parallel work that doesn't depend on it.

---

*Runbook: [`docs/azure-setup-runbook.md`](../azure-setup-runbook.md). ADRs: ADR-007 (superseded), ADR-007a (M365 E3 trial decision) in [`docs/decisions.md`](../decisions.md). Next: [Cloudflare DNS and Let's Encrypt TLS](07-cloudflare-dns-and-tls.md).*
