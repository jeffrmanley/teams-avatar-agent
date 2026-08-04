# Bridging audio both ways: the easy direction, the choppy direction, and a buffer that lied

*Article 09 of the Iris series. Covers Phase 2.3b + 2.3c — the Teams ↔ LiveKit audio pumps. Backfilled after the fact; the video counterpart is article 10.*

**Date:** 2026-05-22
**Author:** Jeff Manley
**TL;DR:** Audio is the "easy" half of the media bridge — both ends speak PCM, no transcoding, no NAL parsing. But "easy" hid three things that each cost real time: the bot's published track has to be tagged `SourceMicrophone` or the agent silently never hears it; the LiveKit→Teams direction has to be paced to wall-clock or it plays back choppy; and the Teams→LiveKit direction had a shared-buffer data race that worked for weeks purely by luck. This is the audio path, end to end, including the bug I only found by reading the code adversarially during the project's closing retrospective.

---

## Two directions, very different difficulty

The bot holds a Teams `IAudioSocket` (bidirectional, 16 kHz mono PCM) and a LiveKit room connection. Audio has to flow both ways:

- **Teams → LiveKit (2.3b):** the human talks; their PCM arrives via `AudioMediaReceived`; we push it into a LiveKit `AudioSource` so the agent's STT can hear it.
- **LiveKit → Teams (2.3c):** the avatar (driven by our TTS) publishes an audio track into the room; we subscribe, pull frames, and push them to `IAudioSocket.Send` so the meeting hears Iris.

Both are "just copy PCM around." Neither needed FFmpeg or codec work. And yet each direction had exactly one non-obvious trap.

## Teams → LiveKit: the track that's published but unheard

The naive version works on the first try and is completely wrong in a way you can't see. You create an `AudioSource`, wrap it in a `LocalAudioTrack`, publish it, and start feeding frames:

```csharp
_audioSource = new AudioSource(SampleRate, Channels);
_audioTrack  = LocalAudioTrack.Create($"teams-audio-{_callId}", _audioSource);
await _room.LocalParticipant.PublishTrackAsync(_audioTrack);   // <-- looks done
```

The track publishes. LiveKit shows it in the room. The bot logs frames flowing in. Everything looks healthy — and the agent's STT receives nothing, so Iris sits there unable to hear anyone.

The reason is a default in LiveKit Agents you'd never guess from the bot side. The agent framework's RoomIO layer, which decides which remote tracks to feed into the STT pipeline, **subscribes only to tracks whose source is `Microphone`**. Our track was published with source `Unknown` (the default), so the agent's input filter dropped it on the floor, silently, with no error on either side. The bot is happily publishing into a void.

The fix is one property:

```csharp
await localParticipant.PublishTrackAsync(_audioTrack, new TrackPublishOptions
{
    Source = TrackSource.SourceMicrophone,
});
```

This took a live test plus a careful read of the *agent's* logs to find — the symptom ("Iris talks but can't hear me") points at the audio bridge, but the cause is a subscribe-side filter one process away. It's written up in more operational detail in article 11 §Layer 4; I mention it here because it belongs to the audio path's permanent record. If your bot publishes audio and the LLM acts like the room is silent, check the track's source tag first.

## Teams → LiveKit: the buffer that lied

Here's the one I'm least proud of, and the reason this article got written when it did. The frame-feeding loop looked like this for the entire life of the project, through every successful test:

```csharp
private readonly short[] _scratch = new short[FrameSamples];   // ONE shared buffer
...
for (var off = 0; off < totalSamples; off += FrameSamples)
{
    Array.Copy(sampleBuffer, off, _scratch, 0, len);
    var frame = new AudioFrame(_scratch, SampleRate, Channels, FrameSamples);
    _ = src.CaptureFrameAsync(frame).ContinueWith(...);          // fire-and-forget
}
```

Teams hands us ~20 ms of PCM per event (320 samples). We slice it into 10 ms chunks (160 samples) and feed each to the LiveKit `AudioSource`. To avoid allocating, I copied each chunk into a single reusable instance field, `_scratch`, made a frame pointing at it, and fired `CaptureFrameAsync` **without awaiting** — fire-and-forget, because blocking the SDK's audio callback thread is its own problem.

The bug: `_scratch` is one buffer, shared across every chunk and every event, and `CaptureFrameAsync` is asynchronous. The LiveKit wrapper marshals that `short[]` to a native buffer on *its* schedule — which can be after the call returns. So the moment the loop comes around and copies the next chunk into `_scratch`, it can overwrite samples that the FFI hasn't read yet. Worse, the Skype media SDK can deliver `AudioMediaReceived` from more than one thread, and they all wrote the same `_scratch` with no lock. Two events interleaving = one event's audio marshaled with the other's bytes.

This is a textbook data race. And it *worked* — through every live test, including the successful 2026-05-20 end-to-end run where the audio was described as "almost perfect." It worked because the race was nearly always won by the writer: the synchronous part of `CaptureFrameAsync` usually marshaled `_scratch` before the next `Array.Copy` landed. "Usually." The audio was occasionally a hair garbled and I'd have attributed it to network jitter. The code was correct by luck, not by construction.

