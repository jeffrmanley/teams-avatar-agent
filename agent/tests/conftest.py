"""Pytest fixtures and env scaffolding for agent tests."""

from __future__ import annotations

import os
import sys
from pathlib import Path

# Make the `agent` package importable without installing the project.
AGENT_DIR = Path(__file__).resolve().parent.parent
if str(AGENT_DIR) not in sys.path:
    sys.path.insert(0, str(AGENT_DIR))

# Tests should never hit the live APIs. Assign placeholder env values if
# the real .env hasn't been loaded, so imports that call require_env at
# module scope don't explode.
_STUBS = {
    "ANTHROPIC_API_KEY": "test-anthropic",
    "DEEPGRAM_API_KEY": "test-deepgram",
    "ELEVEN_API_KEY": "test-eleven",
    "ELEVEN_VOICE_ID": "21m00Tcm4TlvDq8ikWAM",
    "BEY_API_KEY": "test-bey",
    "BEY_AVATAR_ID": "test-avatar",
    "LIVEAVATAR_API_KEY": "test-liveavatar",
    "LIVEAVATAR_AVATAR_ID": "test-liveavatar-avatar",
    "AVATAR_PROVIDER": "bey",
    "LIVEKIT_URL": "wss://example.invalid",
    "LIVEKIT_API_KEY": "test-key",
    "LIVEKIT_API_SECRET": "test-secret",
    "AGENT_NAME": "Iris",
}
for k, v in _STUBS.items():
    os.environ.setdefault(k, v)
