# Teams Avatar Agent ("Iris")

Real-time AI avatar participant for Microsoft Teams meetings. Iris joins a meeting as her own roster entry, introduces herself per EU AI Act Art. 50, and holds a back-and-forth conversation with the people in the room.

**Stack:** Claude Sonnet 4.5 (LLM) · Deepgram Nova-3 (STT) · ElevenLabs Flash v2.5 (TTS) · HeyGen LiveAvatar (photoreal avatar) · LiveKit (the seam). A Graph Communications media bot in C# / .NET 8 bridges Teams SRTP ↔ LiveKit room; a Python LiveKit Agents app and the avatar publisher both join that LiveKit room. Replace either side without touching the other.

See `teams-avatar-agent-PRD.md` for the full spec, `docs/articles/` for the build journal, and `docs/PHASE_2_4_NOTES.md` for the current handoff state.

> ## ⚠️ Archived research project — do not deploy as-is
>
> This is a **completed research capstone, published for reference. It is not maintained
> and accepts no security patches.** It answered one question — *can a real-time AI avatar
> join a Teams call as a first-class participant using off-the-shelf parts in 2026?* — and
> the answer is yes. The dev environment was decommissioned in June 2026.
>
> **Dependencies are pinned to their mid-2026 versions and are known to be out of date.**
> A vulnerability scan of this repository reports **43 known CVEs across 4 packages**
> (the LiveKit, Microsoft Graph, and Python stacks). They are left as-is on purpose, so the
> code matches the articles that describe it. **If you build on this, upgrade every
> dependency and re-audit before running it anywhere real.**
>
> Also note this bot **joins meetings and processes live audio and video of the people in
> them**. Recording and processing participants carries legal obligations that vary by
> jurisdiction (consent, notice, data retention). Iris announces herself on joining per
> EU AI Act Art. 50 — treat that as a starting point, not compliance advice.
>
> All tenant IDs, hostnames, service accounts, and contact details in this repo are
> **placeholders** (`contoso.onmicrosoft.com`, `bot.example.com`, `1111…` GUIDs).
> Substitute your own — see `SETUP.md` and `.env.example`.

## Status

**Phase 2.3 validated end-to-end as of 2026-05-20.** Iris joins a real Teams meeting via `/api/join`, hears the human, speaks back, and shows up as a video tile. Round-trip lag is ~1-1.5s (normal stack latency; metrics work in Phase 3 will trim it).

| Phase | State |
|---|---|
| 0 — preflight + accounts + scaffolding | ✅ |
| 1 — Python LiveKit Agents app | ✅ |
| 2.1–2.3 — C# bot + LiveKit bridge (a/v bidirectional) | ✅ live-validated |
| 2.4 — Teams app manifest sideload | partial — manifest uploaded; +Add UX blocked on Teams licensing (see article 12) |
| 3 — Hardening (JWT validator endpoint, metrics aggregation, idle behavior) | pending |

## Layout

```
agent/      Python LiveKit Agents app (STT + LLM + TTS + avatar dispatch)
bot/        C# .NET 8 Graph Communications media bot (Windows-only)
teams-app/  Teams app manifest + sideload zip builder
docs/       Runbooks, ADRs, dataflow, the build-journal article series
scripts/    Preflight, cert renewal, smoke tests (PowerShell)
logs/       Per-turn JSONL metrics (logs/turns/{callId}.jsonl)
certs/      TLS cert for the public webhook (Cloudflare DNS-01)
```

## Quick start

Reading order if you're trying to reproduce this on a fresh machine: **`SETUP.md` first**, then the article series in order (`docs/articles/README.md`).

For day-to-day dev once the environment is up:

```powershell
# Terminal 1 — bot (binds 443, requires elevated PowerShell)
cd C:\code\LiveOnTeams
dotnet run --project bot/src/TeamsAvatarBot.Host

# Terminal 2 — agent
cd C:\code\LiveOnTeams\agent
uv run python agent.py dev

# Terminal 3 — dispatch Iris into a Teams meeting
curl.exe -i -H "X-Bot-Secret: $env:BOT_DISPATCH_SECRET" `
  "https://bot.example.com/api/join?meetingUrl=<teams-meeting-url>"
```

Iris is in the meeting in 10-15 seconds. Tail T1 for `bridge started` + audio frames pumping; tail T2 for the agent acknowledging the room dispatch.

## Read the journey

The `docs/articles/` directory is a working journal of building this — one article per meaningful decision or surprise. Highlights:

- **[02 — Vendor stack selection.](docs/articles/02-vendor-stack-selection.md)** Why LiveKit is the seam.
- **[06 — The M365 tenant odyssey.](docs/articles/06-m365-tenant-odyssey.md)** What it actually costs to get a Teams calling bot onto a tenant.
- **[10 — Bridging LiveKit video into Teams.](docs/articles/10-livekit-to-teams-video-bridge.md)** The FFmpeg subprocess + NAL parser that ships video.
- **[11 — Iris finally talks.](docs/articles/11-iris-finally-talks.md)** Five layers of live-test failure peeled in the order Teams revealed them.
- **[12 — You can't add a calling bot to a Teams meeting.](docs/articles/12-teams-add-bot-trap.md)** The Teams +Add UX constraint and the Resource Account workaround.

Full index in `docs/articles/README.md`.

## Tests

```powershell
cd C:\code\LiveOnTeams\agent && uv run pytest -v   # 14/14
cd C:\code\LiveOnTeams\bot   && dotnet test         # 24/24
```
