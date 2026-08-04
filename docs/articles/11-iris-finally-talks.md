# Iris finally talks: Phase 2.3 end-to-end

*Article 11 of the Iris series. Covers the live e2e validation of Phase 2.3 — Teams ↔ LiveKit bidirectional audio + video.*

**Date:** 2026-05-20
**Author:** Jeff Manley
**TL;DR:** After three months of building the bridge, Iris finally joined a real Teams meeting, introduced herself, and held a back-and-forth conversation. Getting there from "code compiles, tests pass" required peeling five distinct failure layers off the live system in the order Teams chose to reveal them, not the order I would have. The final blocker was a Graph SDK gotcha: `call.OnUpdated` fires for every sub-resource change, not just state transitions, so my naive `if (state == Established) bridge.StartAsync()` was racing itself across concurrent invocations and trampling the LiveKit room. Two `Interlocked.Exchange` latches and one `LocalParticipant` null-check later, conversations work.

---

## The shape of the bug surface

By the time Phase 2.3 a–e was code-complete (article 10 covers the video side), I had this mental model: the C# bot dials a Teams meeting via `/api/join`, Microsoft sends a calling notification back through my webhook, the SDK transitions a call through `Establishing → Established → Terminated`, I answer with a `LocalMediaSession`, audio and video flow through the LiveKit room I just minted, the Python agent dispatches into that room, the HeyGen avatar joins as a participant, the user hears Iris. All deterministic. All in my code.

What I learned across three live e2e attempts is that real Teams calling exposes five layers of failure that no unit test or local spike will surface. They have to be peeled in order because each one masks the next.

| Layer | Symptom | Root cause | Fix |
|---|---|---|---|
| 1 | URL parser threw on a "Teams short link" (`/meet/<id>?p=<passcode>`) | I only built the legacy `/l/meetup-join/` regex | Extended `TeamsMeetingJoinUrl.Parse` to handle both formats; for short URLs use the lower-level `_client.Calls().AddAsync(Call, IMediaSession)` overload with `JoinMeetingIdMeetingInfo` |
| 2 | Graph returns `500 / 1203002`, call goes `Establishing → Terminated` in 17s | Tenant's `CsApplicationAccessPolicy` doesn't authorize my bot's AAD app id to join meetings | `New-CsApplicationAccessPolicy -AppIds <bot-app-id> -Identity Iris-MeetingJoin` then `Grant-CsApplicationAccessPolicy -PolicyName Iris-MeetingJoin -Global` |
| 3 | Same 500/1203002 even after policy is applied | Suspected firewall to Microsoft's media relays on TCP 8445 (turned out to be a red herring — the policy needs ~5 min to propagate) | Wait 5 min after `Grant-CsApplicationAccessPolicy` |
| 4 | Iris joins the call, intros herself, but can't hear me | Bot publishes its audio track to LiveKit as `SourceUnknown` — LiveKit Agents' RoomIO filter only subscribes to `SourceMicrophone` tracks, so the STT pipeline silently ignores it | Set `TrackPublishOptions.Source = TrackSource.SourceMicrophone` on publish |
| 5 | Black tile in Teams, no audio, even after fix #4 | I had just shipped JWT validation pointing at the wrong OIDC endpoint; every Microsoft notification was 401'd, so the SDK never delivered `state=Established` to my handler | Gated the JWT validator behind `BOT_VALIDATE_INBOUND_JWT=true` (default off until I find the right URL) |

Each of those felt like the answer at the time, and each just unlocked the next. After all five were fixed I expected the next live test to work. It didn't — for a sixth reason that turned out to be the actual hardest one.

## Layer 6 — the race that ate Phase 2.3

On the next attempt, Iris joined and started talking immediately. We had a real conversation for 30-40 seconds, then she went deaf. The audio one-way (LiveKit → Teams) kept flowing — Iris would have happily kept introducing herself forever — but the inbound side (Teams → LiveKit) silently stopped reaching her STT.

The bot log was screaming. Forty thousand of these in two minutes:

```
[11:12:04 WRN] LK CaptureFrameAsync failed (#1)
System.AggregateException: One or more errors occurred.
 (Audio capture failed: an RtcError occurred: InvalidState - failed to capture frame)
   at LiveKit.Rtc.AudioSource.CaptureFrameAsync(AudioFrame frame, ...)
```

`InvalidState` from LiveKit means the FFI handle backing the `AudioSource` is no longer live — the source was either disposed or the room it was published to got disconnected. Both. Going back through the logs from the start of the call:

```
[11:12:03 INF] bridge starting room=meeting-...
[11:12:03 INF] LK track subscribed sid=TR_xxx kind=KindAudio from=liveavatar-avatar-agent
[11:12:03 ERR] bridge start failed for call ...
   System.NullReferenceException: Object reference not set to an instance of an object.
   at TeamsAvatarBot.LiveKitBridge.LiveKitBridge.StartAsync() in LiveKitBridge.cs:line 147
[11:12:03 INF] LK room disconnected
[11:12:03 INF] bridge starting room=meeting-...     <-- AGAIN
[11:12:03 INF] LK track subscribed sid=TR_xxx kind=KindAudio from=liveavatar-avatar-agent
[11:12:03 INF] bridge started
[11:12:03 INF] LK room disconnected                 <-- AGAIN
```

Two `bridge starting` entries inside the same wall-clock second. Two `LK room disconnected` entries. One NRE in between. The bridge was being started twice, in parallel, and the two instances were tearing each other's `_room` field out from under each other.

Line 147 in `LiveKitBridge.cs` is the publish call:

```csharp
await _room.LocalParticipant!.PublishTrackAsync(_audioTrack, new TrackPublishOptions
{
    Source = TrackSource.SourceMicrophone,
});
```

The `!` is a null-forgiving compile-time hint — it has no runtime effect. If `_room.LocalParticipant` is null at runtime, you still get an NRE; you just don't get a compiler warning telling you it might happen. And in a race condition, the bang is actively misleading: it tells you "I've thought about this, it's fine," and it's not.

Here's what was happening, frame by frame:

1. StartAsync #1 enters. Sets `_room = new LkRoom()`. Calls `await _room.ConnectAsync(...)`. Returns.
2. SA#1 continues — `_audioSource = new AudioSource(...)`, `_audioTrack = LocalAudioTrack.Create(...)`.
3. While SA#1 is still running, **StartAsync #2 enters**. Sets `_room = new LkRoom()` — a *brand new* `LkRoom` instance, replacing SA#1's. `LocalParticipant` on a fresh-not-yet-connected room is null.
4. SA#1 reaches line 147. Reads `_room.LocalParticipant`. Gets the field's *current* value — which is now SA#2's brand-new room with `LocalParticipant = null`. NRE.
5. SA#2 continues independently, ConnectAsync succeeds, its `LocalParticipant` populates, its track publishes. SA#2's `_audioSource` lands in the field.
6. The exception from SA#1 is caught in `CallHandler.WireBridge`. The default reaction in the surrounding event handler chain disposes the LiveKit FFI client — which severs SA#2's connection too. `LK room disconnected` x2.
7. SA#2's `_audioSource` is still assigned to the field but its underlying FFI handle is dead. Every subsequent `CaptureFrameAsync` returns `InvalidState`. Forever.

The user-visible symptom — Iris stops hearing me 30 seconds into the conversation — is just the time it takes for the in-flight FIFO of buffered audio frames to drain. The bug fired at second zero.

## Why was StartAsync running twice

Here's the inner Graph-SDK gotcha. `call.OnUpdated` doesn't only fire on state transitions. It fires on **every change to any sub-resource of the call** — participants joining, modalities updating, media capabilities renegotiating. All of those are routine and all of them fire `OnUpdated` while the call's state is still `Established`.

My handler was:

```csharp
call.OnUpdated += async (_, _) =>
{
    var state = call.Resource.State;
    // ... log ...
    if (state == Microsoft.Graph.Models.CallState.Established)
    {
        try { await bridge.StartAsync(); }
        catch (Exception ex) { _log.LogError(ex, "bridge start failed"); }
    }
    // ... Terminated branch ...
};
```

That reads as "start the bridge when the call hits Established." What it actually does is "start the bridge **every time** the call's resource updates *while* it is in the Established state." Three participants joining ≈ three StartAsync invocations ≈ three concurrent attempts to reinitialize `_room`.

I had assumed the Graph SDK fires `OnUpdated` only on state changes because that's what the *name* of the event implies in most frameworks. In this SDK, "updated" means "any property of the resource changed." That's defensible API design — there's only one event to subscribe to — but it lays a clean tripwire for the "if state is X, do thing" pattern that every Microsoft sample uses.

## The fix

Two idempotency latches. Both are cheap and one would have sufficed; the second is belt-and-braces because the first didn't exist when I shipped this and someone else might delete it.

In `CallHandler.WireBridge`, latch per call:

```csharp
var startLatch   = 0;
var disposeLatch = 0;

call.OnUpdated += async (_, _) =>
{
    var state = call.Resource.State;
    // ... log ...

    if (state == Microsoft.Graph.Models.CallState.Established)
    {
        if (Interlocked.Exchange(ref startLatch, 1) != 0) return;
        try { await bridge.StartAsync(); }
        catch (Exception ex) { _log.LogError(ex, "bridge start failed"); }
    }
    else if (state == Microsoft.Graph.Models.CallState.Terminated)
    {
        if (Interlocked.Exchange(ref disposeLatch, 1) != 0) return;
        try { await bridge.DisposeAsync(); }
        catch (Exception ex) { _log.LogError(ex, "bridge dispose failed"); }
    }
};
```

