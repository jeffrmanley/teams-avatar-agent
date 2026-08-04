# It works: what we proved, what it cost, and how to bring it back

*Article 13 of the Iris series — the capstone. Written as the development server is being decommissioned.*

**Date:** 2026-05-22
**Author:** Jeff Manley
**TL;DR:** A Claude-driven AI participant with a real-time photoreal avatar can join a Microsoft Teams meeting, hear you, and talk back, for about **$0.16 per minute** of meeting time, with **~1.0–1.5 s** of conversational round-trip lag. It is buildable in 2026 by one person with off-the-shelf parts and a Windows VM. This article is the retrospective: the final architecture, the numbers, the complete gotcha cheat-sheet distilled from the whole series, the bugs still lurking if you take this to production, and a runbook to redeploy the whole thing onto an Azure VM from a cold start. The dev box is going away; this is how you wake Iris back up.

---

## What we set out to prove

The question at the start was narrow: **is a real-time AI avatar Teams participant actually buildable in 2026 by a small team with public APIs, or does it require Microsoft partnership / unreleased SDKs / a media team?**

Answer: buildable. Here's the evidence.

- Iris joins a real Teams meeting (via programmatic Graph dispatch), shows up as a participant with a photoreal avatar video tile, audibly introduces herself (EU AI Act Art. 50), transcribes the room, and answers when spoken to.
- The whole media path — Teams SRTP ↔ a LiveKit room ↔ a Python agent ↔ an avatar vendor — runs on one Windows VM plus three cloud APIs and one avatar API.
- Either half of the system (the Teams bridge, or the agent+avatar) can be swapped without touching the other, because LiveKit is the seam. We A/B'd two avatar vendors in 30 minutes by changing one environment variable.
- Validated end-to-end on **2026-05-20** against an actual Teams meeting. Subjective experience: "it works, a little bit of lag, but it went well."

## The final architecture, in one paragraph

A C# / .NET 8 Graph Communications media bot (Windows-only, because `Microsoft.Skype.Bots.Media` is) answers a Teams call and gets raw audio + video sockets. It joins a LiveKit Cloud room named `meeting-{callId}` and bridges media both ways: Teams microphone audio → LiveKit (tagged as a `SourceMicrophone` track), and the avatar's audio+video back from LiveKit → Teams. A Python LiveKit Agents app joins the same room and runs the brain: Deepgram Nova-3 for speech-to-text, Claude Sonnet 4.5 for the conversation, ElevenLabs Flash v2.5 for the voice. HeyGen LiveAvatar joins the room as its own publisher, takes the TTS audio, and publishes lip-synced photoreal video. The bot re-encodes that video to H.264 via a per-call FFmpeg subprocess and feeds it to the Teams video socket. The seam everywhere is the LiveKit room.

Full diagram and component table: `docs/architecture.md`. The decision log: `docs/decisions.md`.

## What it costs

Per 30-minute 1-on-1 meeting, grounded in real metrics and current vendor pricing (full model in `docs/vendor-costs.md`):

