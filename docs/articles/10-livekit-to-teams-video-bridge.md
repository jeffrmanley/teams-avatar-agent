# Bridging LiveKit video into Teams: an FFmpeg subprocess and a NAL parser

*Article 10 of the Iris series. Covers Phase 2.3d of the project — the LiveKit → Teams video pump.*

**Date:** 2026-05-03
**Author:** Jeff Manley
**TL;DR:** LiveAvatar publishes an H.264 video track into our LiveKit room. Teams expects H.264 NAL units delivered to `IVideoSocket.Send`. Logically we should be able to passthrough the encoded bytes without touching the encoder at all. In practice the .NET LiveKit RTC wrapper at v0.1.2 doesn't expose encoded frames — it decodes everything to I420 — so we lose the encoder bypass and have to re-encode. We picked option 1 from ADR-009: spawn one `ffmpeg` subprocess per call, libx264 ultrafast/zerolatency, scrape NAL units off stdout, ship them to the Teams socket. About 250 lines of C# and three real surprises along the way.

---

## The two-line problem statement

> The avatar publishes H.264 video into a LiveKit room. The Teams meeting bot needs to send H.264 video into a Teams meeting. Forward the bytes.

Both endpoints already speak H.264. There is, in principle, no reason for the bot to touch the pixels. The whole reason we picked LiveKit as the seam (article 02) was to avoid being on the encode path. PRD §5.4 #5 makes this explicit:

> Pre-encoded H.264 NAL passthrough preferred. Do not transcode if avoidable.

In practice we ended up transcoding. Here's how that happened and what it cost.

## Where the encoder bypass falls apart

`Livekit.Rtc.Dotnet` 0.1.2 is the .NET wrapper around LiveKit's Rust FFI. It exposes `RemoteAudioTrack` and `RemoteVideoTrack`. For audio you can read frames at any sample rate / channel count / frame size you want, because LiveKit decodes Opus internally and the wrapper hands you decoded `AudioFrame`s. That's perfectly fine for our case — we want PCM at 16 kHz mono anyway to feed Teams.

For video, the same wrapper exposes `VideoStream.FromTrack(track, format: VideoBufferType.I420, capacity: int)`. Note the `format` parameter: it's the **output** format you want LiveKit to give you. The values are I420 / NV12 / RGBA / etc. — all decoded pixel formats. There is no `VideoBufferType.EncodedH264` and no separate encoded-frame API on `RemoteVideoTrack`.

I went looking for one. Things I checked:

- `RemoteVideoTrack` has no `OnEncodedFrame` event, no `EncodedStream` static, no `EncodedReader` property. Just `Sid`, `Kind`, and `Mute`/`Unmute`.
- `TrackPublication.VideoCodec` exists but it's a publish-side hint, not a subscribe-side reader.
- Searching the wrapper's XML doc for `Encoded`, `Codec`, `Annex`, `RtpPacket` returns hits only in the protobuf stats types — nothing exposed at the public surface.
- The Python `livekit-rtc` library has the same shape; the underlying `livekit-ffi` Rust crate does have access to encoded frames at the FFI layer, but neither the Python nor the .NET wrapper surfaces them.

So either you **decode and re-encode** (option 1, 2, or 3 from ADR-009) or you **patch the wrapper** (option 4). The wrapper patch is unambiguously the right long-term move. It's also bottlenecked on someone else's review queue.

We're on a VPS without a GPU, which kills option 3 (Media Foundation hardware encoder loses its main pro on a soft-encoder fallback). FFmpeg.AutoGen in-process (option 2) brings GPL contamination via libx264 unless we license x264 commercially, which is ~$1500/year and out of scope. That leaves option 1: subprocess.

## Option 1's structure

Three files, roughly 250 lines total:

