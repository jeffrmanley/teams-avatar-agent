# Picking a real-time AI avatar vendor in one hour

*A field report from building a Microsoft Teams meeting participant.*

**Date:** 2026-04-27
**Author:** Jeff Manley & Claude Opus 4.7
**TL;DR:** We A/B-tested Beyond Presence and HeyGen LiveAvatar in the LiveKit Agents Playground using a 4-question rubric with voice held constant. Total elapsed time: about 60 minutes from "we should test this" to "decision made and committed to git." LiveAvatar won 15-10 across empathy, lip-sync, interruption recovery, and idle naturalness. The methodology is more interesting than the result.

---

## The project

We're building **Iris**, an AI agent that joins Microsoft Teams meetings as a full participant. Photoreal avatar, real-time voice, can hear and respond and be interrupted. Driven by Claude Sonnet 4.5 with Deepgram Nova-3 STT and ElevenLabs Flash v2.5 TTS, all orchestrated through LiveKit Agents.

The architecture is split: a C# Graph Communications bot bridges between Teams and a LiveKit room; the Python agent and the avatar vendor both join that LiveKit room as publishers. **LiveKit is the seam** — change vendors on either side without touching the other.

That seam is the reason this A/B test was even possible.

## What the PRD originally specified

The initial spec called for **Beyond Presence** as the avatar vendor. The reasoning was specific and defensible at the time:

> Beyond Presence is the only avatar vendor whose API accepts a LiveKit room we control. Every other vendor requires bridging between their LiveKit/Daily room and ours (extra hop, extra latency, extra codec pain).

That was true when the PRD was written in early 2026. By the time we got to implementation, it was no longer true — but I wouldn't have known that without doing the research.

## The pivot signal

Mid-build, we'd just finished validating the agent end-to-end against Beyond Presence. The user — who pays for Lenny's Newsletter Pro Pass — mentioned they had a year of free ElevenLabs Creator. We'd specced Cartesia Sonic-3 originally; ElevenLabs Flash v2.5 has comparable latency (~75 ms TTFB) and would save money.

That was a 5-minute swap. One env var rename, one plugin import change, twelve unit tests passed, done.

But the swap raised a question: **what other vendor assumptions in the PRD were now stale?**

The user asked specifically about HeyGen — they'd seen it elsewhere, it looked impressive, and they wanted to know why we weren't using it.

## The product confusion

Here's the first lesson from this exercise: **HeyGen sells two products that look like siblings but do completely different things.**

| Product | What it does | URL | Use case |
|---|---|---|---|
| HeyGen Studio (Free / Creator $29 / Pro) | **Async** video generation: type a script → wait → download an MP4 | heygen.com | Marketing videos, training content |
| HeyGen LiveAvatar (Starter $19 / Essential $100) | **Real-time streaming**: audio in → live video out | liveavatar.com | Live agents (us) |

The HeyGen pricing page that most users land on first is for Studio. The Creator plan ($29/mo) is the most-marketed tier. **None of those plans include real-time avatar minutes.** A user hoping to use HeyGen for a live agent could easily pay $29/mo for the wrong product.

This is the kind of thing a research agent surfaces in two minutes that a human spends an hour figuring out the hard way.

## The architectural unblock

The other thing the research surfaced: HeyGen's LiveAvatar (formerly the Streaming API) **now supports BYO-LiveKit-room.** The older HeyGen forum threads said HeyGen-managed rooms only — that's still true for the legacy v1/v2 Streaming API, but the new LiveAvatar product accepts a `livekit_url + livekit_room + livekit_client_token` triple.

Which means the original PRD's reason for picking Beyond Presence — "the only vendor that lets us own the room" — was now obsolete. There's an official `livekit-plugins-liveavatar` package on PyPI with the same API shape as `livekit-plugins-bey`. Swapping is a one-line plugin import change.

Suddenly we had a second viable vendor and a clean swap path. The decision became: **whose render quality wins on our specific workload?**

## A/B test design

I had three constraints:

1. **Hold voice constant.** Both avatars consume our ElevenLabs Eryn voice in LITE mode. The only variable in the comparison is the avatar render itself, not voice quality.
2. **Score the dimensions that actually matter for our use case.** Iris will be in 30-minute Teams meetings, listening 70% of the time. Idle naturalness matters more than lip-sync precision on a one-shot demo.
3. **Same questions, same order, both rounds.** Eliminate the human-bias of being more articulate the second time.

The questions:

> **Q1 — Empathy / facial expressiveness:**
> *"I'm feeling stressed about a deadline this week. Any advice?"*
> Score on: empathetic facial expression, eye contact warmth, head tilts during listening.

> **Q2 — Sustained delivery + lip-sync stress:**
> *"Tell me a 20-second story about a robot who learned to bake bread."*
> Score on: lip-sync precision on plosives (b/p/m/f), expression variation over a longer turn, gestures that don't loop or freeze.

> **Q3 — Interruption recovery:**
> *"What's your favorite color, and why?"* — interrupt at ~3 seconds with: *"Actually, never mind, what's your favorite season?"*
> Score on: how the avatar handles being cut off — does the mouth/expression let go naturally or freeze in mid-shape?

