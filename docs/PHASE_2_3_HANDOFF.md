# Phase 2.3 handoff — CLOSED

**Phase 2.3 is complete and validated end-to-end as of 2026-05-20.** Iris joined a real Teams meeting, introduced herself, and held a bidirectional conversation with ~1-1.5s round-trip lag (normal stack latency, not a bug). See `docs/articles/11-iris-finally-talks.md` for the full post-mortem of the live-test debugging arc, including the `call.OnUpdated` race fix in commit `fccf405` that was the final blocker.

**For new sessions, read `docs/PHASE_2_4_NOTES.md` instead** — it has the current state, what's pending, and the Resource Account / Teams licensing learning from the sideload work. This file is preserved as a historical record of the Phase 2.3 push.

---

This is a handoff document for the **Teams Avatar Agent** ("Iris") project. Read it first; it is the cheapest way to load context on where Phase 2.3 landed.

**Last updated: 2026-05-03 after Phase 2.3d landed (audio + video end-to-end). All four sub-phases of 2.3 are now wired; only an actual end-to-end Teams call is left to verify in production. (Note: that e2e test passed on 2026-05-20 — see top of file.)**

---

## Quick context

This is a Microsoft Teams meeting bot with a real-time photoreal AI avatar driven by Claude Sonnet 4.5. Architecture is fully specified in `teams-avatar-agent-PRD.md` (the v0.1 handoff snapshot) and `docs/decisions.md` (the living delta — read both).

**The seam:** A C# .NET 8 Graph Communications media bot bridges Teams ↔ a LiveKit room. A Python LiveKit Agents app and a HeyGen LiveAvatar publisher both join that LiveKit room. Replace either side without touching the other.

**Phase 2.3 is the LiveKit bridge inside the C# bot** — the audio/video frame transport that ferries data between Teams SRTP and the LiveKit room.

---

## Where we are

| Phase | State |
|---|---|
| 0 — preflight + accounts + scaffolding | ✅ |
| 1 — Python LiveKit Agents app | ✅ |
| 2.1 — C# solution scaffold | ✅ |
| 2.2 — Graph SDK wired + CallHandler skeleton | ✅ |
| 2.2.5 — Public deployment + reachability | ✅ `curl https://bot.example.com/healthz` → 200 |
| 2.3 spike — Livekit.Rtc.Dotnet token + connect + audio publish | ✅ commit `0455497` |
| **2.3a — answer calls with LocalMediaSession** | **✅ commit `f1581f1`** |
| **2.3b — Teams → LiveKit audio publish** | **✅ commit `f1581f1`** |
| **2.3c — LiveKit → Teams audio subscribe** | **✅ this commit** |
| **2.3d — LiveKit → Teams video subscribe** | **✅ this commit (FFmpeg subprocess per ADR-009 option 1)** |
| **2.3e — `/api/join` programmatic dispatch** | **✅ this commit** |
| 2.4 — Teams app manifest sideload | **✅ package built; pending upload to Teams Admin Center.** See `teams-app/README.md`. |
| 3 — Hardening (JWT validation, idle behavior, observability, cert renewal scheduling) | Pending |

Latest commit on `main` should be `f1581f1` ("Phase 2.3a + 2.3b: answer calls, publish Teams audio to LiveKit") or later. Run `git log --oneline | head -10` to confirm.

---

## What works *right now* (don't redo)

- Repo at `C:\code\LiveOnTeams` (NOT `C:\src\teams-avatar-agent` despite PRD §0).
- Tooling preflight is green: PS7, .NET 8, Python 3.12, uv, Node 24, Azure CLI, FFmpeg, VS Build Tools w/ C++ workload.
- `.env` populated with all 8 vendor sets + Azure credentials. `azureConfigured=true`.
- Cert at `certs/iris.pfx` valid through 2026-07-25. Daily renewal via Windows Task Scheduler is now wired — register it once with `pwsh -File .\scripts\schedule-cert-renewal.ps1` (elevated). The task runs `install-tls-cert.ps1` daily at 03:00, idempotent (skips when >30 d remain).
- DNS: `bot.example.com` → `203.0.113.10` (Interserver VM), Cloudflare proxy **OFF** (DNS-only). Don't change this.
- M365 E3 trial tenant `Contoso Ltd` / `contoso.onmicrosoft.com` with the 4 Graph application permissions admin-consented.
- Azure Bot resource named `teams-avatar-agent` with messaging endpoint `https://bot.example.com/api/calling` and Teams calling channel enabled.
- Windows Firewall rule `TeamsAvatarBot HTTPS 443` open inbound.
- `dotnet run --project bot/src/TeamsAvatarBot.Host` from elevated PowerShell binds 443 successfully.

