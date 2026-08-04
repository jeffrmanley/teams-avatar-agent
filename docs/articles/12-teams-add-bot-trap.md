# You can't add a calling bot to a Teams meeting (so how do you?)

*Article 12 of the Iris series. Covers Phase 2.4 — the Teams app sideload, and the surprise constraint that the sideload alone doesn't actually let users add a calling bot to a meeting via the Teams UI.*

**Date:** 2026-05-20
**Author:** Jeff Manley
**TL;DR:** The default mental model for "make my Teams bot available to meeting participants" is wrong. Microsoft does not implement a user-facing flow for adding a calling bot to a meeting that's already in progress — not in the +Add Participants search, not in the in-meeting Apps + button, not anywhere. They say so explicitly in their docs ("the multiparty call isn't enabled yet in the Teams user interface") but it's buried four clicks into a conceptual overview page and most developers find it the long way. The supported workaround is to create a Teams **Application Instance** (a "resource account") backed by your bot's AAD app id, which makes the bot appear as a callable contact in the Global Address List. That works — but it's gated behind a free Microsoft license that's itself gated behind a prerequisite paid (or free trial) license. Worth knowing before you spend an evening assuming the standard sideload covers this.

---

## What I expected vs what actually happens

I shipped the Teams app sideload package (manifest v1.16, `supportsCalling: true`, `supportsVideo: true`, scopes including `groupchat`) into the Microsoft 365 admin center as an org-wide app, status "Allowed." Then I joined a Teams meeting on the same tenant and tried two ways to add Iris:

1. **People → +Add participants → type "Iris"** — nothing. The search returned no results.
2. **+ Apps button at the top of the meeting → search "Iris"** — nothing. Apps panel was empty for her.

The personal install worked fine — Iris showed up in the Teams sidebar under Apps, opened a chat, and offered me a video-call button. Clicking it actually placed a 1:1 call to my bot's `/api/calling` webhook. The bridge spun up, the avatar joined, and we held a perfectly fine 1:1 conversation. So the bot is fully functional. It's just isolated.

That's the part that confused me for an hour. The bot is reachable. It answers calls. It joins LiveKit. It speaks. But there is no Teams UI to summon it into a meeting that already exists.

## The line in the docs that explains it

[Microsoft Learn: Bots for Teams Calls and Online Meeting](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/calls-meetings-bots-overview), in the Call Types section, paragraph 1, sentence 2:

> A user can initiate a peer-to-peer call with your bot or invite your bot into an existing multiparty call. **The multiparty call isn't enabled yet in the Teams user interface.**

I had read this overview page twice and missed that sentence both times. It's one line, in the middle of a table-heavy concepts overview, four clicks into the docs. But it's load-bearing: Microsoft is telling you, with their own words, that the UX flow you'd expect does not exist. There are only three sanctioned ways for a calling bot to end up in a multi-party call:

1. **1:1 chat with the bot → upgrade to call.** Works. Limited to 1:1.
2. **Programmatic invite via Graph API** — `POST /communications/calls/{id}/participants/invite` — which is what `/api/join` already does in this project. Works for scripts and headless dispatch, no UI involved.
3. **Resource Account** — register the bot's AAD app id as a Teams "Application Instance," assign it a phone-system license, and the bot appears in the Global Address List as a callable contact. Now it shows up in +Add People search.

There is no fourth option. There's no manifest extension that makes a calling bot pop into the in-meeting Apps panel. The Apps + button is for **tab and extension** apps — apps that render a UI inside the meeting — not for calling participants. A `supportsCalling: true` bot will not surface there no matter what scopes you declare.

## How the Resource Account workaround actually works

The Application Instance is the same plumbing that backs Auto Attendants and Call Queues in Teams Phone. From Teams' perspective it's a "user" entity bound to your bot's AAD app id; from Graph's perspective it's a routing target. When someone adds it to a meeting, Teams sends a calling notification to your bot's webhook — the same `/api/calling` endpoint you already have — with the meeting context attached. Your bot answers exactly like it answers anything else. Zero bot-side code changes.

Creating one is two PowerShell commands:

```powershell
Connect-MicrosoftTeams -UseDeviceAuthentication

$instance = New-CsOnlineApplicationInstance `
    -UserPrincipalName iris@<your-tenant>.onmicrosoft.com `
    -ApplicationId <your-bot-app-id> `
    -DisplayName "Iris"

