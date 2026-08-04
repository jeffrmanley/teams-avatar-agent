# Upstream encoded-frame access — research finding

**Date:** 2026-05-20
**Status:** **Not pursuing.** Upstream signals strongly suggest the proposal would be declined; the FFmpeg subprocess path is the long-term answer for now, not an interim one.

## Context

Phase 2.3d ships a per-call FFmpeg subprocess that decodes LiveKit's I420 video back to H.264 for `IVideoSocket.Send` (see `ADR-009`). The decode→re-encode hop costs ~10–30 ms of latency and ~30% CPU per call. If `Livekit.Rtc.Dotnet` exposed the encoded H.264 NAL units the LiveAvatar publisher already sends, we could passthrough with zero CPU and zero re-encode artifacts — exactly what PRD §5.4 #5 intends.

When ADR-009 was written, this looked like a tractable upstream contribution: file an issue against `pabloFuente/livekit-server-sdk-dotnet`, propose a small API addition like `RemoteVideoTrack.OnEncodedFrame`, and bind to whatever the underlying Rust FFI already surfaces.

## What the research found

The .NET wrapper is a thin shim over `livekit-ffi` (Rust). Its `LivekitRtc/rust-sdks/` is a pinned submodule of the upstream Rust crate. **The Rust FFI itself does not expose encoded frames** — it only surfaces decoded `VideoBufferType` values (`I420 / I420A / I422 / I444 / I010 / NV12 / RGBA / ABGR / ARGB / BGRA / RGB24`). See `livekit-ffi/protocol/video_frame.proto`:

> [livekit/rust-sdks `video_frame.proto`](https://github.com/livekit/rust-sdks/blob/main/livekit-ffi/protocol/video_frame.proto) — `VideoBufferType` enum, no encoded variants.

The closely related publish-direction request — letting an app *inject* pre-encoded H.264 instead of letting LiveKit's WebRTC stack encode it — was filed upstream and closed:

> [livekit/rust-sdks #601 "Support for Injecting External H264 Encoded Video Stream"](https://github.com/livekit/rust-sdks/issues/601) — opened 2025-03-13, **closed as not planned with no maintainer comment**.

That's not the same direction we'd be asking for (we want the *subscribe* side), but it's a strong signal: LiveKit is intentionally keeping encoded surface area off the FFI. The Python SDK (`livekit-rtc`) confirms — same FFI underneath, no encoded variant. From `livekit-rtc/livekit/rtc/video_stream.py`:

> `VideoStream` exposes only decoded `VideoFrame` objects; stream type is hardcoded to `VIDEO_STREAM_NATIVE` (lines 127, 143).

The .NET wrapper itself has **zero open issues or PRs** related to encoded frames, H.264, NAL units, or passthrough. We'd be the first.

## What it would actually take

Two upstream PRs, in order, neither of which we control:

1. **`livekit/rust-sdks`** — extend `livekit-ffi/protocol/video_frame.proto` with an `EncodedVideoFrame` message + a new stream variant (`VIDEO_STREAM_ENCODED`), plumb the underlying libwebrtc encoded-frame callback through. Given the #601 precedent, this is **likely to be declined**.
2. **`pabloFuente/livekit-server-sdk-dotnet`** — bind the new FFI surface to a C# `EncodedVideoStream` class + `RemoteVideoTrack.OnEncodedFrame` event. Mechanically straightforward (the wrapper currently has 1 open issue, 0 open PRs; maintenance cadence is dominated by Dependabot — feature PRs sit for a while). Conditional on #1 landing first.

If we forked instead, we'd be carrying patches against both Rust and C# — much higher maintenance burden than one ffmpeg subprocess.

## Code locations (for whoever revisits this)

If the upstream stance changes, here's where the work goes:

- **Rust FFI:** `livekit-ffi/protocol/video_frame.proto` (new message), `livekit-ffi/src/server/video_stream.rs` (plumb encoded callback). Submodule pin in the .NET wrapper is at commit `20e442e`.
- **C# wrapper:** [`LivekitRtc/VideoStream.cs`](https://github.com/pabloFuente/livekit-server-sdk-dotnet/blob/main/LivekitRtc/VideoStream.cs) (`FromTrack` at lines 227–235, `FromParticipant` at 214–222) is where a new `EncodedVideoStream.FromTrack` would slot in. [`LivekitRtc/Track.cs`](https://github.com/pabloFuente/livekit-server-sdk-dotnet/blob/main/LivekitRtc/Track.cs) `RemoteVideoTrack` is at line 225 — an `OnEncodedFrame` event would attach there.

## Decision

**Keep ADR-009 option 1 (FFmpeg subprocess) as the production path.** Stop framing it as an interim solution. The encoder is already shipped, tested, and works; the ~30% CPU per call is a real but acceptable cost. If LiveKit changes their stance and lands FFI encoded-frame support, this document is the starting point for a 1-day port — until then, no further upstream advocacy is worth the calendar time.

## Sources

- [livekit/rust-sdks#601 — Support for Injecting External H264 Encoded Video Stream (closed)](https://github.com/livekit/rust-sdks/issues/601)
- [livekit-ffi video_frame.proto](https://github.com/livekit/rust-sdks/blob/main/livekit-ffi/protocol/video_frame.proto)
- [python-sdks video_stream.py](https://github.com/livekit/python-sdks/blob/main/livekit-rtc/livekit/rtc/video_stream.py)
- [pabloFuente/livekit-server-sdk-dotnet LivekitRtc/](https://github.com/pabloFuente/livekit-server-sdk-dotnet/tree/main/LivekitRtc)
- [pabloFuente/livekit-server-sdk-dotnet commit history](https://github.com/pabloFuente/livekit-server-sdk-dotnet/commits/main) (cadence)
