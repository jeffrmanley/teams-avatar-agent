"""Teams Avatar Agent — LiveKit Agents entrypoint.

Connects Claude Sonnet 4.5 + Deepgram Nova-3 STT + Cartesia Sonic-3 TTS +
LiveKit turn-detector v2 + Beyond Presence photoreal avatar into a single
`AgentSession`. Audio from room participants is transcribed, fed to Claude,
streamed to Cartesia, and rendered as lip-synced video by Beyond Presence,
which publishes into the same room as a separate participant.

Run locally against the LiveKit playground:
    uv sync
    uv run python agent.py dev

Then open https://agents-playground.livekit.io/ and join the same room.
"""

from __future__ import annotations

import logging
import os
import time
from pathlib import Path

import structlog
from dotenv import load_dotenv
from livekit import agents
from livekit.agents import (
    Agent,
    AgentSession,
    JobContext,
    RoomOutputOptions,
    WorkerOptions,
    cli,
    metrics,
)
from livekit.plugins import anthropic, bey, deepgram, elevenlabs, liveavatar, silero
from livekit.plugins.turn_detector.english import EnglishModel

from turn_metrics import TurnMetricsWriter

# ---------------------------------------------------------------------------
# Env + logging
# ---------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
load_dotenv(REPO_ROOT / ".env")

LOG_LEVEL = os.getenv("LOG_LEVEL", "INFO").upper()
logging.basicConfig(level=LOG_LEVEL, format="%(message)s")
structlog.configure(
    processors=[
        structlog.processors.add_log_level,
        structlog.processors.TimeStamper(fmt="iso"),
        structlog.processors.JSONRenderer(),
    ],
    wrapper_class=structlog.make_filtering_bound_logger(getattr(logging, LOG_LEVEL)),
)
log = structlog.get_logger("iris.agent")

AGENT_NAME = os.getenv("AGENT_NAME", "Iris")
AVATAR_PROVIDER = os.getenv("AVATAR_PROVIDER", "bey").lower()

SYSTEM_PROMPT_PATH = Path(__file__).resolve().parent / "prompts" / "system.md"


def load_system_prompt() -> str:
    return SYSTEM_PROMPT_PATH.read_text(encoding="utf-8").strip()


def require_env(name: str) -> str:
    val = os.getenv(name)
    if not val:
        raise RuntimeError(f"missing required env var: {name} (see .env.example)")
    return val


# ---------------------------------------------------------------------------
# Agent
# ---------------------------------------------------------------------------


class IrisAgent(Agent):
    """Claude-backed meeting participant.

    Subclassing `Agent` (instead of using the default) lets us pin the system
    prompt and, later, attach tools (calendar lookup, note capture, etc.)
    without rewriting the session wiring.
    """

    def __init__(self) -> None:
        super().__init__(instructions=load_system_prompt())


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------


