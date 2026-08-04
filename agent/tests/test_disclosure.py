"""Art. 50 first-turn disclosure tests.

These lock in three guarantees from PRD §4.2 and §6.1:

1. The system prompt contains the literal disclosure sentence.
2. The system prompt is loadable as UTF-8 from `agent/prompts/system.md`.
3. The `entrypoint` function, when imported, calls `session.say()` with the
   disclosure before any other user-visible utterance.

We assert on the *static* content of the code + prompt rather than running a
real LiveKit session — the point of this test is to fail CI if somebody edits
the prompt or deletes the `session.say(...)` call without noticing.
"""

from __future__ import annotations

import inspect
import re
from pathlib import Path

import agent as agent_mod

DISCLOSURE_SENTENCE = (
    "Hi, I'm Iris, an AI assistant participating in this meeting."
)


def test_system_prompt_contains_disclosure():
    prompt = agent_mod.load_system_prompt()
    assert DISCLOSURE_SENTENCE in prompt, (
        "system.md must contain the verbatim Art. 50 disclosure sentence"
    )


def test_system_prompt_file_is_utf8():
    path = Path(agent_mod.SYSTEM_PROMPT_PATH)
    # Reading with utf-8 must not raise.
    path.read_text(encoding="utf-8")


def test_entrypoint_emits_disclosure_before_listening():
    src = inspect.getsource(agent_mod.entrypoint)
    # The disclosure must appear literally in the entrypoint, as a session.say
    # call, so regressions are obvious. We tolerate the agent-name substitution
    # via f-string by matching on the invariant prefix.
    pattern = re.compile(
        r"await\s+session\.say\(\s*f?\"Hi, I'm \{?AGENT_NAME\}?, "
        r"an AI assistant participating in this meeting\."
    )
    assert pattern.search(src), (
        "entrypoint() must call session.say() with the Art. 50 disclosure "
        "as its first user-visible utterance"
    )


def test_disclosure_not_interruptible():
    """A user talking over the agent on turn 1 must not suppress the
    disclosure. The `allow_interruptions=False` flag on `session.say` is how
    we enforce that — pin it."""
    src = inspect.getsource(agent_mod.entrypoint)
    assert "allow_interruptions=False" in src, (
        "first-turn disclosure must be called with allow_interruptions=False"
    )
