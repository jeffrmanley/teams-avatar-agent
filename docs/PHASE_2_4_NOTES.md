# Phase 2.4 + Phase 3 status notes

This is the current-state handoff document for the **Teams Avatar Agent** ("Iris") project. Read it first; it is the cheapest way to load context after the Phase 2.3 closeout.

---

## 🏁 PROJECT CLOSEOUT — 2026-05-22

**The development phase is complete and the dev server (Interserver Windows VM) is being decommissioned.** The research question — is a real-time AI avatar Teams participant buildable in 2026 with off-the-shelf parts — is answered: **yes.** Validated end-to-end on 2026-05-20. Final retrospective, full cost/latency numbers, the consolidated gotcha cheat-sheet, the known-issues list, and the **cold-start Azure redeploy runbook** are in **`docs/articles/13-it-works-retrospective.md` — read that first when picking this back up.**

**To wake Iris back up on a new (Azure) host:** follow the redeploy runbook in article 13 §"Redeploy runbook." Short version: stand up a Windows VM with a static public IPv4, clone the repo, restore `.env` + `certs/` (both gitignored — preserve them off the dev box), open TCP 443 (NSG **and** Windows firewall), and **repoint the Cloudflare `iris` A record to the new IP**. Everything else (Azure Bot endpoint, Entra app, tenant `CsApplicationAccessPolicy`, M365 tenant, the `iris@…onmicrosoft.com` Application Instance) is keyed on the domain or the AAD app id and survives the move unchanged.

**Before shutting down the dev box, preserve these (NOT in git):** `.env` (all vendor + Azure secrets) and `certs/iris.pfx` (or just regenerate the cert in 3 min on the new host). Optionally clean up the cloud-side billing you no longer want running (Azure Bot resource, any LiveKit/HeyGen usage, the M365 E3 trial).