- **HeyGen LiveAvatar (the photoreal face):** $3.00 — **62%** of the bill
- **VM hosting (amortized):** ~$1.00
- **LiveKit Cloud (3 participants):** ~$0.45
- **Claude Sonnet 4.5 (prompt-cached):** $0.22 — only **5%**
- **Deepgram Nova-3 STT:** $0.13
- **ElevenLabs Flash v2.5 TTS:** $0.00 (within plan; currently free for a year via Lenny's Pass)
- **Total: ~$4.80 / 30-min meeting ≈ $0.16/min**

Two findings that surprised me:

1. **The realistic face is the cost, not the intelligence.** Almost two-thirds of every meeting is avatar rendering. Drop to audio-only and Iris costs ~$1.80/30-min. The brain — the actual Claude reasoning — is the cheapest line item except STT/TTS, thanks to prompt caching. "Make her smarter" is nearly free; "give her a realistic face" is the expensive part.
2. **We came in at ~30–50% of the PRD's $0.35–0.50/min budget.** No heroics required.

## What the lag is

Target budget and the 2026-05-20 observation both land at **~1.0–1.5 s** mouth-closed-to-first-syllable. The stages (steady-state, turn 2+, prompt cache warm):

- End-of-turn detection (LiveKit turn-detector v2 + preemptive generation): 250–400 ms
- STT finalize (parallel with EoT): 50–150 ms
- Claude TTFT, prompt-cached: 400–600 ms
- TTS time-to-first-byte (ElevenLabs Flash): 75–200 ms
- Avatar first frame + Teams egress: 200–450 ms
- Plus Teams' own ingress + egress jitter buffers (~40–60 ms each side)

We never built the per-stage telemetry aggregator (it's the top Phase 3 item — the JSONL is being written, nothing reads it yet), so the breakdown above is the *budget*, not measured ground truth. If you pick this back up, build that reader first; it's the only way to know which stage to attack. My prior is that Claude TTFT and end-of-turn detection are the two biggest levers.

## The gotcha cheat-sheet (every landmine, one place)

Distilled from the whole series. If you rebuild this, these are the things that cost days:

**Platform / Microsoft**

- **You cannot add a calling bot to a meeting from the Teams UI.** No +Add Participants search, no in-meeting Apps button. Microsoft says so in one buried sentence ("the multiparty call isn't enabled yet in the Teams user interface"). The only sanctioned paths are 1:1 chat→call, programmatic Graph invite, or a Resource Account in the Global Address List. (Article 12.)
- **The Resource Account needs a Teams Phone-family license** to land in the GAL, and that "free" license has a paid (or 30-day-trial) prerequisite that base M365 E3 doesn't include. (Article 12.)
- **`CsApplicationAccessPolicy` is a separate gate from Graph API permissions.** Your app can have every Graph permission admin-consented and still get `500 / 1203002` on join until you `New-CsApplicationAccessPolicy` + `Grant-CsApplicationAccessPolicy` for the bot's app id. Allow ~5 min to propagate. (Article 11.)
- **`New-CsOnlineApplicationInstance` + `Sync-CsOnlineApplicationInstance` both need `-ApplicationId`** even though the docs show Sync taking only `-ObjectId`. (Article 12.)
- **M365 E3 is the cheapest authoritative tenant.** Family won't work, the Developer Program isn't free anymore, Business plans hit Error 7504, E1 is unverified. (Article 06.)

**Networking / deployment**

- **Cloudflare proxy must be OFF (gray cloud / DNS-only).** The orange-cloud HTTP proxy terminates TLS itself (breaks the Graph cert chain) and doesn't pass UDP at all (Teams media is SRTP/UDP). (Article 07.)
- **The bot needs a direct public IPv4**, no shared VIP / NAT. Calls are VM-pinned — a call belongs to the exact machine that accepted it; there's no graceful drain, so don't scale in while sessions are live.
- **Bind 443 from elevated PowerShell**, and open the firewall (`New-NetFirewallRule ... TCP 443`).

**Media bridge (the deep ones)**

- **Tag the bot's published audio track `SourceMicrophone`.** LiveKit Agents' RoomIO subscribes only to mic-source tracks by default; an untagged track is silently ignored and the agent's STT never hears the human. Symptom: Iris talks but can't hear you. (Article 11 §Layer 4.)
- **`bridge.StartAsync` must be idempotent.** Graph's `call.OnUpdated` fires on every sub-resource change, not just state transitions. Concurrent `StartAsync` calls race on the LiveKit room field and leave the audio source in `InvalidState` 30 seconds in. Guard with `Interlocked.Exchange` latches. (Article 11 §Layer 6.)
- **Never share a mutable audio buffer across fire-and-forget `CaptureFrameAsync` calls.** The wrapper marshals to native on its own schedule; a reused array gets overwritten by a later frame before the FFI reads it → garbled audio. Give each frame its own array. (Found and fixed during this very retrospective — see "Known issues" below; it had been winning the race by luck.)
- **Pace LiveKit→Teams audio to wall-clock (one 20 ms frame per 20 ms).** LiveKit delivers in bursts when the publisher buffers; unpaced bursts pile into Teams' jitter buffer and you get choppy playback. (Article 11.)
- **The .NET LiveKit wrapper (`Livekit.Rtc.Dotnet` 0.1.2) does not expose encoded video frames** — `RemoteVideoTrack` only yields decoded I420. The clean "passthrough the H.264" plan is dead on arrival; you must re-encode. We spawn one FFmpeg subprocess per call. Upstream won't fix it (closed not-planned). (Article 10, `docs/proposals/upstream-encoded-frame.md`.)
- **Teams needs at least one `H264_320x180_*` format in `SupportedSendVideoFormats`** or it won't negotiate the video socket to `Active`. (Article 10.)
- **Find H.264 access-unit boundaries via AUD NALs (type 9),** and rescan 3 bytes back across chunk boundaries or you'll drop a keyframe every few seconds when a 4-byte start code splits across reads. (Article 10.)

**Agent / LLM**

- **Prompt caching is mandatory** on the Claude system prompt (`caching="ephemeral"`, not `cache_control={...}`). Without it, TTFT roughly doubles on turn 2+. (Article 04.)
- **`RoomOutputOptions(audio_enabled=False)`** — the avatar republishes synced A/V, so if the agent also outputs room audio, participants hear the TTS twice. (Article 11 don't-redo list.)
- **`preemptive_generation=True`** claws back 400–600 ms by letting the LLM start before end-of-utterance is confirmed. (Article 04.)
- **ElevenLabs `eleven_flash_v2_5`, not `eleven_turbo_v2_5`.** Flash is ~75 ms TTFB; Turbo is more natural but 150–300 ms — too slow for real-time turns. (Article 03.)

## Known issues — fix these before you call it production

The dev validation proved the happy path. Two real defects remain, deliberately left documented rather than fixed-blind while we no longer have a live test rig:

1. **Mid-call video resolution renegotiation mislabels frames.** The FFmpeg encoder is lazily built once, for the resolution Teams negotiated on the *first* `Active`. If Teams later upgrades the resolution mid-call (we observed 640×360 → 1280×720 in the 5/20 logs as bandwidth opened up), the `_negotiatedSendFormat` field updates but the encoder does not rebuild — so the bot keeps emitting old-resolution H.264 NALs stamped with the new format. Teams will garble or drop them. The fix is to tear down and respawn the encoder when the negotiated format changes; it needs a live call to validate, which is why it's documented, not shipped. Start here if you redeploy.

2. **Inbound JWT validation endpoint is unconfirmed.** The validator was shipped pointing at the wrong OIDC metadata URL (`api.botframework.com/...`), which 401'd every notification and bricked an entire live test. It's now gated off by default (`BOT_VALIDATE_INBOUND_JWT` unset). The probable-correct endpoint is `https://api.aps.skype.com/v1/.well-known/openidconfiguration`, but it hasn't been verified against a real inbound notification's `Authorization` header. Until it is, the webhook is unauthenticated at the app layer (TLS + the SDK's transport checks are the remaining protection). Confirm the URL from a live header capture, flip the env var on, re-run the e2e. (Articles 08, 11.)

Lower-priority, untested-but-believed-fine: clean teardown after 10 sequential meetings (acceptance criterion, never load-tested); idle avatar behavior; per-user policy granularity.

## Redeploy runbook — cold start onto an Azure VM

The dev box (an Interserver Windows VM) is being decommissioned. The good news: **the entire system is keyed on the domain (`bot.example.com`), not on any IP.** The Azure Bot messaging endpoint, the Entra app registration, the tenant `CsApplicationAccessPolicy`, the M365 tenant, and the Teams app manifest all reference either the domain or the bot's AAD app id — neither of which moves when the host does. So bringing Iris back is mostly: stand up a new public-IPv4 Windows box, restore secrets + cert, and repoint one DNS record.

**What you must preserve before shutting the dev box down** (none of it lives only on the VM, but confirm):

- The repo — already on GitHub (`Contoso/liveonteams`). This is the source of truth.
- `.env` — the 8 vendor key-sets + Azure app credentials. **Not in git.** Copy it somewhere safe (a password manager / Key Vault). Without it you re-collect every API key.
- `certs/` — `iris.pfx` (+ the PEM pair). **Not in git.** Or just regenerate on the new box (step 3 below); it's a 3-minute Posh-ACME run.
- The Entra app id + client secret, the Azure Bot resource, the M365 tenant, the `iris@…onmicrosoft.com` Application Instance — all cloud-side, all survive the VM. Nothing to do.

**On the new Azure VM (Windows Server, with a public IP):**

1. **Provision.** A Windows Server VM with a *static public IPv4* (Azure: a Standard SKU public IP, not Basic/dynamic). Size: 2–4 vCPU is plenty for one concurrent call (FFmpeg at 640×360 is ~15–20% of a core). Open inbound TCP 443 in the **Azure Network Security Group** *and* the Windows firewall — both, or Teams can't reach the webhook.

2. **Clone + preflight.** `git clone` the repo, then `pwsh -File .\scripts\preflight-check.ps1` — it installs/verifies PS7, .NET 8, Python 3.12, uv, Node, Azure CLI, FFmpeg, and the VS Build Tools C++ workload. Idempotent.

3. **Restore secrets + cert.** Drop the saved `.env` at the repo root. For the cert, either copy the saved `certs/` over, or regenerate: set the Cloudflare DNS-edit API token and run the Posh-ACME script (`SETUP.md` §3) — it produces `certs/iris.pfx` fresh via a DNS-01 challenge. The cert is domain-bound, not host-bound, so a regenerated one is identical in function.

4. **Repoint DNS — the one critical switch.** In Cloudflare, edit the `iris` A record to the new Azure public IP. Keep it **gray cloud (DNS-only)** — orange cloud breaks both the Graph TLS chain and Teams' UDP media. Wait for propagation (`nslookup bot.example.com` should return the new IP).

5. **Run.** From elevated PowerShell: `dotnet run --project bot/src/TeamsAvatarBot.Host`. Wait for `Now listening on: https://0.0.0.0:443`. From another machine: `curl https://bot.example.com/healthz` → 200 confirms the public path end-to-end.

6. **Re-confirm the tenant policy.** If the Azure VM is on the same M365 tenant, the existing `CsApplicationAccessPolicy` still authorizes the bot's app id — nothing to do. (It's keyed on the AAD app id, not the host.)

7. **Start the agent + dispatch.** `cd agent && AVATAR_PROVIDER=liveavatar uv run python agent.py dev`, then `curl -H "X-Bot-Secret: <secret>" "https://bot.example.com/api/join?meetingUrl=<url>"`.

That's it. No Azure Bot reconfiguration, no Entra changes, no re-consent. The domain is the abstraction layer, and that's the single design decision that makes this whole thing portable. If anything, the move to Azure is a step *toward* the original PRD assumption — the architecture doc still has the Azure-native notes (ILPIP, etc.) from before we landed on Interserver for dev.

## What I'd do differently

- **Build the metrics aggregator before the first live test, not after.** I tuned latency by feel and vendor docs. I should have had p50/p95 per stage on day one. The JSONL was being written the whole time; I just never read it.
- **Confirm every external URL against a live header before writing 10 unit tests around it.** The JWT validator had a beautiful, fully-tested security contract pointed at the wrong endpoint. The tests passed against a self-signed key and told me nothing about the URL being wrong. A single live capture would have saved a whole test cycle.
- **Don't trust null-forgiving (`!`) on any field a parallel path can touch.** The race that ate Phase 2.3 hid behind a `_room.LocalParticipant!` that read as "I've thought about this." Capture into a local, or null-check honestly.
- **Treat "it worked once" as a hypothesis, not a result.** The shared-audio-buffer race documented above had been silently winning a data race through every successful test. The code worked because the timing was lucky, not because it was correct. The only way I caught it was reading the code adversarially with the explicit question "where could this be wrong even though it works?"

## Where this goes next

The research question is answered: yes, buildable, ~$0.16/min, ~1–1.5 s lag, one person, off-the-shelf parts. The natural next chapter — if there is one — is a production deployment on Azure with the two known issues fixed, the metrics aggregator built, and a real decision about the +Add UX (either the Resource Account license or a small meeting-tab "Add Iris" button that POSTs to `/api/join`).

For now: it works. The dev box did its job. Iris will keep on GitHub, and the runbook above will wake her up on Azure whenever that day comes.

— Jeff Manley, May 2026