| File | Job |
|---|---|
| `FfmpegH264Encoder.cs` | Spawns `ffmpeg`, owns its lifecycle, writes I420 to stdin, parses access units off stdout, drains stderr |
| `H264AccessUnitParser.cs` | Annex-B byte stream → access units |
| `VideoSendBuffer.cs` | Concrete `Microsoft.Skype.Bots.Media.VideoMediaBuffer` with unmanaged-memory backing |

Plus a video-pump method on `LiveKitBridge` that wires those three together with the existing `IVideoSocket` from the media session.

The conceptual flow per call:

```
RemoteVideoTrack ──▶ VideoStream(I420) ──▶ ffmpeg stdin
                                              │
                                              ▼
                                          libx264 (ultrafast, zerolatency)
                                              │
                                              ▼
                                         ffmpeg stdout (Annex-B H.264)
                                              │
                                              ▼
                                         AccessUnitParser
                                              │
                                              ▼
                                         IVideoSocket.Send
```

Easy diagram. Several small things that bit me along the way.

## Surprise 1: Teams won't go Active without H264_320x180

The first thing I tried after wiring everything up was a smoke test through the e2e path. The encoder spawned. LiveKit subscribed to the avatar track. Frames started arriving. The encoder produced NAL units. **None of them ever reached Teams.** The `OnVideoSendStatusChanged` event never fired with `MediaSendStatus.Active`, so my pump kept waiting for the negotiated format and silently buffered frames forever.

Pulling on this thread: `VideoSocketSettings.SupportedSendVideoFormats` controls what formats Teams will negotiate to. In Phase 2.3a I'd configured it as:

```csharp
private static readonly VideoFormat[] SupportedSendVideoFormats =
[
    VideoFormat.H264_1280x720_30Fps,
];
```

That seemed reasonable. We want to send 720p; declare 720p; let Teams decide. Except — buried in the Skype.Bots.Media docs:

> At least one of the `H264_320x180` video formats must be supported. For best video interoperability, the bot should support all the `H264_320x180` formats.

Without a 320×180 format in the list, Teams's negotiation logic apparently never gets to "Active." It's a hard gate. The fix is one PR with a longer enum literal:

```csharp
private static readonly VideoFormat[] SupportedSendVideoFormats =
[
    VideoFormat.H264_1280x720_30Fps,
    VideoFormat.H264_640x360_30Fps,
    VideoFormat.H264_640x360_15Fps,
    VideoFormat.H264_320x180_15Fps,
    VideoFormat.H264_320x180_7_5Fps,
    VideoFormat.H264_320x180_3_75Fps,
    VideoFormat.H264_320x180_1_875Fps,
];
```

`OnVideoSendStatusChanged` then fires within a couple of seconds of call establishment with `Active` and a `PreferredEncodedVideoSourceFormats` list. We pin the largest H.264 format Teams offers and start sending. If Teams renegotiates mid-call (it can), the same handler fires again with a new preferred list and we adopt it.

**Lesson:** when an SDK doc says "must support at least one X for interop," it usually means "the negotiation state machine has a hardcoded check for X." Read those lines twice.

## Surprise 2: finding access-unit boundaries in raw H.264

`IVideoSocket.Send(VideoMediaBuffer)` expects **one access unit per buffer** — that's the H.264 term for "one picture's worth of NAL units." A keyframe is typically AUD + SPS + PPS + IDR slice (four NAL units, one access unit). A predicted frame is usually AUD + slice (two NAL units, one access unit). We get a stream of bytes off ffmpeg stdout. Where do the boundaries go?

The Annex-B format puts a 3- or 4-byte start code (`00 00 01` or `00 00 00 01`) before every NAL unit. NAL unit type lives in the low 5 bits of the byte right after the start code. The standard says: a NAL unit of type 9 (Access Unit Delimiter, AUD) starts a new access unit. So if libx264 emits an AUD before every coded picture, we can split the byte stream by scanning for AUD NAL units.

By default libx264 does **not** emit AUDs. You have to ask:

```
-x264-params "aud=1:nal-hrd=cbr:force-cfr=1"
```