I found it only at the end of the project, doing a deliberate adversarial read with the question "where is this wrong *even though it works*?" The fix is boring and strictly safer — give every frame its own array:

```csharp
for (var off = 0; off < totalSamples; off += FrameSamples)
{
    var len = Math.Min(FrameSamples, totalSamples - off);
    var chunk = new short[FrameSamples];   // per-frame, no sharing
    Array.Copy(sampleBuffer, off, chunk, 0, len);
    var frame = new AudioFrame(chunk, SampleRate, Channels, FrameSamples);
    _ = src.CaptureFrameAsync(frame).ContinueWith(...);
}
```

A fresh 320-byte array per 10 ms frame is ~32 KB/s of gen-0 garbage — nothing — and the race is gone by construction. The short final chunk's tail stays zero (silence) for free, because `new short[]` is zero-initialized, so the explicit `Array.Clear` the old code needed is gone too.

The lesson, stated generally: **a mutable buffer shared across fire-and-forget async calls is a race whenever the callee reads it lazily.** The compiler won't warn you, the tests pass, and the live demo works. The only defense is to not share the buffer, or to await. I shared it to avoid an allocation that turned out to be free.

## The outbound `e.Buffer.Dispose()` that you must not forget

One more Teams→LiveKit detail, the opposite failure mode — a leak instead of a race. The `AudioMediaReceivedEventArgs.Buffer` wraps native memory the SDK owns, and you must dispose it or you leak unmanaged memory for the life of the call:

```csharp
finally
{
    e.Buffer.Dispose();
}
```

This is called out in Microsoft's own sample bots and in the PRD's gotcha list. It goes in a `finally` so it runs whether or not our processing threw. Cheap to get right, expensive to miss — an unmanaged leak on a per-20-ms-frame hot path adds up fast.

## LiveKit → Teams: correct, but choppy

The return direction had no correctness bug — and still sounded bad until paced. The loop subscribes to the avatar's track, pulls 20 ms frames, wraps each in an `AudioSendBuffer`, and sends:

```csharp
stream = AudioStream.FromTrack(track, sampleRate: 16000, numChannels: 1, frameSizeMs: 20);
await foreach (var evt in stream.WithCancellation(ct))
{
    // copy evt.Frame into a 320-sample buffer, wrap, send to IAudioSocket
}
```

Functionally complete. But Iris's first sentence in the first live test was choppy for the whole sentence. The cause: LiveKit doesn't hand you frames at a steady 50 Hz. When the publisher (the avatar) buffers and flushes, you get bursts — five frames in a millisecond, then a gap. If you forward bursts straight through, they slam into Teams' jitter buffer faster than real-time, and the buffer's reordering/dropping logic turns it into garble.

The fix is to pace sends to wall-clock — emit one 20 ms frame per 20 ms of real time, regardless of how bursty the input is:

```csharp
var pacing = Stopwatch.StartNew();
long sent = 0;
// ... per frame, after sending:
sent++;
var targetMs = sent * FrameMs;                       // when frame N *should* go
var aheadMs  = targetMs - pacing.ElapsedMilliseconds;
if (aheadMs > 0) await Task.Delay((int)aheadMs, ct); // ahead → wait; behind → fire now
```

The rule: if we're ahead of the target cadence, sleep until the target; if we're behind, fire immediately and don't try to "catch up" by skipping — let the SDK's jitter buffer absorb the occasional late frame. After this, Iris's voice was smooth. The pacing logic is six lines and it's the difference between "intelligible" and "robot underwater."

## The unmanaged-memory buffers

Both directions of the send path (`AudioSendBuffer` here, `VideoSendBuffer` in article 10) are concrete `Microsoft.Skype.Bots.Media` media-buffer subclasses backed by `Marshal.AllocHGlobal`. The SDK's `Send` takes ownership of the buffer and disposes it when it's done transmitting — *if* the send succeeds. If `Send` throws, ownership never transferred and you must dispose it yourself or leak:

```csharp
try { socket.Send(buf); }
catch (Exception ex) { buf.Dispose(); /* we still own it */ }
```

This split-ownership contract (callee owns on success, caller owns on throw) is easy to get subtly wrong and shows up as a slow unmanaged leak under error conditions. Worth a comment at every call site so the next person doesn't "simplify" the catch.

## What the audio path taught me

The video path (article 10) looked hard and was hard — codecs, NAL parsing, subprocess lifecycle. The audio path looked easy and was easy, which is exactly why its three traps were dangerous: nothing about "copy PCM both ways" signals that you need to tag a track source, pace to a stopwatch, and never share a frame buffer. Easy code earns less scrutiny, and two of these three (the source tag, the pacing) only surfaced under a live call, while the third (the race) didn't surface at all — it just worked until I went looking for the way it was secretly broken.

If there's one transferable takeaway: **the bug that passes every test and works in the demo is the most expensive kind, because nothing prompts you to look for it.** Budget time to read your working code adversarially before you call it done.

— Jeff Manley, May 2026