In `LiveKitBridge.StartAsync` itself, latch per bridge instance:

```csharp
public async Task StartAsync()
{
    if (Interlocked.Exchange(ref _startInvoked, 1) != 0)
    {
        _log.LogDebug("bridge StartAsync ignored — already invoked");
        return;
    }
    // ... rest of the method ...
}
```

And while I was in there, the bang-suppression on `LocalParticipant` got replaced with an honest null-check that throws with context:

```csharp
var localParticipant = _room.LocalParticipant
    ?? throw new InvalidOperationException(
        $"LiveKit ConnectAsync returned but LocalParticipant is null (call {_callId})");
await localParticipant.PublishTrackAsync(_audioTrack, new TrackPublishOptions
{
    Source = TrackSource.SourceMicrophone,
});
```

If this fails post-fix, I'll see *why* in the log instead of a context-free `NullReferenceException` that could have been any of a hundred fields.

## What I'd skip if I did it again

- **The JWT validator detour.** I wrote it (with 10 unit tests pinning the security contract) before I'd checked which OIDC endpoint URL Microsoft uses for Graph Communications notifications. The URL I copy-pasted from a Bot Framework sample was the wrong one. Every notification got 401'd. The bot was a ghost in the meeting. I lost a full live-test cycle to that before realizing it. The validator is now gated behind `BOT_VALIDATE_INBOUND_JWT=true`, off by default, and I'll re-enable it when I have ground truth on the right URL from actual incoming notification headers.

- **Building the manifest before the e2e worked.** Phase 2.4 (the Teams app sideload zip) is genuinely independent work from Phase 2.3, but doing it in parallel created a temptation to test via the sideload path before the programmatic `/api/join` path was solid. The sideload adds a Teams Admin Center round-trip *and* a global address list propagation delay that mask whatever bug you're chasing. `/api/join` from a curl is the right tool for media-plane debugging; sideload is the right tool for UX testing.

- **Trusting bang-suppression on async-touched fields.** Anywhere a field can be reassigned by a parallel code path (event handler, fire-and-forget Task, second invocation of the same method), `field!.Member` is just a lie wearing a compile-time hat. Capture into a local first, or null-check explicitly. The C# language gives you the rope; it doesn't tell you which knots will hold.

## What the live test actually looked like at the end

After `fccf405` landed, the third live e2e attempt produced this in the bot log:

```
[11:12:03 INF] call <id> updated state=Established ...
[11:12:03 INF] bridge starting room=meeting-<id> call=<id>
[11:12:03 INF] LK track subscribed sid=TR_AMAGygm4jbcrmS kind=KindAudio from=liveavatar-avatar-agent
[11:12:03 INF] LK track subscribed sid=TR_VCpzzFDbVpt53E kind=KindVideo from=liveavatar-avatar-agent
[11:12:03 INF] bridge started room=meeting-<id>
[11:12:04 INF] VideoSendStatusChanged status=Active preferredEnc=640x360@30
[11:12:04 INF] negotiated send format = 640x360@30
[11:12:04 INF] ffmpeg started pid=2476 in=1280x720 out=640x360@30 bitrate=600kbps
[11:12:04 INF] Teams→LK audio frame#50 bytes=640 silent=False captureFails=0
[11:12:05 INF] LK→Teams audio frame#50
[11:12:06 INF] Teams→LK audio frame#100 bytes=640 silent=False captureFails=0
[11:12:07 INF] LK→Teams audio frame#100
... etc, captureFails stays at 0 for the entire meeting ...
```

One `bridge starting` line. One `bridge started` line. Zero `LK CaptureFrameAsync failed` entries. Bidirectional audio frames climbing forever. In Teams, Iris's video tile showed up after a 2-3 second handshake, she introduced herself smoothly, and held a back-and-forth with about 1-1.5 seconds of round-trip lag.

That lag is the natural cost of the stack — Teams ingress jitter buffer (~50ms) + bridge hop + LK SFU + Deepgram STT endpointing (~300ms) + GPT-4o-mini TTFB (~600ms) + Cartesia TTS first-chunk (~250ms) + return path + Teams' own receive-side jitter buffer. Phase 3 metrics work is going to make each of those a number on a dashboard, and that's the next thing to tune. But the bridge is no longer the bottleneck. The bridge works.

— Jeff Manley, May 2026
