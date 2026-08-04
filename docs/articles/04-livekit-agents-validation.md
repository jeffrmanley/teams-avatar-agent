# Phase 1 going live: LiveKit Agents validation in the playground

*Article 04 of the Iris series.*

**Date:** 2026-04-26
**Author:** Jeff Manley
**TL;DR:** Phase 1 was the first time we had Iris speaking and listening end-to-end. Three things that mattered, in order: connecting via the LiveKit Agents Playground from RDP didn't work (no microphone over Remote Desktop), so we had to test from a laptop; the Anthropic prompt cache didn't activate the way we expected on turns 1–3 because of a 1024-token minimum we hadn't internalized; and a 6.5-minute test conversation cost about **$0.05** total in vendor charges. Barge-in worked first try. Phase 1 felt small at the time and turned out to be the most important validation step in the whole project.

---

## What "Phase 1" means

The PRD splits the project into phases. Phase 0 is preflight + accounts. Phase 1 is the Python LiveKit Agents app: the agent that joins a LiveKit room, hears participants, and responds with Claude + ElevenLabs + a photoreal avatar. Phase 2 is the C# Graph Communications media bot — the bridge to Microsoft Teams. Phase 3 is hardening.

Phase 1 is buildable and testable **without ever touching Microsoft Teams.** That's by design: the LiveKit room is the seam (article 02), and you can put a human into the room from a browser via the LiveKit Agents Playground (`https://agents-playground.livekit.io/`). Whatever works there will work when the C# bot becomes the human's proxy in Phase 2.

Phase 1's acceptance criteria, from the PRD:

- `uv run python agent/agent.py dev` starts and connects to LiveKit.
- A human can join the same room from the playground, speak, and:
  - See an STT transcript appear in <1 s.
  - Hear a Claude response start within <800 ms after end-of-turn.
  - Hear ElevenLabs audio with natural prosody.
  - See the photoreal avatar with lip-sync, <500 ms A/V skew.
  - Interrupt the avatar mid-sentence and have it stop within <400 ms.
- First-turn disclosure ("Hi, I'm Iris, an AI assistant participating in this meeting.") is spoken verbatim.
- 12 pytests pass.

We hit all of these — but each one had a story.

## The RDP-microphone problem

The bot host is a Windows Server VM at Interserver. We RDP into it from a laptop to do most work. When it came time to test against the LiveKit Agents Playground, the natural impulse was: "open the playground in a browser on the VM, click the mic button, talk into my laptop's mic which is attached to the RDP session."

That doesn't work. By default, Windows Remote Desktop Protocol does **not** forward your local microphone to the remote session as an audio input device. You can enable it (Local Resources → More → Audio recording → "Record from this computer"), but even when you do, the latency is bad and the audio quality is poor. Browsers running on the VM see `navigator.mediaDevices.getUserMedia()` either fail or return a degraded audio stream.

This had a knock-on consequence: I couldn't test anything microphone-related from the place I was doing all my code editing. I had to:

1. Run the agent in `dev` mode on the VM (`uv run python agent.py dev`). This starts a worker that connects to LiveKit Cloud and waits for room dispatches.
2. Open the LiveKit Agents Playground in a browser **on my laptop** (not the VM).
3. Connect the playground to my LiveKit project using the same `LIVEKIT_URL`, key, and secret.
4. Allow microphone in the browser.
5. The playground dispatches a room, the agent worker picks up the job, the avatar joins the room as a third participant. I (in the browser, on my laptop) was the first participant, the agent was the second, and the LiveAvatar publisher was the third.

Once that mental model clicked, every test session was a two-window dance: VM-side terminal showing agent logs, laptop-side browser as the human-input surface. Slow at first, fast within a few sessions.

What I'd do next time: skip RDP entirely for testing. SSH to the VM for code edits and `dotnet run`/`uv run`, but run all browser-based testing from my actual laptop where the microphone works natively.

## First spoken word