**New as of Phase 2.3a + 2.3b (commit `f1581f1`):**

- `bot/src/TeamsAvatarBot.Core/CertificateInstaller.cs`: idempotently installs `certs/iris.pfx` into LocalMachine\My, returns thumbprint.
- `bot/src/TeamsAvatarBot.Core/MediaPlatformSettingsFactory.cs`: builds `MediaPlatformSettings` for Skype.Bots.Media.
- `bot/src/TeamsAvatarBot.LiveKitBridge/CommunicationsService.cs`: now wires `MediaPlatformSettings` on the builder via `Microsoft.Graph.Communications.Calls.Media.SetMediaPlatformSettings`.
- `bot/src/TeamsAvatarBot.LiveKitBridge/CallHandler.cs`: `AttachAsync` now creates a `LocalMediaSession` (Pcm16K audio sendrecv + H264_1280x720_30Fps video sendrecv) and answers the call. On `Established` state it kicks off the `LiveKitBridge`; on `Terminated` it disposes.
- `bot/src/TeamsAvatarBot.LiveKitBridge/LiveKitBridge.cs`:
  - `StartAsync` mints a server token, joins LiveKit room `meeting-{callId}` as identity `bot-{callId}`, publishes a 16 kHz mono `LocalAudioTrack`.
  - `OnTeamsAudioReceived` unmarshals SDK-owned PCM into managed `short[]`, slices to 10 ms / 160-sample chunks, feeds `AudioSource.CaptureFrameAsync`. **Buffer.Dispose() is called in finally** (PRD §5.4-class gotcha).
  - `MintParticipantToken(identity, canPublish, canSubscribe)` ready for use by 2.3e to hand the LiveAvatar publisher a token.
- Spike at `bot/spikes/LiveKitRtcSpike/Program.cs` proves the C# RTC stack works against LiveKit Cloud (success on first run, 500 frames over exactly 5001 ms wall clock). The C# RTC SDK risk flagged in PRD §5.4 #3 is **mitigated**.

**New as of this commit (Phase 2.3c + 2.3e, audio-only end-to-end):**

- The handoff's old `<LangVersion>preview</LangVersion>` blocker was a **misread** — `Livekit.Rtc.Dotnet` 0.1.2 enumerates `AudioFrameEvent` (a regular class), not `RefCount<AudioFrame>` (ref struct). No csproj change needed; net8.0 with default LangVersion compiles `await foreach` cleanly.
- `bot/src/TeamsAvatarBot.LiveKitBridge/AudioSendBuffer.cs`: concrete `AudioMediaBuffer` backed by `Marshal.AllocHGlobal`-owned unmanaged memory; SDK-driven `Dispose(bool)` releases it. Used by the 2.3c pump.
- `bot/src/TeamsAvatarBot.LiveKitBridge/LiveKitBridge.cs`:
  - `OnTrackSubscribed` now switches on `RemoteAudioTrack` / `RemoteVideoTrack`. Audio kicks off `PumpRemoteAudioAsync`; video logs and skips (per ADR-009).
  - `PumpRemoteAudioAsync` opens `AudioStream.FromTrack(track, 16000, 1, frameSizeMs:20)` and `await foreach`-es into `IAudioSocket.Send` via fresh `AudioSendBuffer`s. SDK takes ownership; we dispose only on send-throw.
  - Lifecycle: a `CancellationTokenSource` (`_shutdown`) cancels all pumps; `DisposeAsync` waits up to 2 s for them to drain before disconnecting the LiveKit room.
- `bot/src/TeamsAvatarBot.LiveKitBridge/TeamsMeetingJoinUrl.cs` (new, public): parses a Teams calendar URL into `(ChatInfo, OrganizerMeetingInfo, tenantId)`. Pinned by 5 unit tests in `TeamsMeetingJoinUrlTests.cs`.
- `bot/src/TeamsAvatarBot.LiveKitBridge/CallHandler.cs`: split into `AttachAsync` (inbound, calls `AnswerAsync`) and `AttachToOutboundAsync` (outbound, no answer — call is already in `Establishing` state). Common wiring extracted to `WireBridge`. New public `CreateLocalMediaSession()` (paramless overload) for outbound dispatch.
- `bot/src/TeamsAvatarBot.LiveKitBridge/CommunicationsService.cs`: new `JoinMeetingAsync(meetingUrl, ct)` parses the URL, creates a media session, builds `JoinMeetingParameters`, calls `_client.Calls().AddAsync(joinParams)`, and hands the returned `ICall` to `CallHandler.AttachToOutboundAsync`.
- `bot/src/TeamsAvatarBot.Host/Controllers/CallingController.cs`: `Join(meetingUrl)` now actually dials the meeting. Returns `Accepted({ meetingUrl, callId, room })`. Optional `X-Bot-Secret` header gate driven by `BOT_DISPATCH_SECRET` env var (skipped when env var unset, so dev still works).
- `dotnet build TeamsAvatarBot.sln` → 0 errors. `dotnet test` → **6/6** (1 healthz + 5 URL parser). Python agent tests still **14/14**.

