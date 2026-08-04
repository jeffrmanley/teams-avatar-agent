"""Session config pins.

Static assertions that the agent is wired to the exact models and knobs the
PRD calls out (§4.2). These tests do not spin up a session — they inspect
source text because the alternative is booting all the plugins (which
downloads weights and hits real APIs).
"""

from __future__ import annotations

import inspect

import agent as agent_mod


def _src() -> str:
    return inspect.getsource(agent_mod.entrypoint)


def test_llm_is_claude_sonnet_4_5():
    src = _src()
    assert 'model="claude-sonnet-4-5"' in src


def test_llm_has_prompt_caching():
    src = _src()
    assert 'caching="ephemeral"' in src, (
        "prompt caching is mandatory — without it, TTFT doubles on turn 2+"
    )


def test_stt_is_nova_3_multi():
    src = _src()
    assert 'model="nova-3"' in src
    assert 'language="multi"' in src


def test_stt_endpointing_is_low():
    # Turn detection is owned by LiveKit turn-detector, not Deepgram — if
    # Deepgram endpointing creeps up, it will fight the turn-detector and
    # add hundreds of ms to EoT.
    src = _src()
    assert "endpointing_ms=25" in src


def test_tts_is_eleven_flash():
    # Flash v2.5 picked over Turbo for the ~75 ms TTFB. Turbo is ~2-4x slower
    # to first audio, which busts our 1.1-1.4 s total turn budget.
    src = _src()
    assert 'model="eleven_flash_v2_5"' in src


def test_turn_detector_is_english_model():
    src = _src()
    assert "EnglishModel()" in src


def test_room_output_audio_disabled():
    # Beyond Presence republishes the lip-synced audio with its video frames.
    # If the agent *also* publishes raw TTS audio, participants hear it twice.
    src = _src()
    assert "RoomOutputOptions(audio_enabled=False)" in src


def test_avatar_session_attached():
    """Both providers must remain wired so AVATAR_PROVIDER can flip them
    without code changes."""
    src = _src()
    assert "bey.AvatarSession" in src
    assert "liveavatar.AvatarSession" in src
    assert "avatar.start(session, room=ctx.room)" in src


def test_avatar_provider_env_switch():
    src = inspect.getsource(agent_mod)
    assert 'AVATAR_PROVIDER = os.getenv("AVATAR_PROVIDER", "bey")' in src, (
        "AVATAR_PROVIDER must default to 'bey' so existing setups don't break"
    )


def test_preemptive_generation_enabled():
    # Cold-cache turns 1-3 of every new meeting will feel sluggish without
    # this. With it, the LLM call starts before the turn-detector confirms
    # end-of-utterance; saves ~400-600 ms.
    src = _src()
    assert "preemptive_generation=True" in src