async def entrypoint(ctx: JobContext) -> None:
    # local_participant is not addressable until session.start triggers
    # ctx.connect(), so log identity after start instead.
    log.info("agent_starting", agent=AGENT_NAME, room=ctx.room.name)

    eleven_voice = require_env("ELEVEN_VOICE_ID")

    # STT: turn-detection handled by LiveKit turn-detector, not Deepgram,
    # so endpointing_ms is kept low.
    stt = deepgram.STT(
        model="nova-3",
        language="multi",
        endpointing_ms=25,
    )

    # LLM: Claude Sonnet 4.5 per PRD §4.2. Newer (claude-sonnet-4-6) is
    # available; revisit after Phase 1 baselines. Extended thinking is *off*
    # (destroys TTFT). `caching="ephemeral"` is the plugin's supported
    # switch; it injects cache_control markers on system prompt + history.
    llm = anthropic.LLM(
        model="claude-sonnet-4-5",
        temperature=0.6,
        caching="ephemeral",
    )

    # eleven_flash_v2_5 is the lowest-TTFB ElevenLabs model (~75 ms),
    # comparable to Cartesia Sonic-3. eleven_turbo_v2_5 (the plugin default)
    # is more natural but ~150-300 ms TTFB - too slow for real-time turns.
    tts = elevenlabs.TTS(
        model="eleven_flash_v2_5",
        voice_id=eleven_voice,
    )

    # preemptive_generation lets the LLM start generating before the turn
    # detector confirms end-of-utterance. If the user resumes speaking the
    # in-flight call is cancelled (cheap); if they really stopped, the
    # response is already part-streamed by the time EOU fires. Typical
    # latency saving: 400-600 ms on cold-cache turns. Cost: a small uplift
    # in cancelled LLM calls.
    session = AgentSession(
        stt=stt,
        llm=llm,
        tts=tts,
        vad=silero.VAD.load(),
        turn_detection=EnglishModel(),
        preemptive_generation=True,
    )

    # Per-turn metrics collector. Dumps p50/p95 summaries at session end.
    usage = metrics.UsageCollector()

    # JSONL writer for per-turn metrics. Each `metrics_collected` event is
    # appended as its own line; downstream aggregation (notebook / CLI)
    # joins by speech_id to assemble turn records. Crash-safe — no
    # in-memory buffering between writes. See docs/dataflow.md.
    turn_log_dir = Path(os.getenv("BOT_TURN_LOG_DIR", str(REPO_ROOT / "logs" / "turns")))
    # Bot creates rooms named "meeting-{callId}". Drop the prefix for the
    # filename so it matches the bot's callId space for cross-stream joins.
    call_id = ctx.room.name.removeprefix("meeting-") or ctx.room.name
    metrics_writer = TurnMetricsWriter(turn_log_dir, call_id)
    log.info("turn_metrics_writer", path=str(metrics_writer.log_path))

    @session.on("metrics_collected")
    def _on_metrics(ev):
        metrics.log_metrics(ev.metrics)
        usage.collect(ev.metrics)
        try:
            metrics_writer.record(ev.metrics)
        except Exception as exc:  # noqa: BLE001 - never let logging crash the agent
            log.warning("metrics_write_failed", error=str(exc))

    async def _dump_summary():
        try:
            summary = usage.get_summary()
            log.info("session_usage_summary", summary=str(summary))
        except Exception as exc:  # noqa: BLE001 - best-effort logging
            log.warning("usage_summary_failed", error=str(exc))

    ctx.add_shutdown_callback(_dump_summary)

    # Avatar provider is switchable via AVATAR_PROVIDER env var so we can
    # A/B test photoreal vendors without code changes. Both vendors use the
    # same LiveKit BYO-room pattern — they join our room as their own
    # participant and publish lip-synced A/V from our TTS audio.
    if AVATAR_PROVIDER == "bey":
        avatar = bey.AvatarSession(avatar_id=require_env("BEY_AVATAR_ID"))
    elif AVATAR_PROVIDER == "liveavatar":
        # HeyGen LiveAvatar in LITE mode (1 credit/min). The plugin reads
        # LIVEAVATAR_API_KEY natively; we surface avatar_id explicitly so
        # the failure mode is a clean error rather than a silent default.
        avatar = liveavatar.AvatarSession(avatar_id=require_env("LIVEAVATAR_AVATAR_ID"))
    else:
        raise RuntimeError(
            f"unknown AVATAR_PROVIDER {AVATAR_PROVIDER!r}; expected 'bey' or 'liveavatar'"
        )
    log.info("avatar_provider", provider=AVATAR_PROVIDER)
    await avatar.start(session, room=ctx.room)

    # Beyond Presence publishes lip-synced A/V; disable our own room audio
    # output so participants don't hear the TTS + avatar audio doubled.
    await session.start(
        room=ctx.room,
        agent=IrisAgent(),
        room_output_options=RoomOutputOptions(audio_enabled=False),
    )

    # First-turn disclosure: Art. 50 requires an audible introduction on
    # every meeting. We inject it as a greeting rather than waiting for the
    # user to speak, so a silent audience still hears it.
    await session.say(
        f"Hi, I'm {AGENT_NAME}, an AI assistant participating in this meeting.",
        allow_interruptions=False,
    )

    log.info(
        "agent_ready",
        room=ctx.room.name,
        identity=ctx.room.local_participant.identity,
        started_at=time.time(),
    )


def main() -> None:
    cli.run_app(WorkerOptions(entrypoint_fnc=entrypoint))


if __name__ == "__main__":
    main()