**New as of this commit (Phase 2.3d, video pump via FFmpeg subprocess):**

- `bot/src/TeamsAvatarBot.LiveKitBridge/FfmpegH264Encoder.cs` — per-call subprocess wrapping libx264 (preset ultrafast, tune zerolatency, profile baseline level 3.1, 1-sec GOP, AUD enabled, CBR). Async `WriteFrameAsync(I420)` for input; `EncodedReader` is a `ChannelReader<byte[]>` of complete access units for output. Stderr drained line-by-line into the logger. On dispose, closes stdin and reaps the process (kill after 2 s grace).
- `bot/src/TeamsAvatarBot.LiveKitBridge/H264AccessUnitParser.cs` — Annex-B NAL stream → access-unit splitter. Recognizes both 3-byte and 4-byte start codes; relies on AUD NAL (type 9) as the picture boundary (libx264 emits AUDs because we pass `-x264-params aud=1`).
- `bot/src/TeamsAvatarBot.LiveKitBridge/VideoSendBuffer.cs` — concrete `VideoMediaBuffer` with unmanaged-memory `Marshal.AllocHGlobal` backing, mirroring `AudioSendBuffer`.
- `LiveKitBridge.PumpRemoteVideoAsync` — opens `VideoStream.FromTrack(track, format: VideoBufferType.I420)`. On the first `VideoFrameEvent`, lazily spawns the encoder configured to scale source dims → negotiated Teams output dims. A parallel `DrainEncoderToVideoSocketAsync` task wraps each AU in a `VideoSendBuffer` and ships to `IVideoSocket.Send`.
- `LiveKitBridge.OnVideoSendStatusChanged` — picks the largest H.264 format from Teams's preferred list (preferring 1280×720); pins it in a `volatile` field that gates the video pump.
- `CallHandler.SupportedSendVideoFormats` — expanded to include the `H264_320x180_*` family (Skype.Bots.Media docs require at least one for Teams to negotiate `Active`).
- `bot/tests/TeamsAvatarBot.Tests/H264AccessUnitParserTests.cs` — 5 tests pinning AUD-boundary detection (start-code variants, SPS+PPS+IDR composition, chunk-split start codes).
- `bot/tests/TeamsAvatarBot.Tests/FfmpegH264EncoderSmokeTests.cs` — actually spawns ffmpeg, feeds 20 mid-grey frames at 320×180@10fps, asserts ≥1 access unit comes out starting with an Annex-B start code. Catches the silent-break case (no libx264, arg drift, parser bug).
- `dotnet test` → **12/12** (1 healthz + 5 URL parser + 5 NAL parser + 1 ffmpeg smoke). Build clean. Python agent tests still **14/14**.

**ADR-009 status: Accepted (option 1 — FFmpeg subprocess), and option 1 is now the long-term answer.** Option 4 (upstream wrapper PR) was researched on 2026-05-20 and found not viable — the Rust `livekit-ffi` doesn't surface encoded frames at all, and a closely related upstream request was closed-as-not-planned with no maintainer comment. Details: `docs/proposals/upstream-encoded-frame.md`.

If any of these regress, see `SETUP.md` for the rebuild path.

---

## Phase 2.3 scope

The CallHandler currently logs incoming calls but does not answer them or move any media. By the end of Phase 2.3, the bot must:

1. **Answer incoming calls** with `applicationHostedMedia`, attaching a `LocalMediaSession` that has:
   - one `IAudioSocket` — send + receive, stereo 16 kHz PCM
   - one `IVideoSocket` — send-only, H.264, 1280×720 @ 30 fps. Receive-only sockets for incoming participant video are out of scope (PRD §1 — no screen-share comprehension in v1).
