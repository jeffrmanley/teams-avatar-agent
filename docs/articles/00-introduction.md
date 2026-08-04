# Building a Teams meeting bot with a real-time AI avatar in 2026

*A series.*

**Author:** Jeff Manley
**Series start:** April 2026

---

## Why this, why now

For about a decade, "AI participant in a video meeting" has been a thing you could fake in three different ways: you could record an MP4 and play it as a virtual webcam, you could screen-share a slide and dub over it, or you could send a transcript to a chatbot after the fact. None of these are participants. They're props.

What changed in 2026 is that all four of the layers needed for a real-time AI meeting participant matured at roughly the same time:

1. **Streaming STT** that finalizes a transcript in <150 ms (Deepgram Nova-3, AssemblyAI Universal-Streaming).
2. **Streaming LLMs** with sub-second time-to-first-token when the prompt is cached (Claude Sonnet 4.5 with the cache_control / ephemeral cache option).
3. **Streaming TTS** with TTFB under 100 ms and natural prosody (ElevenLabs Flash v2.5, Cartesia Sonic-3).
4. **Streaming photoreal avatars** that accept an audio track and publish lip-synced video into a room you control (Beyond Presence, HeyGen LiveAvatar — both adopted the BYO-LiveKit-room pattern in late 2025/early 2026).

When you stack these together, the end-to-end "mouth-closed → first syllable of response" budget lands in the **1.0–1.4 second range** on a good day. That's still slower than a human, but it's inside the band where humans don't perceive the agent as broken — about the same lag as a sluggish video call.

The interesting question stopped being *can you build this?* and started being *what's actually load-bearing in the stack, and how much of the work is the integration plumbing rather than the AI?*

Spoiler: most of the work is the plumbing.

This series is the field report from building one — **Iris**, a meeting participant for Microsoft Teams — and writing down the decisions, dead ends, and surprises in real time so the next person doesn't have to re-derive them.

## What we're building

Iris is an AI agent that joins a scheduled Microsoft Teams meeting as a full participant. It:

- Shows up as a participant in the roster with a "Bot" tag.
- Renders as a photoreal lip-synced avatar in its own video tile.
- Hears other participants via mixed audio.
- Reasons over the rolling conversation with Claude Sonnet 4.5.
- Speaks with a streaming TTS voice synced to the avatar.
- Can be interrupted mid-sentence and recover gracefully.
- Discloses on its first turn that it's an AI ("Hi, I'm Iris, an AI assistant participating in this meeting.") — not optional under EU AI Act Art. 50, which becomes enforceable on 2 August 2026.

What it explicitly does **not** do (for v1):

- Comprehend shared screens or whiteboards.
- Identify individual speakers beyond "someone is speaking vs not."
- Join PSTN-dialed phone participants.
- Work in Zoom, Google Meet, or any other platform.
- Run as a browser-automation bot. (Microsoft's MC1251206 detection rollout in mid-2026 is on a collision course with Recall.ai-style approaches; we're going through the supported Graph Communications media-bot path instead.)

## The architectural shape

The non-obvious thing about this project is that the right architecture is split into two halves connected by an off-the-shelf SFU:

```
Teams meeting (SFU)
       |  Opus + H.264 over SRTP/UDP
       v
+--------------------------------------+
| C# .NET 8 Graph Communications bot   |  <-- Windows-only (native Skype.Bots.Media)
| Kestrel directly on :443             |
+--------------------------------------+
       |  WebRTC
       v
+--------------------------------------+
| LiveKit room: meeting-{callId}       |  <-- the "seam"
+--------------------------------------+
       ^                            ^
       |                            |
+------+------+              +------+--------+
| Python      |              | HeyGen        |
| LiveKit     |              | LiveAvatar    |
| Agents      |              | (BYO room)    |
| - Deepgram  |              +---------------+
| - Claude    |
| - ElevenLabs|
+-------------+
```

There's exactly one Windows-bound piece: the C# Graph Communications media bot, because `Microsoft.Skype.Bots.Media` is a native Windows-only NuGet. Everything else — the Python agent, the avatar vendor, the LiveKit SFU — is portable.

