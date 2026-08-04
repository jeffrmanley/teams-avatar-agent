"""Tests for the per-turn metrics JSONL writer."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import pytest

import sys
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from turn_metrics import TurnMetricsWriter  # noqa: E402


@dataclass
class _FakeMetric:
    """Plain object that mimics a LiveKit metric dataclass.

    We don't import a real one here — TurnMetricsWriter is duck-typed
    and the real metrics classes change across LiveKit Agents versions.
    A small fake keeps these tests hermetic.
    """

    type: str
    speech_id: str | None
    ttft: float | None = None
    duration: float | None = None


def test_writer_creates_log_file_in_call_specific_path(tmp_path):
    writer = TurnMetricsWriter(tmp_path, "callA")
    assert writer.log_path == tmp_path / "callA.jsonl"


def test_writer_creates_parent_directory_if_missing(tmp_path):
    nested = tmp_path / "deep" / "logs" / "turns"
    writer = TurnMetricsWriter(nested, "callB")
    writer.record(_FakeMetric(type="llm_metrics", speech_id="s1", ttft=0.45))
    assert (nested / "callB.jsonl").exists()


def test_writer_emits_one_jsonl_line_per_event(tmp_path):
    writer = TurnMetricsWriter(tmp_path, "callC")
    writer.record(_FakeMetric(type="llm_metrics", speech_id="s1", ttft=0.45))
    writer.record(_FakeMetric(type="tts_metrics", speech_id="s1", duration=1.2))
    writer.record(_FakeMetric(type="eou_metrics", speech_id="s1"))

    lines = (tmp_path / "callC.jsonl").read_text(encoding="utf-8").strip().splitlines()
    assert len(lines) == 3
    for line in lines:
        # Each line is independently parseable JSON.
        obj = json.loads(line)
        assert obj["call_id"] == "callC"
        assert obj["speech_id"] == "s1"
        assert "ts" in obj
        assert "type" in obj
        assert "data" in obj


def test_writer_falls_back_to_class_name_when_metric_lacks_type_field(tmp_path):
    writer = TurnMetricsWriter(tmp_path, "callD")

    class CustomMetric:
        def __init__(self):
            self.speech_id = "s2"

    writer.record(CustomMetric())
    line = (tmp_path / "callD.jsonl").read_text(encoding="utf-8").strip()
    obj = json.loads(line)
    assert obj["type"] == "CustomMetric"


def test_writer_serialises_non_json_values_via_str(tmp_path):
    writer = TurnMetricsWriter(tmp_path, "callE")

    class WithTimestamp:
        def __init__(self):
            self.type = "vad_metrics"
            self.speech_id = None
            # Anything that needs json's `default=str` fallback.
            self.timestamp = object()

    writer.record(WithTimestamp())
    line = (tmp_path / "callE.jsonl").read_text(encoding="utf-8").strip()
    obj = json.loads(line)
    # The non-serialisable object got stringified rather than crashing.
    assert isinstance(obj["data"]["timestamp"], str)


def test_writer_is_append_only_across_multiple_record_calls(tmp_path):
    writer = TurnMetricsWriter(tmp_path, "callF")
    writer.record(_FakeMetric(type="llm_metrics", speech_id="s1"))
    # Imagine the process restarted between calls — same writer reused.
    writer.record(_FakeMetric(type="llm_metrics", speech_id="s2"))
    writer.record(_FakeMetric(type="llm_metrics", speech_id="s3"))

    lines = (tmp_path / "callF.jsonl").read_text(encoding="utf-8").strip().splitlines()
    speech_ids = [json.loads(line)["speech_id"] for line in lines]
    assert speech_ids == ["s1", "s2", "s3"]