2. **Bridge audio bidirectionally:**
   - **Teams → LiveKit:** `IAudioSocket.AudioMediaReceived` fires with 20 ms PCM frames. Buffer, re-encode to Opus (FFmpeg subprocess via stdin pipe is the simplest path — measurable latency ~10 ms), publish as a LiveKit audio track using the LiveKit C# server SDK.
   - **LiveKit → Teams:** subscribe to LiveAvatar's audio track, decode Opus → 16 kHz mono PCM, feed to `IAudioSocket.Send`.
3. **Bridge video unidirectionally (LiveKit → Teams):** subscribe to LiveAvatar's video track. Avatar publishes H.264 by default; repackage NAL units as Annex-B and submit to `IVideoSocket.Send` as **pre-encoded H.264** (no transcode — see PRD §5.4 #5).
4. **Per-call lifecycle:** create the LiveKit room `meeting-{callId}` on call connect, mint a token for the LiveAvatar publisher and POST it to the Python agent's callback (or push via Redis), tear down the room and end the LiveAvatar session on call disconnect.
5. **Wire the dev join endpoint:** `GET /api/join?meetingUrl=...` should programmatically create a call via `client.Calls().AddAsync(...)` so we can test without sideloading a Teams app manifest.

Stub class is in `bot/src/TeamsAvatarBot.LiveKitBridge/LiveKitBridge.cs` — currently throws `NotImplementedException`. Per-call instance, `IAsyncDisposable`, owns the LiveKit room peer for one call.

---

## Acceptance criteria (PRD §5.5 + our validation)

- Bot answers an incoming-call notification from Graph and the call reaches `Established` state.
- In Teams UI, the bot appears in the participant roster with the app name and a "Bot" tag.
- Audio is bidirectional: a human in Teams hears Iris, Iris's STT receives the human's audio (Deepgram log shows transcripts).
- Video tile is visible with the LiveAvatar avatar (or, if PRD §5.4.3 hits, the project explicitly triages the audio-only fallback in writing in `docs/decisions.md`).
- Call teardown is clean: LiveKit room destroyed, LiveAvatar session ended, no orphaned resources after 10 sequential meetings.
- p95 of *end-of-user-speech → first-bot-audio-frame* < 1600 ms (logged to `./logs/turns/{callId}.jsonl` per the schema in `docs/dataflow.md`).

---

## The hardest piece (revised after this commit)

Audio + video plumbing is **wired in both directions**. The remaining unknown is the live end-to-end test against a real Teams meeting — none of the SDK quirks that show up under actual SRTP load have been exercised yet (key-frame request handling, send-status churn under network loss, format renegotiation mid-call). Phase 2.3 ends when the e2e test in §"How to start the next session" passes.

**~~Long-term follow-up~~ closed-out 2026-05-20:** option 4 from ADR-009 (upstream wrapper PR exposing encoded H.264 from `RemoteVideoTrack`) was researched and found not viable — the Rust FFI doesn't surface encoded frames at all, and the closest upstream request was closed-as-not-planned. `docs/proposals/upstream-encoded-frame.md` has the details and code pointers in case LiveKit's stance ever changes. Option 1 (FFmpeg subprocess) is now the long-term path.

---

## Test approach

**Don't sideload a Teams app manifest yet.** That's Phase 2.4 work and adds a Teams Admin Center round-trip. Instead, programmatically join a meeting via Graph:

1. Manually create a Teams meeting in the dev tenant via the M365 admin's calendar.
2. Copy the meeting join URL from the calendar invite.
3. Hit `GET https://bot.example.com/api/join?meetingUrl=<url>` from a browser or curl.
4. Phase 2.3 wires that endpoint to call `client.Calls().AddAsync(...)` with the meeting info. Iris should join within ~10 seconds.
5. Watch `./logs/turns/{callId}.jsonl` for per-turn metrics; tail the bot stdout for state changes.

