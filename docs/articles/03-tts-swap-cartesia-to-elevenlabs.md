# The Lenny's Pass detour: swapping Cartesia for ElevenLabs

*Article 03 of the Iris series.*

**Date:** 2026-04-26
**Author:** Jeff Manley
**TL;DR:** Mid-build, we discovered we already had a year of free ElevenLabs Creator via Lenny's Product Pass. We swapped from the originally-specced Cartesia Sonic-3 to ElevenLabs Flash v2.5 in about five minutes. The actual code change was three lines. The interesting parts are why **Flash v2.5** beats **Turbo v2.5** for this use case (despite Turbo being the plugin default), and why **`ELEVEN_API_KEY`** is the env var name (despite "ElevenLabs" suggesting `ELEVENLABS_API_KEY`).

---

## The setup

The PRD originally specced **Cartesia Sonic-3** for streaming TTS. Reasonable pick: ~75 ms TTFB on streaming WebSocket, $0.000065 per character at the lowest tier, official LiveKit Agents plugin (`livekit-plugins-cartesia`), well-tuned for English conversational prosody.

We'd validated the rest of the agent against Cartesia. STT working, LLM streaming with prompt caching, turn detection firing, avatar lip-syncing. The whole loop was end-to-end on the playground.

At which point a casual conversation surfaced: I was paying for Lenny's Product Pass (a curated set of free-tier upgrades on dev tools, distributed by Lenny Rachitsky's newsletter). Among the ~30 vendors in the pass, one of them was **ElevenLabs Creator — free for one year**. ElevenLabs Creator is normally $22/month.

The question was whether ElevenLabs Flash v2.5 was good enough to replace Cartesia. The answer is yes, with a caveat about the model selection.

## Why Flash v2.5 specifically

ElevenLabs has a confusing model naming scheme. The relevant ones for streaming TTS in 2026:

| Model | Use case | TTFB | Notes |
|---|---|---|---|
| `eleven_turbo_v2_5` | General real-time | 150–300 ms | The plugin's default. More natural prosody. Too slow for our use case. |
| `eleven_flash_v2_5` | Latency-critical real-time | ~75 ms | What we use. Slightly less natural, but inside the budget. |
| `eleven_multilingual_v2` | Highest quality, non-real-time | 600+ ms | Asynchronous content; not relevant here. |
| `eleven_v3` | Newest flagship | varies | Not yet stable in the streaming plugin. |

The catch: the LiveKit `livekit-plugins-elevenlabs` plugin's default is `eleven_turbo_v2_5`. If you write `tts = elevenlabs.TTS(voice_id=...)` and don't specify a model, you get Turbo, not Flash. And Turbo at 150–300 ms TTFB is the difference between an agent that feels responsive and one that feels laggy.

Our turn-budget breakdown allocates **75–200 ms** to TTS TTFB. Turbo's 150–300 ms range eats most of that and sometimes overruns it. Flash's ~75 ms median fits comfortably.

The actual code:

```python
# eleven_flash_v2_5 is the lowest-TTFB ElevenLabs model (~75 ms),
# comparable to Cartesia Sonic-3. eleven_turbo_v2_5 (the plugin default)
# is more natural but ~150-300 ms TTFB - too slow for real-time turns.
tts = elevenlabs.TTS(
    model="eleven_flash_v2_5",
    voice_id=eleven_voice,
)
```

This is the kind of comment you write so that an AI editing the file three weeks later doesn't "fix" it back to the default in the name of "matching the plugin's recommended config." It's also pinned by a test in `agent/tests/test_session_config.py`: if anyone ever silently switches the model, the test fails and we notice.

The naturalness gap between Flash and Turbo is real but small for short conversational turns. For 1–3 sentence responses (the typical Iris turn) the difference is barely perceptible. For longer storytelling or sustained delivery, you can hear Turbo do slightly better with intonation. We accepted that tradeoff.

## The plugin contract gotcha: env var names

LiveKit Agents plugins read credentials from environment variables. Each plugin has a convention. The conventions are obvious for some vendors and less obvious for others.

For ElevenLabs the env var is **`ELEVEN_API_KEY`**, not `ELEVENLABS_API_KEY` and not `ELEVEN_LABS_API_KEY`. The plugin reads exactly that name from the environment.

Why does it matter? Because if you're naming your env vars after the company name (which is the natural instinct: "the company is ElevenLabs, so the var is `ELEVENLABS_API_KEY`"), you'll get a runtime error from the plugin, *not* a clear "missing env var" error from your own code. The plugin will reach for `os.environ["ELEVEN_API_KEY"]`, get a `KeyError`, and surface a stack trace that's a layer down from where you'd expect.

We caught this within about a minute, because our `.env.example` already had `ELEVEN_API_KEY` written down. (Whoever drafted the file got it right by reading the LiveKit plugin source.) But in a project where the env vars are getting added incrementally, this would burn 15–30 minutes of debugging.

The other env var is **`ELEVEN_VOICE_ID`** — that one's *our* convention, not the plugin's. The plugin doesn't pre-define the voice; we pass it in explicitly:

```python
eleven_voice = require_env("ELEVEN_VOICE_ID")
tts = elevenlabs.TTS(model="eleven_flash_v2_5", voice_id=eleven_voice)
```

Note `voice_id`, not `voice`. The plugin is consistent with the underlying API: voices are referenced by ID, not by friendly name. We use `DXFkLCBUTmvXpp2QwZjA` (Eryn) as the v1 voice; the choice was driven by the avatar A/B test (article 05) where we wanted to hold voice constant across both avatar vendors.

## The actual swap

The full diff was three lines, plus a config update.

In `agent/agent.py`:

```diff
-from livekit.plugins import anthropic, bey, cartesia, deepgram, silero
+from livekit.plugins import anthropic, bey, deepgram, elevenlabs, silero
@@
-    tts = cartesia.TTS(model="sonic-3", voice=cartesia_voice)
+    tts = elevenlabs.TTS(model="eleven_flash_v2_5", voice_id=eleven_voice)
```

In `agent/pyproject.toml`:

```diff
- "livekit-agents[deepgram,cartesia,anthropic,silero,turn-detector,bey]>=1.5.0,<2.0.0",
+ "livekit-agents[deepgram,elevenlabs,anthropic,silero,turn-detector,bey]>=1.5.0,<2.0.0",
```

In `.env`, the rename: `CARTESIA_API_KEY=...` → `ELEVEN_API_KEY=...`, plus `ELEVEN_VOICE_ID=...`.

That's it. Run `uv sync` to refresh the venv, run the existing tests (12 passed, no regressions), and run the agent against the playground. First playback was natural, on-budget for TTFB, and noticeably warmer than Cartesia on the same content.

## Why this was easy: the seam, again

This is the third time the LiveKit-as-seam architecture has earned its keep, and we're only ~3 days into the project.

What it would have looked like if the agent code talked to TTS vendors directly: a `CartesiaClient` class, a `WebSocketStreamingTTS` interface, an `audio_buffer.feed(chunk)` lifecycle owned by us, and a "TTS abstraction" file that we'd have to either extend (add ElevenLabs as a second concrete implementation) or rewrite (factor out the interface with Cartesia and ElevenLabs as adapters). Realistically half a day of work, not five minutes.

Because the LiveKit Agents framework owns the streaming lifecycle and gives us a plugin per vendor with the same shape, the swap is just changing which plugin we instantiate. The cost of being wrong about TTS at the start of the project was effectively zero.

## What we did **not** do

A few things you might expect from a vendor swap that we deliberately skipped:

- **No A/B test.** Once the new TTS hit the playground and sounded fine, we committed. We didn't need a side-by-side comparison because the comparison criteria were "TTFB under 200 ms" and "voice doesn't sound robotic." Both pass for both vendors. (The avatar A/B test in article 05 had a higher bar because the cost difference was meaningful and the quality difference was not predictable.)
- **No extensive re-tuning.** Cartesia and ElevenLabs both default to reasonable streaming chunk sizes and prosody settings for English conversational use. We didn't tweak any of those at swap time. If we wanted to optimize for, say, a non-English language, we'd revisit.
- **No fallback wiring.** The agent doesn't try-Cartesia-then-ElevenLabs; it just uses ElevenLabs. The PRD §6.3 specs a fallback to LiveKit's silero TTS plugin (lower quality but local, no API call required) on hard ElevenLabs failure. That's Phase 3 work; Phase 1 just trusts the vendor. If ElevenLabs had a 5-minute outage during dev, we'd notice in real time and fix forward.

## What if Lenny's Pass expires

The Creator plan is free for one year through Lenny's. After that it's $22/month. ElevenLabs Flash v2.5's per-character pricing is roughly competitive with Cartesia at our usage levels (~7,800 characters per 30-min meeting), so the absolute monthly cost stays comparable.

If the costs ever diverge meaningfully — Cartesia drops their price, or ElevenLabs raises theirs — the swap back is symmetric: three-line diff in `agent.py`, one extra in `pyproject.toml`, env var rename. We've done the round-trip in our heads enough times that it doesn't feel like a lock-in.

## What this taught us about Lenny's-pass-style discounts

There's a meta-lesson here that's not specific to TTS.

When you're scoping a project with a dozen SaaS vendors, **check the discount programs you already have access to before you commit to the spec**. Lenny's Product Pass alone covered three of our vendors at meaningful discounts (ElevenLabs free for a year, plus Supabase Pro and a few others we don't use yet). YC alumni get separate discounts. AWS Activate, Google for Startups, Microsoft for Startups all have credit programs. Vendor friends and personal connections are sometimes worth a few thousand dollars in the first year.

For a bootstrapped project, the difference between "$22/month" and "free for a year" might decide whether the project ships or stalls. None of that money is going to change the architecture; it's just going to determine how much runway you have. Our cost model in `docs/vendor-costs.md` shows the at-list and at-discount numbers, and the discount column matters.

## Lessons

1. **Don't trust plugin defaults for latency-critical models.** ElevenLabs' plugin defaults to Turbo (more natural, slower). For a real-time agent that's the wrong default. Always specify the model explicitly and pin it in tests.
2. **Read the plugin's env var convention before naming yours.** `ELEVEN_API_KEY` (not `ELEVENLABS_API_KEY`) is what the plugin reads. Misnames produce confusing errors a layer below where you'd expect.
3. **Voice IDs, not voice names.** The streaming TTS plugins reference voices by ID. Capture the ID alongside the friendly name in your `.env.example` so the next person doesn't have to dig for it.
4. **Discount programs first, architecture second.** A year of free ElevenLabs through Lenny's Pass changed the cost model meaningfully but changed nothing about the architecture. Check what you have access to before committing to vendor specs.
5. **A "five-minute swap" is the load-bearing observation.** If your architecture lets you swap a vendor in five minutes, the actual vendor pick stops being load-bearing. Optimize the architecture for swappability and don't agonize over individual vendor picks.

---

*Code: [`agent/agent.py`](../../agent/agent.py) lines 124–127 (TTS instantiation). Pinned test: `agent/tests/test_session_config.py::test_tts_uses_flash_model`. ADR: ADR-001 in [`docs/decisions.md`](../decisions.md). Next: [LiveKit Agents validation in the playground](04-livekit-agents-validation.md).*