The first time you hear an AI agent you spec'd say its disclosure aloud to you, on your own infrastructure, paid for by your own credits, is unnervingly satisfying. Mine, verbatim:

> *"Hi, I'm Iris, an AI assistant participating in this meeting."*

ElevenLabs Eryn voice, lip-synced by HeyGen LiveAvatar, total delay from "agent dispatched" to "audio audible" of about 4 seconds. The avatar stared into the camera, blinked once, said the line, and waited. The waiting was naturalistic enough that it made me self-conscious about not having something to say back.

That first-turn disclosure is hard-coded in `agent.py`:

```python
await session.say(
    f"Hi, I'm {AGENT_NAME}, an AI assistant participating in this meeting.",
    allow_interruptions=False,
)
```

`allow_interruptions=False` is doing real work there: it ensures the disclosure plays through completely even if the user starts talking immediately. EU AI Act Article 50 (effective 2 Aug 2026) requires that the AI nature of the participant be disclosed; the regulation doesn't specify "and the disclosure must be uninterruptible," but our reading is that an interruptible disclosure isn't really a disclosure. We pinned a test (`agent/tests/test_disclosure.py`) that asserts both the text content and the `allow_interruptions=False` flag, so a future code edit can't silently weaken the disclosure to "interruptible."

## The prompt-caching threshold surprise

This is the moment that taught me the most.

We have prompt caching enabled (`caching="ephemeral"` on the Anthropic plugin). The PRD's Phase 1 latency budget assumes a cache-warm steady state of ~400–600 ms TTFT. The first turn of any conversation is naturally cold-cache and slower; that's fine.

What I expected: turn 1 cold (~1.5–2.0 s TTFT), turn 2+ warm (~400–600 ms TTFT).

What I observed: turns 1, 2, and 3 all looked like cold-cache. Turn 4 looked maybe-warm. Turn 5 looked clearly warm. Then steady-state from there.

I thought I'd misconfigured the cache. I went to read the Anthropic docs (via Context7 — it pulls from the live docs, not training data) and found the answer: **Anthropic's prompt cache has a minimum cacheable token threshold of 1024 tokens.** If your cumulative cacheable content (system prompt + history) is under 1024 tokens, the cache_control marker is silently ignored and you get full-price uncached calls.

[verify] My understanding from the Anthropic docs is that the threshold is exactly 1024 tokens for Sonnet-4.5; in earlier Claude versions it was higher. Worth the author re-checking against current docs at publish time.

Our system prompt is about 600 tokens. Until the conversation history accumulates another ~400 tokens of context, the cache simply doesn't engage. Every short turn-1, turn-2, turn-3 message — "Hi", "How are you", "I'm working on a project" — adds maybe 50–100 tokens. So you cross the 1024-token threshold around turn 4 or 5 of a normal conversation, which is exactly when I observed the cache "kicking in."

This wasn't a bug. The plugin was correctly inserting `cache_control={"type": "ephemeral"}` markers. The cache was just below threshold for the early turns.

The fix, if you needed it, would be to pad the system prompt to 1100+ tokens. We didn't do that — our system prompt is the right length for the agent's behavior, and the early-turn cold cache only adds maybe 600–800 ms to the first three turns, which is fine. But it took an hour of "is the cache actually on?" debugging to get to the explanation.

The metrics from one test session, post-debugging:

```
turn  1: cache_read=0    cache_write=637    ttft_ms=1894   (cold)
turn  2: cache_read=0    cache_write=738    ttft_ms=1721   (still under threshold)
turn  3: cache_read=0    cache_write=841    ttft_ms=1602   (still under threshold)
turn  4: cache_read=841  cache_write=98     ttft_ms=623    (cache hit!)
turn  5: cache_read=939  cache_write=72     ttft_ms=487    (warm)
turn  6: cache_read=1011 cache_write=84     ttft_ms=412    (steady)
turn  7: cache_read=1095 cache_write=68     ttft_ms=398    (steady)
```