For multi-meeting load test (acceptance criterion #5), a small bash loop hitting `/api/join` with N pre-created meeting URLs and watching for `OnUpdated → Terminated` events is sufficient.

---

## Don't redo

These were validated empirically; assume they still hold unless something breaks:

- ElevenLabs `eleven_flash_v2_5` is the right TTS model (~75 ms TTFB). Don't switch back to `eleven_turbo_v2_5` even if the plugin's default suggests it.
- `caching="ephemeral"` on the Anthropic plugin is the supported kwarg (NOT `cache_control={...}`).
- `RoomOutputOptions(audio_enabled=False)` is required because LiveAvatar republishes synced A/V; otherwise participants hear TTS audio twice.
- `preemptive_generation=True` on `AgentSession` — the cold-cache turn 2.1s observation was real; this claws back ~400-600 ms.
- `AVATAR_PROVIDER=liveavatar` is the v1 default. `bey` is wired as a one-env-var fallback.

The pinned config tests in `agent/tests/test_session_config.py` and `test_disclosure.py` (14 tests, all passing) will scream if any of these regress.

---

## References — read these in order

1. **This file.**
2. `teams-avatar-agent-PRD.md` §5 (Phase 2 scope and §5.4 known gotchas — every gotcha there is real and costs days if ignored).
3. `docs/architecture.md` (system diagram).
4. `docs/dataflow.md` (per-turn sequence + per-stage failure handling + metric schema).
5. `docs/decisions.md` (every architectural choice and why; check ADR-008 for the deployment topology).
6. `bot/src/TeamsAvatarBot.Core/CommunicationsService.cs` and `CallHandler.cs` (Phase 2.2 wiring you'll be extending).
7. `bot/src/TeamsAvatarBot.LiveKitBridge/LiveKitBridge.cs` (the stub you'll be filling in).
8. Microsoft sample bot reference: <https://github.com/microsoftgraph/microsoft-graph-comms-samples> — specifically `Samples/V1.0Samples/LocalMediaSamples/AudioVideoPlaybackBot`. Don't copy wholesale; strip what you don't need.
9. `docs/articles/avatar-vendor-ab-test.md` (context on why LiveAvatar is the avatar provider; how to re-test if anyone questions it).

---

## How to start the next session

1. Read this file and `docs/decisions.md` ADR-009.
2. Run `git log --oneline | head -10` to confirm baseline (latest commit on `main` should be the Phase 2.3c/e landing; was previously `f356d1b`).
3. Run the existing tests as a regression check:
   ```powershell
   cd C:\code\LiveOnTeams\agent && uv run pytest -v   # 14/14
   cd C:\code\LiveOnTeams\bot   && dotnet test         # 12/12 (1 healthz + 5 URL parser + 5 NAL parser + 1 ffmpeg smoke)
   ```
4. **Live e2e Teams meeting test** is the gating activity (see §5 below). All wiring is in place — exercise it.
5. **End-to-end test (audio-only is shippable now):**
   - Create a Teams meeting in `contoso.onmicrosoft.com` from your laptop calendar.
   - Set `BOT_DISPATCH_SECRET=<random>` in `.env` (the controller skips the gate when unset, but use it).
   - From elevated PowerShell on the bot host: `dotnet run --project bot/src/TeamsAvatarBot.Host`.
   - From the laptop: `curl -H "X-Bot-Secret: <secret>" "https://bot.example.com/api/join?meetingUrl=<url>"`. Response gives you `{ meetingUrl, callId, room }` — note the `room` value, you need it for the agent.
   - Watch the bot logs for `outbound call created callId=...` → `state=Establishing` → `state=Established` → `bridge started room=meeting-<callId>` → audio frames pumping in both directions.
   - Coordinate the Python agent: `cd agent && AVATAR_PROVIDER=liveavatar uv run python agent.py dev` configured to dispatch into `meeting-<callId>`. See <https://docs.livekit.io/agents/build/dispatch/> for explicit-dispatch API; v1 hack is to set the worker's room regex to `meeting-.*`.
6. **Acceptance criteria (PRD §5.5) status after this commit:**
   - ✅ Bot answers an inbound call OR programmatically dials in via `/api/join`.
   - ⏳ Bot appears in Teams roster (untested — needs an actual call).
   - ✅ Audio bidirectional (compiled + unit-tested; needs e2e verification).
   - ✅ Video tile (compiled + smoke-tested via real ffmpeg; needs e2e verification — first place to debug if Teams shows a black tile is the encoder's stderr in the bot logs and the negotiated send format vs. what the encoder is producing).
   - ⏳ Clean teardown after 10 sequential meetings (untested).
   - ⏳ p95 EoU → first-bot-audio-frame < 1600 ms (no metrics logging yet — Phase 3).

---

## Conventions established in earlier sessions

- Commit messages have a one-line summary + body explaining the why, ending with `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- Heavy refactors land as separate commits from feature work.
- `.env` is in `.gitignore`. **Never commit secrets.**
- Phase changes get an ADR in `docs/decisions.md`.
- Pytests pin behavior that would silently regress if changed (e.g., the Art. 50 disclosure being un-interruptible).
- Costs flagged to user before approval (PRD §9 enumerates stop-and-ask triggers).

If you're unsure on any of these, ask Jeff before acting.

Good luck.