Sync-CsOnlineApplicationInstance -ObjectId $instance.ObjectId -ApplicationId <your-bot-app-id>
```

Two notes from running this on a real tenant:

- **Use the `.onmicrosoft.com` domain**, not your vanity domain. The Application Instance has to live in Microsoft's directory namespace because it's a synthetic user.
- **`-ApplicationId` is required on both cmdlets**, despite the official docs only showing `-ObjectId` for `Sync-CsOnlineApplicationInstance`. The cmdlet errors with "At least one of ApplicationId or AcsResourceId must be provided" if you omit it. The docs lag the SDK.

After those run, the Application Instance is created as an AAD object — `Get-CsOnlineApplicationInstance` may take 5-10 minutes to see it due to Teams Config API replication, but the underlying entity exists immediately. Confirm by checking `https://admin.microsoft.com → Users → Active users` with the filter set to "All users" (resource accounts are unlicensed and hidden by default).

## The licensing trap

The free **Microsoft Teams Phone Resource Account** license — the one you need to assign to your shiny new Iris user — has a prerequisite. From the Purchase services panel:

> **You must own one of the prerequisite products below to purchase the selected plan.**
> • Teams Phone with Calling Plan
> • Office 365 E5 (Free)
> • Microsoft 365 E5 (Free)
> • Microsoft Teams Phone Standard (Free)
> • Microsoft Teams Shared Space (Free)
> • Teams Phone with pay-as-you-go calling
> • Teams Essentials and Teams Phone with PAYG (zone 2)
> • Teams Essentials and Teams Phone with domestic calling
> • Teams Phone with domestic and international calling
> • Teams Essentials and Teams Phone with domestic and international

If your tenant is on Microsoft 365 E3 (which is the cheapest sustainable plan that meets the *bot's* requirements — see article 06), none of those prerequisites are included. You'd think the "Free" entries on the list would save you, but several of them are "Free trial — auto-renews at full price after 30 days" entries. Microsoft Teams Phone Standard's "Free" SKU, for instance, is a 30-day trial that converts to $8/user/month if you don't cancel.

For this project I'm going to skip the license entirely and test whether the Application Instance shows up in the Global Address List without it. My read of the docs is that the license is required for PSTN routing (phone numbers, dial-out, IVR), not for AAD discoverability. Worst case I waste an hour of wall-clock time waiting for GAL propagation and confirming it doesn't work, then come back and grab the free trial.

A future-Jeff note here: if you're reading this with deep pockets and a real product, just buy the cheapest free-trial unlock and set a calendar reminder for day 28 to cancel.

## The pragmatic alternative: skip the Resource Account entirely

For everything except polished demos, `/api/join` does what the Resource Account UX does, with no Microsoft licensing involved:

```bash
curl -i \
  -H "X-Bot-Secret: <secret>" \
  "https://bot.example.com/api/join?meetingUrl=<teams-meeting-url>"
```

That returns `202 Accepted` with the call id, the bot dials in via Graph, and Iris is in the meeting in 10-15 seconds. There's no propagation delay, no licensing prerequisite, no PowerShell session to keep alive. The downside is that "adding Iris to your meeting" requires running a script, which is fine for dev but obviously won't scale to "any user in the org can do it."

The cleanest long-term path, if `/api/join`-from-a-button matters, is a tiny **Teams meeting extension** — a custom side-panel app that renders a single "Add Iris" button inside the meeting and POSTs the meeting's join URL to `/api/join` on click. That route involves building a small SPA and hosting it, but it sidesteps both the Resource Account license and the global address list propagation lag, and gives a much better UX than +Add People for the actual user workflow (a button literally inside the meeting, not a search box).

I'm tabling that until Phase 3 metrics work surfaces whether the lag is even an acceptable user experience.

## What I'd skip if I did it again

- **Don't sideload the Teams app manifest assuming it covers the meeting-add UX.** It doesn't. Sideloading makes the bot installable from the Apps catalog and gives you the 1:1 chat surface. It does *nothing* for the +Add Participants flow or the in-meeting Apps button. Two separate problems, two separate mechanisms. I spent an hour wondering why my correctly-uploaded "Allowed" app wasn't surfacing in the participants search; the answer was that the participants search has never searched the apps catalog and never will.

- **Read Microsoft's "Bots for Teams Calls" overview page front to back before building anything.** The page that explains the limitation is not where you'd look for it — it's a conceptual overview, not a manifest reference. The relevant sentence is buried mid-page. Search the page for the word *user interface* before assuming a UX flow exists. If the docs don't show you the click path, the click path doesn't exist.

- **For dev testing, prefer `/api/join` over sideload-and-search.** Every minute spent debugging GAL propagation or app-catalog cache is a minute not spent improving the bot. The programmatic dispatch path was working two weeks before the manifest was uploaded; that's the path that should be your daily driver until you have a stakeholder who specifically needs the UX polish.

— Jeff Manley, May 2026