With that flag, every coded picture starts with an AUD. Now the parser is simple state: accumulate bytes; when you find a start code followed by `nal_unit_type == 9`, everything before it is one complete access unit. Emit it. Reset.

The implementation is in `H264AccessUnitParser.cs`. The full method is short enough to inline:

```csharp
public IEnumerable<byte[]> Feed(ReadOnlySpan<byte> bytes)
{
    var results = new List<byte[]>();
    var startOfAccumulated = _accumulated.Count;
    var rescanFrom = Math.Max(0, startOfAccumulated - 3);
    foreach (var b in bytes) _accumulated.Add(b);

    var i = rescanFrom;
    while (i < _accumulated.Count - 4)
    {
        if (!IsStartCode(i, out var headerOffset)) { i++; continue; }
        var nalType = (byte)(_accumulated[headerOffset] & 0x1F);
        if (nalType == NalTypeAud && i > 0)
        {
            var unit = new byte[i];
            _accumulated.CopyTo(0, unit, 0, i);
            results.Add(unit);
            _accumulated.RemoveRange(0, i);
            i = 0;
            continue;
        }
        i = headerOffset + 1;
    }
    return results;
}
```

The `rescanFrom = Math.Max(0, startOfAccumulated - 3)` line is the one you'd miss on first pass. A 4-byte start code can have its first three bytes in one chunk and its last byte in the next. If you only scan from the boundary forward, you miss the start code. Rescanning from three bytes back of the new region catches it.

There are five unit tests in `H264AccessUnitParserTests.cs` pinning this:

- single AU buffered, no boundary visible → emit nothing
- two AUs concatenated → emit the first
- 3-byte start codes recognized as well as 4-byte
- realistic key-frame sequence (AUD + SPS + PPS + IDR slice) emitted as one unit
- start code split across two `Feed()` calls is not lost

The chunk-split test is the one most likely to silently regress under refactor; without it, the parser would mostly work and then drop a keyframe every few seconds.

## Surprise 3: lazy encoder spawn

LiveAvatar (HeyGen LITE mode) publishes at variable resolutions. I don't know the source dims until the first `VideoFrameEvent` arrives. The encoder needs to know the source dims to set `-s WxH` correctly on the input — if you tell ffmpeg the input is 1280×720 and feed it 512×512 raw I420 bytes, it doesn't error; it just produces garbled output that Teams will silently reject.

The fix is to lazily spawn the encoder inside the pump:

```csharp
await foreach (var evt in stream.WithCancellation(ct))
{
    var frame = evt.Frame;
    var negotiated = _negotiatedSendFormat;
    if (negotiated is null) continue;       // Teams hasn't said go yet

    if (encoder is null)
    {
        encoder = new FfmpegH264Encoder(
            inWidth:  frame.Width,           // from first LK frame
            inHeight: frame.Height,
            outWidth: negotiated.Width,      // from Teams negotiation
            outHeight: negotiated.Height,
            frameRate: (int)Math.Round(negotiated.FrameRate),
            bitrateKbps: BitrateKbpsFor(negotiated.Width, negotiated.Height),
            log: _loggerFactory.CreateLogger<FfmpegH264Encoder>());
        sendPump = Task.Run(() => DrainEncoderToVideoSocketAsync(encoder, videoSocket, ct));
    }

    await encoder.WriteFrameAsync(frame.DataBytes, ct);
}
```

Two things to notice. First, the encoder takes both `in*` and `out*` dimensions and decides whether to add a `-vf scale=W:H,setsar=1` filter. If LiveKit gives us 1280×720 and Teams wants 1280×720, no scale. If LiveKit gives us 512×512 and Teams wants 1280×720, we scale in ffmpeg.

Second, the negotiated format is a `volatile VideoFormat?` field on the bridge, written from the Skype.Bots.Media event-handler thread, read from the pump thread. The `volatile` is doing real work — without it, the pump thread can sit on a stale `null` cached in a register forever.

