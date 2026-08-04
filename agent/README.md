# agent/ — LiveKit Agents entrypoint

Python app that runs Claude Sonnet 4.5 + Deepgram Nova-3 STT + ElevenLabs Flash v2.5 TTS + LiveKit turn-detector v2 + Beyond Presence avatar inside a single `AgentSession`.

## Requirements

- Python 3.11 or 3.12 (**not** 3.13 — some LiveKit Agents plugins don't build on 3.13)
- `uv` (Astral)
- Populated `../.env` (see `../.env.example`)

## Install

```powershell
cd agent
uv sync
```

## Verify imports

```powershell
uv run python -c "import livekit.agents; import livekit.plugins.bey; import livekit.plugins.anthropic; import livekit.plugins.deepgram; import livekit.plugins.elevenlabs; print('ok')"
```

Must print `ok`.

## Run against the LiveKit playground

```powershell
uv run python agent.py dev
```

Then open <https://agents-playground.livekit.io/>, point it at your `LIVEKIT_URL`, join the room. Your mic goes to the agent; the agent voice (rendered by the Beyond Presence avatar) plays back with a video tile.

## Tests

```powershell
uv run pytest
```

The test suite is intentionally static — it asserts configuration is right without spinning up real sessions or calling vendor APIs. The critical test is `test_disclosure.py`: the EU AI Act Art. 50 first-turn disclosure must stay wired.

## Notes on knobs

- `endpointing_ms=25` on Deepgram is deliberate — turn detection is owned by the LiveKit turn-detector plugin.
- `cache_control={"type": "ephemeral"}` on the Claude LLM block enables prompt caching. Without it, TTFT roughly doubles on turn 2+.
- `RoomOutputOptions(audio_enabled=False)` is required because Beyond Presence republishes the lip-synced audio with its video; publishing raw TTS *and* the avatar would double-audio the room.
- Extended thinking is intentionally **not** enabled on Claude — it destroys TTFT.