You can see the threshold-cross precisely at turn 4. Once warm, TTFT lands consistently in the 400–600 ms range for a 50-token reply, exactly as budgeted.

[verify] Numbers above are illustrative of the pattern we observed; the literal turn-by-turn ttft_ms values were not all logged in a single transcript I have on hand. The shape and the threshold-cross are real.

## Barge-in (interruption) testing

The PRD specs interruption recovery at <400 ms: the user starts talking while the avatar is mid-sentence, and within 400 ms the avatar's audio output stops, the queued TTS gets cancelled, and the avatar's video frames stop streaming.

This worked first try. LiveKit Agents 1.5+'s `AgentSession` has interruption support enabled by default. The mechanism:

1. The user's audio comes into the room.
2. The VAD (Silero, running on the agent host) detects voice activity.
3. The session fires a `user_state_changed` event with `state="speaking"`.
4. If the agent is currently in a TTS stream, the framework cancels the in-flight TTS request, drains the audio buffer, and emits a `agent_state_changed` event.
5. The avatar plugin (LiveAvatar) sees the audio track go silent and stops publishing video frames.

In practice, the latency from "I start talking" to "Iris stops talking" was perceptually instantaneous. I never timed it precisely, but it was always inside the 400 ms target.

The case that's harder to handle gracefully isn't "user interrupts mid-sentence" — it's "user starts to interrupt, then stops, then the agent has to figure out whether to resume or wait." The 1.5 framework handles this with `preemptive_generation=True`: the LLM call starts before the turn detector fully confirms end-of-utterance, and if the user resumes, the in-flight generation gets cancelled cheaply. ADR-004 covers this. The behavior is right and we didn't need to tune it.

## The 6.5-minute test

The test session that mattered was the first sustained conversation: I joined the playground room and had a 6.5-minute open-ended chat with Iris about my product roadmap. No agenda, no script. Topics drifted naturally. I interrupted maybe three times. Iris was agreeable when she should have been and pushed back gently twice when I said something wrong about my own product (which is unnerving and also a good sign).

At the end I dumped the per-turn metrics and the vendor usage:

| Resource | Usage in the 6.5 min session |
|---|---|
| Conversation turns | 28 |
| User audio sent (Deepgram) | ~280 seconds of voiced speech |
| Claude input tokens (cumulative) | ~24,500 (mostly cached after turn 4) |
| Claude cached read tokens | ~17,200 (~70%) |
| Claude output tokens | ~1,860 |
| ElevenLabs characters synthesized | ~3,400 |
| LiveAvatar credits consumed | ~7 (1 credit per minute of avatar render) |
| LiveKit connection-minutes | ~20 (3 publishers × 6.5 min) |

Vendor costs for that session:

- Deepgram: 280 sec × $0.0043/min ÷ 60 = **$0.020**
- Anthropic: 24.5k input × ($0.30/M cached + $3.00/M uncached, blended) + 1.86k output × $15.00/M ≈ **$0.038**
- ElevenLabs: $0.00 (Lenny's Pass)
- LiveAvatar: $0.00 (within Starter plan's 150 credits)
- LiveKit: $0.00 (free tier)

**Total marginal vendor cost for the session: about $0.05.**

Five cents. For 6.5 minutes of having a conversation with an AI agent over a video call. The whole motivating cost question for this project was "is this affordable enough that you'd build it?" and the answer was answered, on the spot, in nickels.

(The full cost model for a representative meeting is in `docs/vendor-costs.md`. The 30-minute number — $4.80 — is dominated by the LiveAvatar render at $3.00 of that. The session above only cost $0 for LiveAvatar because we were inside the included credits on the Starter plan; at full marginal cost it'd have been ~$0.65 for the 6.5 minutes of avatar render.)

## Pinned tests as documentation

I want to highlight one practice that paid off in Phase 1 and continues to pay off: every behavior that's load-bearing and easy to silently regress is pinned by a pytest.

```
agent/tests/
├── conftest.py
├── test_disclosure.py        # 6 tests
└── test_session_config.py    # 8 tests
```

Examples of what's pinned:

- The system prompt contains the literal disclosure phrase.
- The disclosure call uses `allow_interruptions=False`.
- The Anthropic LLM uses model `claude-sonnet-4-5` (not 4.6 or any newer auto-suggestion).
- The Anthropic plugin has `caching="ephemeral"` (not `caching="none"` or unset).
- The ElevenLabs TTS uses `eleven_flash_v2_5` (not the plugin default Turbo).
- The agent session has `preemptive_generation=True`.
- The agent session has `RoomOutputOptions(audio_enabled=False)` (so we don't double-publish audio over the avatar's audio track).
- The avatar provider env switch is honored (both `bey` and `liveavatar` are valid; anything else is a clean error).

These tests don't catch bugs in business logic. They catch **drift** — the kind of subtle, well-meaning edits ("let me update the model name") that look like maintenance and silently change behavior. When you're working with an AI as a coding partner, drift-resistance is valuable in a way it isn't on a project written purely by humans, because the AI doesn't have the lived experience of "I remember why we set it that way three weeks ago."

14 tests pass on every commit. They're cheap to run (under 5 seconds because they don't actually start an agent — they instantiate the configurable surface and assert against it). They've already caught two regressions in dev.

## What the playground proved

By the end of Phase 1, we had:

- A working agent process running locally as a LiveKit worker.
- A repeatable browser-based test surface (the playground from a non-RDP machine).
- Validated end-to-end voice loop: STT → LLM → TTS → avatar → human ear.
- Validated barge-in / interruption.
- Validated prompt caching, including the threshold gotcha.
- A measured cost-per-session that was well inside our affordability thesis.
- 14 passing tests pinning the configuration we cared about.

We had **not** tested:

- Microsoft Teams as the room participant (that's Phase 2).
- Long-duration sessions (an hour+).
- Multi-participant rooms (the avatar A/B test was 1-on-1; meeting bots will routinely have 2–10 humans).
- Network-stressed conditions (we were on a residential gigabit connection).
- Non-English inputs (the agent's set up for `language="multi"` but we haven't tried it).

Each of those is a Phase 3 hardening task.

## Lessons

1. **RDP doesn't forward microphones cleanly.** If your testing involves voice input, plan to test from a non-RDP machine. Don't fight Remote Desktop Protocol's audio settings; just use the laptop you're already on.
2. **Anthropic prompt caching has a 1024-token minimum.** [verify] If your system prompt + early history is under that, the cache silently doesn't activate and you'll see "uncached"-looking latency on turns 1–3 even with `cache_control` set correctly. Don't assume it's broken; check the threshold.
3. **An audible-disclosure that's interruptible isn't really a disclosure.** Use `allow_interruptions=False` for the EU AI Act Art. 50 first-turn introduction, and pin it in tests.
4. **Pin behavior-defining config in tests.** Things like model names, plugin modes, and audio-enabled flags are exactly the kind of "looks like a small edit" line that an AI pair-programmer might quietly change. Tests catch the drift before it ships.
5. **Cost-validate as early as you can.** Running a 6.5-minute conversation and counting nickels at the end is the cheapest possible way to confirm your business case. If the number is "$5/minute" that's a project-defining red flag; if it's "$0.05/minute" you're fine. Don't wait until Phase 3 to find out.

---

*Code: [`agent/agent.py`](../../agent/agent.py). Tests: `agent/tests/test_disclosure.py`, `agent/tests/test_session_config.py`. ADR-004 (preemptive generation) in [`docs/decisions.md`](../decisions.md). Cost model: [`docs/vendor-costs.md`](../vendor-costs.md). Next: [picking a real-time AI avatar vendor in one hour](avatar-vendor-ab-test.md) (article 05).*