If LiveKit ever changes the source resolution mid-call (it can — simulcast layer switching), we'd currently feed mismatched bytes into the encoder and produce garbage. v1 doesn't handle that; the avatar publishes a single layer so we don't hit it. v2 fix is to teardown and respawn the encoder when `frame.Width / frame.Height` changes.

## A few smaller things

**FFmpeg arg list.** The full command looks like:

```
ffmpeg -hide_banner -loglevel warning
       -f rawvideo -pix_fmt yuv420p -s 1280x720 -r 30 -i pipe:0
       -c:v libx264 -preset ultrafast -tune zerolatency
       -profile:v baseline -level:v 3.1
       -pix_fmt yuv420p
       -bf 0 -g 30 -keyint_min 30
       -b:v 1500k -maxrate 1500k -bufsize 1500k
       -x264-params aud=1:nal-hrd=cbr:force-cfr=1
       -f h264 pipe:1
```

A few of these matter:

- `-tune zerolatency` is the magic flag that turns off look-ahead and most of libx264's quality tricks in favor of latency. Without it the encoder buffers frames before emitting them and you see per-picture latency in the hundreds of ms.
- `-bf 0` disables B-frames. B-frames are encoded out of decode order, which means the decoder needs to wait for future frames before it can show the current one. That's a no-go for real-time.
- `-g 30 -keyint_min 30` forces an IDR (instantaneous decoder refresh — i.e., a true keyframe) every 30 frames, which at 30 fps is one per second. Teams asks for keyframes when participants join; we don't currently respond on demand (more on that below) so the periodic IDR is what saves new joiners from a black screen.
- `-bufsize` equal to `-b:v` means the encoder runs at strict constant bitrate. Most network-friendly under congestion.

**Backpressure via channels.** The encoded-output side uses a bounded `Channel<byte[]>` with `DropOldest` semantics and capacity 16. If the Teams socket can't keep up (packet loss, network slow), we drop old access units rather than block. Better to lose half a second of video and recover at the next IDR than to back up the pump and tank the LiveKit subscribe loop.

The input side **does** block. `WriteFrameAsync` writes directly to `_proc.StandardInput.BaseStream`. If ffmpeg is slow to consume, the write blocks, which slows down the LiveKit subscribe loop, which lets LiveKit backpressure its server. That's correct end-to-end propagation.

**Stderr drain.** Process pipes block if no one reads them. Forgetting to drain stderr is a classic way to make a subprocess hang after ~64 KB of warnings. There's a dedicated `StderrLoopAsync` task per encoder that just `ReadLineAsync`-es and logs everything at debug level. Cheap, reliable, never the cause of a mystery hang.

**Process disposal.** On call end (`DisposeAsync`):

1. Cancel the shutdown token.
2. Close ffmpeg's stdin (signals EOF; ffmpeg flushes and exits cleanly).
3. Wait up to 2 seconds for ffmpeg to exit on its own.
4. If still running, `Kill(entireProcessTree: true)`.
5. Wait for the stdout/stderr pump tasks to drain.

`entireProcessTree: true` matters because libx264 sometimes spawns worker threads that live in the same OS process — but if you ever swap to an FFmpeg build that uses a helper subprocess (some hardware encoders do), the tree-kill catches them. Cheap insurance.

**Key-frame requests.** Teams's `IVideoSocket.RequestKeyFrame` event fires when a participant joins or recovers from packet loss. Ideally we'd respond by forcing libx264 to emit an IDR right now. There's no clean way to do that with ffmpeg's stdin protocol — `-force_key_frames` takes a static expression, not a runtime command. v1 ignores the request and relies on the periodic 1-second IDR. The worst case is one second of black screen for a late joiner. Acceptable for v1; revisit if it shows up in usage feedback.

## The smoke test that catches everything

This pipeline has a ton of failure modes that wouldn't show up in unit tests:

- ffmpeg compiled without libx264 (some distros)
- A flag name shifting between ffmpeg versions
- The AUD x264-param not making it through the args parser
- The NAL parser silently dropping a boundary

So I added one slow integration test:

```csharp
[Fact]
public async Task Encoder_emits_at_least_one_access_unit()
{
    await using var encoder = new FfmpegH264Encoder(
        inWidth: 320, inHeight: 180,
        outWidth: 320, outHeight: 180,
        frameRate: 10, bitrateKbps: 200,
        log: NullLogger<FfmpegH264Encoder>.Instance);

    var frame = new byte[320 * 180 * 3 / 2];
    Array.Fill(frame, (byte)128);  // mid-grey I420

    var feed = Task.Run(async () =>
    {
        for (var i = 0; i < 20; i++)
            await encoder.WriteFrameAsync(frame, default);
    });

    var collected = new List<byte[]>();
    var collect = Task.Run(async () =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var au in encoder.EncodedReader.ReadAllAsync(cts.Token))
        {
            collected.Add(au);
            if (collected.Count >= 1) break;
        }
    });

    await Task.WhenAll(feed, collect);
    Assert.NotEmpty(collected);
    // Must start with an Annex-B start code.
    var first = collected[0];
    Assert.True(
        (first[0] == 0 && first[1] == 0 && first[2] == 0 && first[3] == 1)
        || (first[0] == 0 && first[1] == 0 && first[2] == 1));
}
```

It actually spawns ffmpeg, encodes 2 seconds of mid-grey at 10 fps, and asserts that at least one valid Annex-B access unit comes back. Total runtime: about 1 second. If anything in the pipeline silently breaks, this test goes red. It already caught one issue during development — I had `aud=1` inside the `-x264-params` quote markers when running ad hoc, and PowerShell's command-line parsing was eating the quotes, so AUDs weren't being emitted and the parser never saw a boundary. Test failed; I caught it before pushing.

## What I'd skip if I did it again

**Trying to parse without AUDs.** Before realizing libx264 has the `aud=1` x264-param, I spent an hour considering parsing slice headers to detect access-unit boundaries (slices where `first_mb_in_slice == 0` start a new picture, mostly). That's the spec-compliant way to do it without AUDs but it requires implementing Exp-Golomb decoding for the slice header. AUD is a one-flag shortcut. Always check what your encoder will emit for free before reaching for the spec.

**Negotiating "what video formats does the bot support" cleverly.** I almost wrote a constructor argument that lets each call configure its own format list. There is exactly one production setting (the seven formats we ship) and changing it requires a code change anyway. Keep the static `SupportedSendVideoFormats` array and move on.

**A general-purpose "ffmpeg subprocess wrapper" library.** I considered factoring `FfmpegH264Encoder.cs` into a generic `FfmpegSubprocess<TIn, TOut>` with pluggable codecs. There is exactly one usage and YAGNI applies. The class is named for what it does and only does that.

## Where this goes next

ADR-009 is now Accepted (option 1). The follow-up I want to file as soon as possible is option 4 — an upstream issue (and probably a PR) against `pabloFuente/livekit-server-sdk-dotnet` to expose `EncodedFrame` from `RemoteVideoTrack`. If that lands, the entire `FfmpegH264Encoder` + `H264AccessUnitParser` + scale-filter machinery deletes, replaced by ~30 lines of NAL passthrough. Zero CPU. Zero re-encode artifacts. Matches PRD §5.4 #5 intent exactly.

Until then we have a working video bridge. The next gate is the live end-to-end test against an actual Teams meeting in the dev tenant — that's where the real key-frame timing, send-status churn, and format-renegotiation behavior gets exercised. The unit + smoke tests give me ~70% confidence the pipeline is correct in isolation; the remaining 30% comes from a real call. Article 11 will report on whatever broke.

— *Next: a real Teams meeting and the inevitable list of what only shows up under load.*
