# Picking the vendor stack: the LiveKit seam

*Article 02 of the Iris series.*

**Date:** 2026-04-26
**Author:** Jeff Manley
**TL;DR:** Six vendors do the AI work in this project: Anthropic (LLM), Deepgram (STT), originally Cartesia and now ElevenLabs (TTS), originally Beyond Presence and now HeyGen LiveAvatar (avatar render), and LiveKit (the SFU that ties them together). The interesting decision wasn't any individual pick — it was the architectural commitment to put a LiveKit room at the center of everything. That commitment is what makes the rest of the picks reversible.

---

## Why this article exists

There are a hundred blog posts in 2026 explaining what individual AI vendors do well. There are very few that explain what holds them together architecturally for a real-time agent. This article is about the latter.

The picks I'll go through, with the per-meeting cost we end up paying for each:

| Layer | Pick (v1) | Cost / 30-min meeting |
|---|---|---|
| LLM | Anthropic Claude Sonnet 4.5 | ~$0.22 |
| STT | Deepgram Nova-3 | ~$0.13 |
| TTS | ElevenLabs Flash v2.5 (orig. Cartesia Sonic-3) | ~$0.00 (free under Lenny's Pass for year 1; ~$22/mo otherwise) |
| Avatar render | HeyGen LiveAvatar LITE (orig. Beyond Presence) | ~$3.00 |
| SFU / room | LiveKit Cloud | ~$0.45 |
| **Total** | | **~$3.80** vendor-side per 30-min meeting |

Add the amortized VM cost (~$1) and you're at the **~$4.80 / 30-min meeting** ($0.16/minute) figure that shows up across the rest of the docs.

## The architectural shape, restated

The system has three movable halves connected by a fixed seam:

```mermaid
flowchart LR
  subgraph teams[Microsoft Teams]
    M[Teams meeting<br/>SFU]
  end

  subgraph host["Bot host (Windows Server)"]
    BOT[C# .NET 8 Graph<br/>Communications media bot]
  end

  subgraph livekit["LiveKit Cloud"]
    LK[(Room: meeting-{callId})]
  end

  subgraph agent["Agent host"]
    A[Python LiveKit Agents<br/>STT + LLM + TTS]
  end

  subgraph vendors[Vendor APIs]
    DG[Deepgram Nova-3]
    AN[Anthropic Claude]
    EL[ElevenLabs Flash]
    LA[HeyGen LiveAvatar]
  end

  M <-.SRTP.-> BOT
  BOT <-.WebRTC.-> LK
  LK <-.WebRTC.-> A
  LK <-.BYO-room.-> LA

  A -.audio.-> DG
  A -.text.-> AN
  A -.text.-> EL
  EL -.PCM.-> A
  A -.PCM.-> LA
  LA -.video+audio.-> LK
```

The C# bot bridges Teams ↔ a LiveKit room. The Python agent and the avatar publisher both join that LiveKit room. The vendor APIs (Deepgram, Anthropic, ElevenLabs) are called *from inside* the agent and never see the room.

The fixed seam is the LiveKit room. The movable halves are:

- The **Teams side** (C# + Graph SDK). Replace it with a Zoom adapter, a Google Meet adapter, or a "headless WebRTC peer" later. The agent doesn't change.
- The **agent side** (Python LiveKit Agents). Replace any individual vendor — STT, LLM, TTS — with a one-line plugin import change. We did this with TTS in five minutes (article 03).
- The **avatar side**. Replace Beyond Presence with HeyGen LiveAvatar by changing one env var. We A/B-tested this in 30 minutes (article 05).

Without that shape, every vendor swap would be a multi-day refactor. With that shape, the only architectural commitment is "we're using LiveKit as our SFU." Everything else is reversible.

## Why LiveKit, specifically

LiveKit is a managed WebRTC SFU (Selective Forwarding Unit) with two products: **LiveKit Cloud** (managed) and **LiveKit Server** (self-hosted, MIT-licensed, written in Go). For v1 we use LiveKit Cloud (free tier covers all of dev; ~$0.005 per connection-minute beyond).

The thing that made LiveKit the right pick wasn't the SFU itself — there are several technically capable SFUs (Mediasoup, Janus, Daily, Pion-based homebrews). It was **LiveKit Agents**, the Python framework for building voice agents that join LiveKit rooms.

LiveKit Agents 1.5+ ships with a plugin model that lets you compose a session out of components:

```python
session = AgentSession(
    stt=deepgram.STT(model="nova-3", language="multi", endpointing_ms=25),
    llm=anthropic.LLM(model="claude-sonnet-4-5", caching="ephemeral"),
    tts=elevenlabs.TTS(model="eleven_flash_v2_5", voice_id=voice_id),
    vad=silero.VAD.load(),
    turn_detection=EnglishModel(),
    preemptive_generation=True,
)
```

Each plugin has the same shape: an interface the framework calls (`stream` for STT, `chat` for LLM, `synthesize` for TTS), an env-var convention for credentials, a small set of model-tuning kwargs. Swapping STT vendors is a one-line import change. Same for LLM. Same for TTS. The framework handles the streaming, the cancellation on barge-in, the metrics, and the room I/O.

The same framework supports avatar plugins as a separate concept — `bey.AvatarSession`, `liveavatar.AvatarSession`, etc. — that handle the BYO-LiveKit-room dance with photoreal avatar vendors. The avatar plugin attaches *to* a session, but doesn't compose into it the way STT/LLM/TTS do, because the avatar joins the room as its own participant rather than running inline in the agent process.

What makes the framework stick: **the abstractions are at the right level.** Every plugin is the right amount of vendor-specific (you still get model names and voice IDs in your hands) without reinventing streaming or cancellation per vendor. This is the kind of thing that's easy to under-design (just expose the raw vendor SDK and let the user wire it) or over-design (full abstract interface that flattens vendor differences). LiveKit got it right.

## LLM: why Claude Sonnet 4.5

We didn't agonize over this one. The PRD specced `claude-sonnet-4-5` and we kept it.

The relevant criteria for a real-time meeting agent:

1. **Time to first token (TTFT) under 1 second on a 2k–10k token prompt, with streaming.**
2. **Prompt caching** that meaningfully reduces TTFT on turn 2+. A 30-minute conversation has 50–80 turns; cold-cache TTFT on every turn is unaffordable both in latency and cost.
3. **Reasoning quality on conversational, fast-turnaround tasks.** Iris isn't writing essays; she's deciding whether to acknowledge a question, ask a clarifier, or stay quiet because two humans are still talking to each other.
4. **A LiveKit Agents plugin.**

Claude Sonnet 4.5 hits all four. Cache-warm TTFT measured around 400–600 ms in our testing. The `caching="ephemeral"` plugin option turns on cache_control markers for the system prompt and rolling history, getting us steady-state cache hit rates around 70% by minute 5 of a typical conversation. (We'll cover the cache-threshold gotcha in article 04 — it cost us about an hour of confusion.)

We did **not** turn on extended thinking. It's a great feature for asynchronous, deeply-reasoned tasks. For a real-time agent it kills TTFT — adding 2–10 seconds of reasoning before the first user-visible token is the wrong tradeoff for "what's your favorite color?" The agent.py code has it explicitly off:

```python
llm = anthropic.LLM(
    model="claude-sonnet-4-5",
    temperature=0.6,
    caching="ephemeral",
)
```

(No `thinking={"type": "enabled", ...}` parameter. We pinned a test that asserts extended thinking stays off, because it's the kind of thing that's easy to silently re-enable later.)

The cost math: at ~110k input tokens per 30-min meeting (most of it cached) and ~3.7k output tokens, Claude Sonnet 4.5 costs around **$0.22 per meeting**. That's 5% of the per-meeting total. With prompt caching off it would roughly double, but it would still be a minor line item.

## STT: why Deepgram Nova-3

Deepgram Nova-3 was the obvious pick, but worth saying why explicitly.

- **TTFT to partial under 100 ms** on streaming WebSocket sessions, and final transcripts within 50–150 ms of speech end.
- **`language="multi"` mode** that handles English plus mixed code-switching without us picking a per-language model up front.
- **$0.0043/minute committed pricing** — about $0.13 for a 30-min meeting.
- **A LiveKit Agents plugin** with the right shape.

We use `endpointing_ms=25` (low) because we don't want Deepgram doing turn detection — that's owned by LiveKit's turn-detector v2 plugin upstream of it. Letting Deepgram and the turn detector both endpoint creates ambiguity about which one wins, and the turn-detector v2 model is meaningfully better at detecting end-of-turn semantically (not just on silence).

The signup is the easiest of any vendor: $200 of free credit on signup, no card required for dev, member-level API key. We've burned almost none of the $200 in months of dev work.

## TTS: Cartesia, then ElevenLabs

The PRD specced **Cartesia Sonic-3**: ~75 ms TTFB, lowest-on-market streaming TTS at the time of writing, with a LiveKit Agents plugin.

Mid-build I realised I already had a Lenny's Product Pass with a year of free **ElevenLabs Creator** included. ElevenLabs **Flash v2.5** has comparable TTFB (~75 ms by their published benchmarks, ~80–110 ms in our measurements). Switching saved $22/month and was a five-minute change. Article 03 covers that in detail.

The thing worth pointing out here: this is the third place where the seam architecture earned its keep in the first month of the project. We didn't have to commit to Cartesia at design time and then live with the choice. We swapped to ElevenLabs *after* validating the rest of the stack against Cartesia. The cost of being wrong about TTS at design time was effectively zero.

## Avatar: Beyond Presence, then HeyGen LiveAvatar

The PRD specced **Beyond Presence**, on the basis that it was — at the time of writing — the only avatar vendor with a BYO-LiveKit-room API. Every other avatar vendor (HeyGen v1/v2 Streaming API, Tavus, Simli) wanted to manage the room themselves and required a bridging layer between their room and ours. Bridging is a hop, a transcode, and a codec negotiation we didn't need.

By the time we got to implementation, that constraint had moved. **HeyGen launched LiveAvatar** (formerly the Streaming API, rebranded and re-architected) which now also accepts a `livekit_url + room + client_token` triple. Two BYO-room vendors.

We A/B-tested them in the LiveKit Agents Playground using a 4-question rubric (empathy, sustained delivery / lip-sync, interruption recovery, idle naturalness). LiveAvatar won 15-10. That's a 30-minute experiment worth its own article — it's [article 05](avatar-vendor-ab-test.md).

The cost of avatar render dominates the per-meeting bill: **$3.00 of the $4.80 total** is LiveAvatar in LITE mode (1 credit/min, BYO TTS — we feed our ElevenLabs audio in and LiveAvatar just renders lip-synced video). If we ever drop the avatar — or if a cheaper photoreal vendor emerges — the per-meeting cost falls below $2.

We had to delete one early candidate from consideration: **Hedra Character-3**. Initial research surfaced Hedra at $0.05/min flat — about 6× cheaper than LiveAvatar. There was an official `livekit-plugins-hedra` package. We almost picked them on cost alone. Then we checked the plugin docs and found:

> *"Hedra sunset their Realtime Avatar product on April 15, 2026. This plugin no longer functions."*

Eleven days before our research session. A blog post from May 2025 would still recommend Hedra confidently. The lesson: **verify vendor docs at decision time**, not from memory of a recent article.

## SFU: LiveKit Cloud (free tier)

The free tier covers everything we need for dev: 5 GB of bandwidth/month, unlimited rooms, 50 concurrent participants. A 30-minute meeting with three publishers (bot, agent, avatar) uses about 90 connection-minutes; at the paid rate of ~$0.005/connection-minute that's $0.45/meeting. We're on the free tier, so today it's $0/meeting.

A few setup notes that aren't obvious:

- **Disable "Agent Observability" during onboarding.** It's a paid feature, and our per-turn metrics already get logged locally to `./logs/turns/{callId}.jsonl`. Don't pay for the same data twice.
- **Skip "Agent Builder."** That's LiveKit's no-code agent UI. We're building in code.
- **The API secret is shown once.** If you lose it, generate a new pair.
- **Pick a region close to your bot host.** Latency from bot to LiveKit room is one of the budget items in our turn loop. We're in `us-central` because Interserver's Chicago datacenter is nearby.

## The latency budget, by stage

The end-to-end "mouth-closed → first syllable of response" budget breaks down as follows:

| Stage | Budget | Owner |
|---|---|---|
| End-of-turn detection (LiveKit turn-detector v2 + preemptive generation) | 250–400 ms | LiveKit Agents |
| STT finalize (parallel with EoT) | 50–150 ms | Deepgram Nova-3 |
| Claude Sonnet 4.5 TTFT (cache warm) | 400–600 ms | Anthropic |
| TTS TTFB | 75–200 ms | ElevenLabs Flash v2.5 |
| Avatar first-frame + Teams egress | 200–450 ms | LiveAvatar + bot |
| **Total** | **1.0–1.4 s** | — |

The numbers add to more than 1.4s if you sum them naively. They overlap:

- STT finalization runs in parallel with end-of-turn detection.
- LLM generation runs in parallel with TTS startup once the first tokens stream.
- TTS audio starts being rendered by the avatar before TTS itself is finished.

Our first cache-warm measurement, **before** turning on `preemptive_generation=True`, was around 2.0 seconds. Enabling preemptive generation (the LLM call starts before the turn detector confirms end-of-utterance; if the user resumes speaking, the in-flight call gets cancelled cheaply) clawed back 400–600 ms. That's documented in ADR-004.

The honest assessment is that we land in the 1.4s ballpark, not 1.0s, in steady state. Humans tolerate this — it's about what a sluggish video call feels like — but it's slower than human-to-human turn-taking, which is in the 200ms range.

## Where the next 2× cost reduction will come from

The pie chart from `docs/vendor-costs.md`:

```
LiveAvatar render     $3.00   62%
VM hosting (amort.)   $1.00   21%
LiveKit room          $0.45    9%
Claude               $0.22    5%
Deepgram             $0.13    3%
ElevenLabs            $0.00    0% (Lenny's Pass year 1)
```

LiveAvatar is the dominant cost. The next big efficiency win in this stack will come from the avatar layer, not the LLM, STT, or TTS layer. We're watching for:

- **A Hedra-replacement** — a sub-$0.10/minute real-time photoreal avatar that doesn't sunset.
- **An open-source local renderer** that runs on a GPU we already have.
- **Off-peak / batched modes** for the avatar, if the vendor offers them.

If/when one of these lands, the architecture supports the swap. That's the whole point of the seam.

## Lessons

1. **Pick the seam, not the vendors.** A LiveKit room as the integration point lets you A/B and swap individual vendors with no architectural change. That property is worth more than any specific vendor pick.
2. **Verify vendor docs at decision time.** Hedra had been sunset 11 days before we considered them. Anything you remember from a "recent post" is older than you think.
3. **Disable extended thinking on the LLM for real-time use.** It's a great feature for asynchronous tasks. For a real-time agent it kills TTFT.
4. **Let one component own end-of-turn detection.** Don't have STT and a turn-detector both endpointing. Pick one (we picked LiveKit turn-detector v2 because it's semantically aware) and turn the other one's threshold way down.
5. **The avatar render is the cost dominator.** If you're costing this stack, ~60% of the per-meeting bill is the avatar. Pick that vendor with full understanding of pricing, not as an afterthought after the LLM and STT.

---

*ADRs referenced: ADR-001 (Cartesia → ElevenLabs), ADR-002 (Beyond Presence → HeyGen LiveAvatar), ADR-003 (Hedra eliminated), ADR-004 (preemptive generation). Cost model: [`docs/vendor-costs.md`](../vendor-costs.md). Architecture diagrams: [`docs/architecture.md`](../architecture.md), [`docs/dataflow.md`](../dataflow.md). Next: [the TTS swap from Cartesia to ElevenLabs](03-tts-swap-cartesia-to-elevenlabs.md).*