**Code state at closeout:** `main` clean, all pushed. Final bug fix landed — the shared-audio-buffer data race in `OnTeamsAudioReceived` (each frame now gets its own backing array; see article 09 + 13). Tests: **.NET 24/24, Python 20/20.** Two known bugs are documented-not-fixed (deliberately, since there's no live test rig during wind-down): mid-call video resolution renegotiation, and the unconfirmed JWT validator endpoint. Both are in article 13 §"Known issues — fix these before you call it production."

**Validation honesty:** the last *live* end-to-end run was **2026-05-20**, which succeeded but was **before** the audio-race fix (`acd3eb4`). There was no time to re-validate live before decommissioning the box, so that fix ships on green unit tests + strictly-safer-by-construction reasoning (a per-frame array cannot be worse than a shared one), not on a fresh live run. First thing to do on the Azure redeploy: one clean live call confirming `captureFails=0` holds for the whole meeting.

*Everything below is the pre-closeout status, preserved for the redeploy.*

---

**Last updated: 2026-05-20.** Phase 2.3 is validated end-to-end (see `docs/PHASE_2_3_HANDOFF.md` and article 11). Phase 2.4 (Teams app sideload + meeting-add UX) is partially landed — the manifest is uploaded, but the +Add UX requires more work than expected.

---

## Where we are

| Phase | State |
|---|---|
| 0 — preflight + accounts + scaffolding | ✅ |
| 1 — Python LiveKit Agents app | ✅ |
| 2.1 — C# solution scaffold | ✅ |
| 2.2 — Graph SDK + CallHandler skeleton | ✅ |
| 2.2.5 — Public deployment + reachability | ✅ |
| 2.3 — LiveKit bridge (a–e) | ✅ validated e2e 2026-05-20 |
| **2.4 — Teams app manifest sideload** | **Partially done** — see below |
| 3 — Hardening | Pending — three open items, see bottom |

Latest commit on `main` should be `fccf405` or later (the bridge `StartAsync` idempotency fix that closed Phase 2.3). Run `git log --oneline | head -10` to confirm.

---

## Phase 2.4 status

**What landed:**

- `teams-app/manifest.json` — Teams app manifest v1.16 with `supportsCalling: true`, `supportsVideo: true`, scopes `personal/team/groupchat`, bot AAD id `11111111-1111-1111-1111-111111111111`.
- `teams-app/build.py` — PEP-723 script that generates icons + zips the package. Output: `teams-app/iris-teams-app.zip`.
- The zip is uploaded to the M365 dev tenant's Teams Admin Center (Status: Allowed, org-wide). App ID assigned by Microsoft: `33333333-3333-3333-3333-333333333333`.
- A Teams **Application Instance** ("Resource Account") was created on the tenant via PowerShell:
  ```
  ObjectId          : 44444444-4444-4444-4444-444444444444
  ApplicationId     : 11111111-1111-1111-1111-111111111111
  DisplayName       : Iris
  UserPrincipalName : bot@contoso.onmicrosoft.com
  ```
  Created via `New-CsOnlineApplicationInstance` + `Sync-CsOnlineApplicationInstance -ApplicationId <id>` (both `-ApplicationId` is required despite docs).
- The user can chat with Iris and place 1:1 video calls from the chat (Apps → Iris → call icon). This routes through `/api/calling` and works end-to-end exactly like `/api/join`.

**The blocker for the polished "+Add Iris" UX:**

The Resource Account needs a **Microsoft Teams Phone Resource Account** license assigned in `https://admin.microsoft.com → Users → Iris → Licenses and apps`. That license is "free" but requires a prerequisite Teams Phone-family license that this tenant doesn't own. The cheapest unlocks (per the Purchase services screen on 2026-05-20):

- **Microsoft Teams Phone Standard (Free)** — 30-day trial, auto-renews at $8/user/month
- **Office 365 E5 (Free)** — 30-day trial
- **Microsoft 365 E5 (Free)** — 30-day trial
- **Microsoft Teams Shared Space (Free)** — for shared-device scenarios

We chose **not to buy any of these yet**. The Application Instance may show up in the Global Address List for +Add Participants search *without* the license, since the license is primarily for PSTN routing. As of 2026-05-20 the user is going to wait 30-60 min for GAL propagation and test, and revisit licensing only if the test fails.

Full discussion of why this UX is even an issue (Microsoft has no native UI for adding a calling bot to a meeting in progress) is in `docs/articles/12-teams-add-bot-trap.md`. **Read that before suggesting any Teams-side UX changes** — it explains the platform constraint and the three (and only three) supported invocation patterns.

**What's pending in Phase 2.4:**

1. Confirm whether Iris appears in +Add Participants without the Resource Account license. (Wait + test in a meeting.)
2. If not, decide: buy the free-trial unlock (set cancel-by date reminder) OR accept that `/api/join` is the canonical UX for now and ship.
3. (Stretch) Build a small Teams meeting tab extension with an "Add Iris" button that POSTs to `/api/join` — sidesteps both the Resource Account licensing dance and the global address list propagation lag. Probably the right long-term answer if the +Add UX matters.

---

## Phase 3 status — three open items

1. **JWT validator endpoint.** `GraphInboundJwtValidator` was shipped in commit `0990d83` pointing at `https://api.botframework.com/api/.well-known/openidconfiguration`, which 401'd every Microsoft notification and bricked the e2e test. Fixed in `c12d335` by gating the validator behind `BOT_VALIDATE_INBOUND_JWT=true` (default off). The unit tests still pass — they pin the validator against a self-signed RSA key, so they don't catch the wrong upstream URL. **Right OIDC endpoint is likely `https://api.aps.skype.com/v1/.well-known/openidconfiguration`** per recent Microsoft Communications samples, but it has not been confirmed against real production notification headers. When confirmed, set `BOT_VALIDATE_INBOUND_JWT=true` and re-run the live e2e.

2. **Per-turn metrics aggregation CLI.** `TurnMetricsWriter` (`agent/turn_metrics.py`) writes one JSONL line per `metrics_collected` event to `logs/turns/{call_id}.jsonl` keyed on `speech_id`. There is no aggregator yet. Phase 3 acceptance criterion is "p95 EoU→first-bot-audio-frame < 1600 ms" — we can't report that without a reader. Build a small CLI that joins records by `speech_id` and emits p50/p95 per stage (STT endpointing, LLM TTFB, TTS first-chunk, return path). This is what will tell us where the ~1-1.5s e2e lag is actually coming from.

3. **Idle avatar fallback / per-user policy granularity.** Both flagged in PRD §5.4 and §7 but not started. Lower priority than #1 and #2.

---

## What works *right now* (regression baseline)

- Repo at `C:\code\LiveOnTeams`.
- Phase 2.3 e2e tests pass: `cd C:\code\LiveOnTeams\bot && dotnet test` → **24/24** (1 healthz + 5 URL parser + 5 NAL parser + 1 ffmpeg smoke + 10 JWT + 2 other). Python agent tests still **14/14**.
- `dotnet run --project bot/src/TeamsAvatarBot.Host` from elevated PowerShell binds 443.
- `/api/join` programmatic dispatch works end-to-end against a real Teams meeting. Bidirectional audio. Avatar video tile. ~1-1.5s round-trip conversation lag.
- TLS cert auto-renewal via Windows Task Scheduler is wired (commit `3bdb8da`).
- M365 E3 trial tenant `Contoso Ltd` / `contoso.onmicrosoft.com` with the 4 Graph application permissions admin-consented. The `bot@contoso.onmicrosoft.com` Application Instance exists.

If any of these regress, see `SETUP.md` for the rebuild path.

---

## Don't redo

- The double-`StartAsync` race is fixed (commit `fccf405`). The fix is two `Interlocked.Exchange` latches plus a defensive null-check. If anyone "simplifies" by removing the latches, audio capture will start failing 30 seconds into the next live test. See article 11 for the full debugging arc.
- The `SourceMicrophone` tag on the published audio track is load-bearing. Without it, LiveKit Agents' RoomIO filter silently drops Iris's input audio. See `LiveKitBridge.cs` around the `PublishTrackAsync` call and article 11 §Layer 4.
- The Application Instance / Resource Account is the **only** Microsoft-sanctioned way to make a calling bot discoverable from the Teams +Add Participants flow. There is no manifest extension that makes a calling bot appear in the in-meeting Apps panel. See article 12.

---

## How to start the next session

1. Read this file + `docs/decisions.md` + `docs/articles/11-iris-finally-talks.md` + `docs/articles/12-teams-add-bot-trap.md`.
2. Run `git log --oneline | head -10` to confirm baseline.
3. Run the test suite as a regression check (commands above).
4. Pick a Phase 3 item from the list above (probably #2, the metrics CLI, because it unblocks empirical tuning of the e2e lag the user noticed in the 2026-05-20 e2e test).
5. If the user wants Phase 2.4 UX polish instead, the unblocked path is the small meeting-tab "Add Iris" extension — sidesteps Microsoft licensing entirely.

---

## References — read in order

1. **This file.**
2. `docs/PHASE_2_3_HANDOFF.md` (historical record of Phase 2.3, now closed).
3. `docs/articles/11-iris-finally-talks.md` — the live-test debugging arc.
4. `docs/articles/12-teams-add-bot-trap.md` — the Teams +Add UX constraint and why.
5. `docs/articles/README.md` — full article index.
6. `docs/decisions.md` — ADRs.
7. `teams-avatar-agent-PRD.md` — the original v0.1 PRD; treat as historical.