The half that talks to Teams (the C# bot) doesn't know anything about Claude, Deepgram, ElevenLabs, or any avatar vendor. It only knows about a LiveKit room. The half that does the actual conversational AI (the Python agent) doesn't know anything about Teams, ILPIP, Microsoft Graph, or the bot framework. *It only knows about a LiveKit room.* The avatar vendor doesn't know anything about either. *It only knows about a LiveKit room.*

This shape is what we call **the seam**. Whichever half breaks, you can replace without touching the other. Vendor swaps that would otherwise be week-long migrations become one-env-var changes. We've already used the seam to A/B-test Beyond Presence against HeyGen LiveAvatar in 30 minutes (article 05), and to swap Cartesia for ElevenLabs in five (article 03).

If there's one architectural lesson worth carrying forward from this whole project, it's that one. The seam matters more than any individual vendor pick.

## What this series will cover

The first nine articles cover Phase 0 through Phase 2.2.5 — every step from "fresh Windows Server" to "the bot is publicly reachable on the internet, validated end-to-end, and ready to start handling actual Teams calls." Subsequent articles will pick up from Phase 2.3 (the LiveKit bridge — frame transport between the Teams SRTP streams and the LiveKit room) and Phase 3 (hardening: idle behavior, JWT validation, observability, longevity testing).

The articles, in order:

| # | Topic | Phase |
|---|---|---|
| 00 | Introduction (this article) | — |
| 01 | Environment setup on Windows Server 2025 | 0 |
| 02 | Vendor stack selection: the LiveKit seam | 0 |
| 03 | TTS swap: Cartesia → ElevenLabs Flash v2.5 | 1 |
| 04 | LiveKit Agents validation in the playground | 1 |
| 05 | Picking a real-time AI avatar vendor in one hour | 1 |
| 06 | The Microsoft 365 tenant odyssey | 2.0 |
| 07 | Cloudflare DNS + Let's Encrypt TLS for the bot | 2.0 |
| 08 | The C# bot foundation: scaffold to public reachability | 2.1–2.2.5 |

Each article is self-contained; you should be able to drop into any one of them without having read the others. Internal links are sparing and appear only where they actually save the reader time.

## Style and ground rules

A few notes about how these are written, mostly so you know what to expect:

- **First-person plural, past tense.** "We did X. Y didn't work. Z worked." Not because there's a team — at this stage it's one human (Jeff) plus Claude Code, doing pair-programming sessions with an AI. The "we" is honest about that arrangement.
- **Specific over general.** When something costs $43.20 a month or fails with `Error 7504`, those numbers and codes are in the text. Vague summaries don't help anyone debugging.
- **Costs disclosed.** Every vendor choice has a per-meeting cost attached, sourced from `docs/vendor-costs.md`. We're at roughly $4.80 per 30-minute meeting at our current vendor mix; the article series shows where each dollar lands.
- **Mistakes called out as mistakes.** When we picked the wrong thing, we say so and explain what we picked instead.
- **Lessons section at the end.** Every article ends with 3–5 takeaways someone could apply on a different project. If you only have a minute, read those.

## A note on the AI-pair-programming workflow

The single biggest force multiplier on this project hasn't been any individual vendor — it's been Claude Code as a build partner. Most of the code was written in conversation with Claude, with the human (Jeff) holding the architectural through-line and making the tradeoff calls. The PRD was treated as an executable spec for the AI to follow phase by phase, with stop-and-ask gates for anything that costs money or commits to an external configuration.

That workflow shows up in these articles in two specific places:

1. **Decisions are written down at the time they're made.** The architecture decision log (`docs/decisions.md`) was built up commit by commit, not retrofitted at the end. That's because Claude needed it as much as we did — to load context for the next session.
2. **Pinned tests guard against drift.** Things like "the disclosure must be un-interruptible," "the LLM must use the ephemeral cache mode," and "the model must be `claude-sonnet-4-5` not `claude-sonnet-4-6`" are all asserted in the test suite. Not because they're hard to spec — because they're easy to silently regress when an AI is editing the file weeks later.

We'll come back to both points in later articles where they paid off.

## What's the alternative path, briefly

For completeness, we considered and rejected two other approaches:

**Recall.ai-style browser automation.** Recall.ai (and competitors) run a headless Chrome that joins the meeting like a human would, scraping audio and video from the rendered page. This works *today*, charges by the meeting-minute, and skips the entire Microsoft tenant + bot framework path. It also has a serious problem coming: Microsoft's MC1251206 ("New bot detection in Teams calls and meetings") rollout, scheduled for May–June 2026, explicitly targets browser-automation bots. Whatever Recall.ai customers are paying for in 2026 might not work in 2027. We weren't willing to ship onto a foundation that Microsoft was actively planning to break.

**Remotion-based offline rendering.** Remotion is a great React-based video renderer for offline content. It is not a real-time pipeline. Builds based on Remotion sometimes get described as "live AI avatars" by marketing teams, but they're not — they're 30-second clip generators with a fast turnaround.

The supported Graph Communications media-bot path that this project uses is the one Microsoft itself documents and supports. It's also harder, because it requires a tenant, an Azure Bot resource, public IPv4, a TLS cert, and four admin-consented Graph permissions. Articles 06 and 07 cover what that costs in time and money.

## The point of the series

We were unable to find an honest end-to-end writeup of building one of these in 2026. The Microsoft documentation tells you what each API does. The vendor blogs tell you what their product does. Neither tells you what happens when you try to bolt all of it together on a Friday afternoon and find out that, for example, M365 Family doesn't qualify for a tenant, that Cloudflare's HTTP proxy breaks Graph's cert chain, or that Anthropic's prompt cache requires a 1024-token minimum that nothing in the docs makes obvious.

The hope is that someone trying to build a similar agent in 2026 or 2027 finds this and saves a week.

— Jeff
