"""Per-turn metrics writer for the Iris agent.

Writes one JSONL line per `metrics_collected` event into
``logs/turns/{call_id}.jsonl``. Each line carries the event type plus the
LiveKit Agents metric fields (STT / LLM / TTS / EOU / VAD), with
``speech_id`` as the correlation key downstream code uses to assemble
per-turn records.

Per-event lines (instead of in-memory per-turn aggregation) are crash-safe:
if the agent process dies mid-call, the lines written so far are intact
on disk. The aggregation step is moved downstream into a notebook or
small CLI — see ``docs/dataflow.md``.

PRD §5.5 acceptance criterion #5 (p95 EoU → first-bot-audio-frame
< 1600 ms) is computed downstream from these files plus the bot's own
``logs/bot-audio.jsonl`` (added in a later phase).
"""

from __future__ import annotations

import json
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def _to_dict(metric: Any) -> dict[str, Any]:
    """Best-effort: pull a flat dict out of any LiveKit metric object.

    Tries Pydantic ``model_dump()`` first (LiveKit Agents 1.5+ uses Pydantic
    v2), then falls back to ``__dict__``. Either way we end up with a JSON-
    serialisable shape — anything non-serialisable is stringified.
    """
    if hasattr(metric, "model_dump"):
        return metric.model_dump(mode="json")  # type: ignore[no-any-return]
    if hasattr(metric, "__dict__"):
        return {k: v for k, v in metric.__dict__.items() if not k.startswith("_")}
    return {"_repr": repr(metric)}


class TurnMetricsWriter:
    """Append-only JSONL writer for one call's metric events.

    Thread-safe (LiveKit Agents fires ``metrics_collected`` from various
    plugin callbacks; a lock keeps line writes atomic).
    """

    def __init__(self, log_dir: Path | str, call_id: str) -> None:
        self.call_id = call_id
        self.log_path = Path(log_dir) / f"{call_id}.jsonl"
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    def record(self, metric: Any) -> None:
        """Append one metric event as a JSONL line."""
        line = self._format(metric)
        with self._lock, self.log_path.open("a", encoding="utf-8") as f:
            f.write(line)
            f.write("\n")

    def _format(self, metric: Any) -> str:
        payload = _to_dict(metric)
        # Wrap with envelope fields the downstream aggregator relies on.
        envelope = {
            "call_id": self.call_id,
            "ts": datetime.now(tz=timezone.utc).isoformat(),
            # ``type`` is on LiveKit metric objects directly (LLMMetrics
            # has type="llm_metrics" etc.). If absent, use the class name.
            "type": payload.get("type") or type(metric).__name__,
            "speech_id": payload.get("speech_id"),
            "data": payload,
        }
        return json.dumps(envelope, default=str)