> **Q4 — Idle / listening test:**
> After Q3 finishes, just stop talking for 30 seconds and watch. Don't ask anything.
> Score on: blink rate, micro-head movements, breathing, eye darting. Real humans blink every 4–6 seconds. A frozen statue is the failure mode.

Q4 is the secret weapon. Most avatar demos show the avatar talking. Real meetings have most participants listening most of the time. **Idle is the dimension nobody markets.**

## The mechanism

The seam is what made this test cheap to run.

```python
if AVATAR_PROVIDER == "bey":
    avatar = bey.AvatarSession(avatar_id=require_env("BEY_AVATAR_ID"))
elif AVATAR_PROVIDER == "liveavatar":
    avatar = liveavatar.AvatarSession(avatar_id=require_env("LIVEAVATAR_AVATAR_ID"))
```

One env variable. To swap vendors mid-test: stop the agent process, edit `.env`, restart. Each round took about 3 minutes including reconnecting to the LiveKit playground and waiting for the avatar to dispatch.

The whole testing protocol fit on a single screen of markdown. Both rounds plus scoring took about 25 minutes of wall-clock time once the second account was set up.

## The results

| Dimension | Beyond Presence | HeyGen LiveAvatar |
|---|---|---|
| Q1 — empathy expression | 2 | **4** |
| Q2 — lip-sync + sustained delivery | 5 | 5 |
| Q3 — interruption smoothness | 2 | **4** |
| Q4 — idle naturalness | 1 | 2 |
| Subjective: which would you actually want as a meeting participant? | | **✓** |

LiveAvatar **15** / Beyond Presence **10**, and on subjective preference too.

The scores told a specific story. Both vendors aced lip-sync (Q2) — that's table-stakes. Where they diverged was the **emotional surface** — empathetic listening expressions, smooth recovery from being interrupted, the things that make an avatar feel like it's *with* you in the conversation rather than *performing* at you.

But both vendors **scored badly on idle (Q4).** That's the most important finding from this whole exercise. For a Teams meeting bot that will spend 70% of every meeting listening, neither vendor is delivering naturalistic idle behavior in 2026. We've added that to our Phase 3 hardening backlog as a v1 risk to evaluate options for.

## Surprise findings the research surfaced

Two things changed our mental model that we wouldn't have found without spending an hour on it:

**1. Hedra Character-3 is dead.** Initial research surfaced Hedra at $0.05/min flat — 6× cheaper than LiveAvatar. They were an official LiveKit launch partner. We almost picked them. Then we checked the plugin docs:

> *"Hedra sunset their Realtime Avatar product on April 15, 2026. This plugin no longer functions."*

Eleven days before our test. A blog from May 2025 would still recommend Hedra confidently. This is why "let me check the docs right now" beats "I remember from a recent post that…"

**2. HeyGen's flagship Avatar IV doesn't carry over to LiveAvatar.** The user wanted to clone themselves with HeyGen because Avatar IV's quality is exceptional. We discovered LiveAvatar uses a *different model* optimized for latency over fidelity (capped at 1080p; Avatar IV does 4K). To use a custom HeyGen avatar in real-time, you have to record a *separate* 2-minute training video on the LiveAvatar platform. The Avatar IV asset doesn't transfer.

That kills the main appeal of "going with HeyGen because you can clone yourself." Beyond Presence requires the same ~2-minute training video. The cloning experience is roughly equivalent.

## Cost model

We landed on roughly **$4.80 per 30-minute meeting** with our final vendor mix. LiveAvatar alone is **62% of that cost**. Claude is 5%, thanks to prompt caching. Per-minute we're at $0.16 — half the original PRD budget of $0.35–$0.50/min.

The model is in `docs/vendor-costs.md`. The headline implication is that **the next 2× cost reduction in this stack will come from the avatar render**, not from the LLM, STT, or TTS layers. That's where to keep watching the market.

## Methodology takeaways

Things that worked:

- **Hold voice constant.** Cleanest A/B variable isolation.
- **Score idle separately.** Vendors don't market this; you have to test for it.
- **Use the seam.** A one-env-var swap means you're not committing to anything. Failure mode is "lose 30 minutes and learn something."
- **Score on a rubric, not gut feel.** It forced us to articulate dimensions and surfaced that idle was bad for both vendors — a finding that doesn't show up in "which feels more natural?"
- **Verify the docs at decision time.** Hedra's sunset was 11 days old. Anything older than a month, recheck.

Things I'd do differently next time:

- **Test custom-avatar quality, not stock.** Stock avatars are tuned for vendor demos. Real-world deployment uses cloned likenesses. The visual quality of those isn't always proportional to stock.
- **Test under network stress.** Both vendors looked fine on a fast residential connection. A Teams meeting from a hotel WiFi might have a different ranking.
- **Test multilingual.** Iris is currently English-only. Vendors vary widely in their support for non-English phonemes; that's its own A/B.

The whole exercise took about an hour, including writing this report. Compared to the ~$3,500 of monthly cost difference between right and wrong on this decision (LiveAvatar Essential vs Beyond Presence Starter at scale), an hour of structured testing was the cheapest hour of the whole project.

---

*Code is at `agent/agent.py`; the AVATAR_PROVIDER env switch is what made this test possible. ADRs in `docs/decisions.md` (ADR-002, ADR-003). Cost model in `docs/vendor-costs.md`. The 4-question rubric is reusable — fork it for your own avatar comparison.*
