# Architecture Decision Log

Short, dated record of decisions that change the v0.1 PRD. Each entry: context → decision → consequences.

---

## ADR-001 — Swap Cartesia Sonic-3 → ElevenLabs Flash v2.5

**Date:** 2026-04-26
**Status:** Accepted

**Context:** PRD §4.2 specified Cartesia Sonic-3 for streaming TTS (75–150 ms TTFB, lowest latency on the market). User has Lenny's Product Pass which includes 1 year free of ElevenLabs Creator plan.

**Decision:** Use ElevenLabs Flash v2.5 (~75 ms TTFB, comparable to Sonic-3) instead of Cartesia. Switch is one plugin import + env var rename.

**Consequences:**
- Saves ~$22–60/month on TTS for the year of free Creator plan.
- Slightly more natural prosody (subjective, ElevenLabs has more voice options).
- If Lenny's Pass year-free expires before we ship to production, swap back to Cartesia is a one-line change.

---

## ADR-002 — Avatar provider: HeyGen LiveAvatar over Beyond Presence

**Date:** 2026-04-26
**Status:** Accepted

**Context:** PRD §2 chose Beyond Presence because at write-time it was "the only avatar vendor whose API accepts a LiveKit room we control." HeyGen has since launched **LiveAvatar** (rebranded from their old Streaming API) which adopts the same BYO-LiveKit pattern. User asked for an A/B test.

**Decision:** Use HeyGen LiveAvatar (LITE mode, BYO ElevenLabs TTS) as v1 default. Keep Beyond Presence wired behind `AVATAR_PROVIDER=bey` env var for re-testing without code changes.

**Evidence:** A/B test in LiveKit playground, 4-question rubric, voice held constant (Eryn).

| Dimension | BP | LiveAvatar |
|---|---|---|
| Q1 — empathy | 2 | 4 |
| Q2 — lip-sync sustained | 5 | 5 |
| Q3 — interruption | 2 | 4 |
| Q4 — idle naturalness | 1 | 2 |
| Subjective preference | | ✓ |

**Consequences:**
- Higher monthly minimum: BP Starter €49 covered 280 min/mo; LiveAvatar Essential needs $100/mo for similar capacity. Currently on LiveAvatar Starter $19/mo for dev (150 min Lite).
- BP trial cancelled before €49 auto-conversion.
- Idle naturalness scored 1–2/5 for both vendors — flagged as a Phase 3 hardening risk.
- Plugin swap is one env var, so the decision is reversible cheaply.

---

## ADR-003 — Hedra Character-3 eliminated

**Date:** 2026-04-26
**Status:** Accepted

**Context:** Initial vendor research surfaced Hedra Character-3 as a cheap real-time avatar option ($0.05/min flat → ~$14/month for our envelope). Official `livekit-plugins-hedra` existed.

**Decision:** Do not use. Hedra **sunset their Realtime Avatar product on 2026-04-15** (11 days before this decision); the LiveKit plugin page now displays "This plugin no longer functions."

**Consequences:** Removes the cheapest credible alternative. LiveAvatar at $19–100/mo and Beyond Presence at €49/mo remain the photoreal real-time options.

---

## ADR-004 — Enable preemptive generation on AgentSession

**Date:** 2026-04-26
**Status:** Accepted

**Context:** Empirical 2026-04-26 playground testing measured cache-warm turn time at ~2.0 s end-to-end (EoU → first syllable). PRD §2 budget was 1.1–1.4 s.

**Decision:** Set `AgentSession(preemptive_generation=True)`. The LLM call starts before the turn-detector confirms end-of-utterance; if the user resumes speaking, the in-flight call is cancelled cheaply.

**Consequences:**
- Expected ~400–600 ms reduction in cold-cache turn time.
- Slight increase in cancelled LLM calls (cheap; most don't progress past first token before cancel).
- Pinned via `test_preemptive_generation_enabled` so it doesn't silently regress.

---

## ADR-005 — Repo location: C:\code\LiveOnTeams (override of PRD §0)

**Date:** 2026-04-26
**Status:** Accepted

**Context:** PRD §0 specified `C:\src\teams-avatar-agent`. User placed the PRD at `C:\code\LiveOnTeams` and approved building there.

**Decision:** Repo lives at `C:\code\LiveOnTeams`. PRD path reference is documentation-only.

**Consequences:** No functional impact. Memory note exists so future sessions don't try to migrate.

---

## ADR-006 — TLS via Let's Encrypt + Cloudflare DNS-01 (not win-acme HTTP-01)

**Date:** 2026-04-26
**Status:** Accepted

**Context:** PRD §3.5 named `scripts/install-win-acme.ps1` as the cert tool. Win-acme defaults to HTTP-01 challenge which requires port 80 inbound.

**Decision:** Use **Posh-ACME** with Cloudflare DNS-01 challenge instead. No port 80 needed. Cert auto-renews from a token-scoped (`Zone:DNS:Edit`) Cloudflare API token.

**Consequences:**
- One fewer firewall hole.
- Renewal works even if port 80 is blocked upstream.
- Adds `CLOUDFLARE_API_TOKEN` to the secrets list.
- Cert valid through 2026-07-25; renewal task to be scheduled in Phase 3.

---

## ADR-007 — Tenant strategy: Microsoft 365 E3 trial *(superseded ADR-007a)*

**Date:** 2026-04-26
**Status:** Superseded by ADR-007a

**Context:** User has M365 Family. Graph Communications calling APIs are not provisioned for consumer/Family plans.

**Decision (original):** Use the free **Microsoft 365 Developer Program** (E5 sandbox tenant, 25 licenses, admin) for v1 dev.

**Why superseded:** During signup we discovered the M365 Developer Program changed eligibility in 2024 — now requires an active Visual Studio Pro/Enterprise subscription (~$1199/yr) or Microsoft Partner Network membership. Free signup is no longer available to most developers.

---

## ADR-007a — Tenant: Microsoft 365 E3 trial (revised)

**Date:** 2026-04-27
**Status:** Accepted

**Context:** With M365 Developer Program closed (ADR-007 superseded), we evaluated:
- M365 Business Basic/Standard/Premium → all return "Error 7504: Insufficient enterprise tenant permissions" on `Calls.AccessMedia.All`. Confirmed unsupported.
- Office 365 E1 ($10/mo) → no authoritative Microsoft confirmation; community signals lean negative. Treat as unverified.
- M365 E3 ($36/mo annual or $43.20/mo month-to-month) → **only commercial plan with authoritative Microsoft confirmation** for application-hosted media bots.
- M365 E5 ($57/mo annual) → adds Power BI Pro, Phone System, advanced compliance — none needed for Iris.
- Visual Studio Pro subscription ($1199/yr) → re-enables E5 sandbox; only worth it for the IDE.

**Decision:** **M365 E3 trial** (30 days free, $43.20/mo month-to-month after) on a dedicated dev tenant `contoso.onmicrosoft.com`. 1 user license (the bot is an application, not a user).

**Consequences:**
- $0 for first 30 days, then $43.20/mo recurring (cancellable monthly).
- Dedicated dev tenant; no risk to production `example.com` mail/identity.
- **Anti-fraud caveat:** Microsoft blocks signup attempts from datacenter IPs. The signup itself must be done from a residential/business consumer ISP (i.e., not from the bot host VM).
- **Cloud Communications enablement:** Even on E3, individual tenants sometimes require a Microsoft support ticket to enable the calling APIs. Our `Contoso Ltd` tenant enabled them on first admin-consent click — others may not be so lucky.

---

## ADR-008 — Bot deployment: Kestrel directly on 443 with Posh-ACME cert

**Date:** 2026-04-27
**Status:** Accepted

**Context:** PRD §5 implied an IIS or reverse-proxy fronted bot. Investigated:
- IIS + ARR proxy → adds a hop, more moving parts, harder cert management.
- Cloudflare Tunnel / ngrok → solves NAT/IP exposure but Microsoft Graph won't trust the cert chain through these.
- Kestrel direct on 443 → simpler, fewer moving parts, Posh-ACME-managed Let's Encrypt cert from `certs/iris.pfx`.

**Decision:** Kestrel binds 0.0.0.0:443 directly using `certs/iris.pfx` (issued by Posh-ACME via Cloudflare DNS-01 challenge). Windows Firewall opens TCP 443 inbound. Bot runs from elevated PowerShell to bind the privileged port.

**Consequences:**
- Single-process deployment; minimal moving parts.
- Cert renewal handled by `scripts/install-tls-cert.ps1` (Phase 3 will schedule via Task Scheduler).
- Bot must run as Administrator OR have a URL ACL reservation (`netsh http add urlacl`) to bind 443. v1: Administrator. Phase 3: URL ACL + non-admin service.
- Verified end-to-end on 2026-04-27 — `curl https://bot.example.com/healthz` returns 200 with full config payload from any external client.

---

## ADR-009 — Phase 2.3d encoder: FFmpeg subprocess (option 1)

**Date:** 2026-05-02 (opened) → 2026-05-03 (resolved option 1)
**Status:** Accepted — option 1 (FFmpeg subprocess) shipped this commit. Option 4 (upstream `livekit-server-sdk-dotnet` patch to expose `EncodedFrame`) is the long-term answer; track it as a follow-up.

**Context:** Phase 2.3d needs to forward the LiveAvatar video track from LiveKit into the Teams `IVideoSocket.Send`. PRD §5.4 #5 prefers **pre-encoded H.264 NAL passthrough**: have LiveAvatar publish H.264, intercept the encoded bytes before LiveKit decodes them, repackage as Annex-B and submit to the Teams socket. Zero CPU cost, no quality loss.

After surveying `Livekit.Rtc.Dotnet` 0.1.2:

- The wrapper exposes `VideoStream` which yields decoded `VideoFrame` objects (raw I420/NV12/RGB pixels). There is **no `EncodedFrame` API** on `RemoteVideoTrack` in this version. The only "Codec"/"Encoded" references are in stats and `TrackPublishOptions` (publish-side hint).
- Therefore pre-encoded passthrough is **not possible** with the current wrapper. Either the wrapper grows the API, we drop down to the FFI bindings ourselves, or we accept a decode → re-encode hop.
- A re-encode hop requires a software (or hardware-accelerated) H.264 encoder dep. The C# BCL has none. Options:
  1. **FFmpeg subprocess** — pipe I420 frames into stdin of `ffmpeg -i - -c:v libx264 -tune zerolatency …`, read Annex-B from stdout. FFmpeg is already in the host PATH (preflight). Adds process-management complexity but no extra .NET dep. Latency: ~10–30 ms per frame depending on tune.
  2. **FFmpeg.AutoGen + libavcodec** — managed P/Invoke bindings to libav. In-process; lowest IPC overhead. Brings in unmanaged DLLs we'd need to ship. Bigger learning curve.
  3. **Media Foundation H.264 encoder** (Windows-only, hardware-accelerated via Intel Quick Sync / NVENC). Lowest CPU on capable hardware; Windows-only is fine for this bot. Requires `Vortice.MediaFoundation` or similar wrapper. Most complex setup.
  4. **Bump the wrapper / vendor patch** — file an issue / PR against `pabloFuente/livekit-server-sdk-dotnet` to expose `EncodedFrame`. Slowest but eliminates the encoder dep.

**Decision:** **Option 1 (FFmpeg subprocess)** picked on 2026-05-03. Deciding factors:
- Production target is a VPS without GPU → option 3 (Media Foundation hardware encoder) loses its main pro; software MF is worse than libx264.
- Option 2 (FFmpeg.AutoGen in-process) brings GPL contamination via libx264 unless we pay for a commercial x264 license; out of scope for v1.
- Option 4 (upstream wrapper patch) is the right long-term answer but bets the schedule on someone else's review queue. We can pursue it in parallel and delete option 1 if and when it lands — option 1's surface is ~250 lines, throwing it away is cheap.

**Implementation landed in this commit:**
- `bot/src/TeamsAvatarBot.LiveKitBridge/FfmpegH264Encoder.cs` — one ffmpeg subprocess per call. Args: `-f rawvideo -pix_fmt yuv420p -s WxH -r FPS -i pipe:0 -c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline -level:v 3.1 -bf 0 -g FPS -keyint_min FPS -b:v Nk -maxrate Nk -bufsize Nk -x264-params "aud=1:nal-hrd=cbr:force-cfr=1" -f h264 pipe:1`. Optional `-vf scale=W:H` when LiveKit source resolution differs from negotiated Teams output.
- `bot/src/TeamsAvatarBot.LiveKitBridge/H264AccessUnitParser.cs` — Annex-B parser keyed off AUD NAL units (type 9). Splits ffmpeg's stdout byte stream into one access unit per call to `IVideoSocket.Send`. 5 unit tests pin the boundary detection (3-byte and 4-byte start codes, AUD detection, SPS+PPS+IDR composition, start-code split across chunks).
- `bot/src/TeamsAvatarBot.LiveKitBridge/VideoSendBuffer.cs` — concrete `VideoMediaBuffer` backed by `Marshal.AllocHGlobal`; SDK takes ownership on `Send` and disposes when done.
- `LiveKitBridge.PumpRemoteVideoAsync` — opens `VideoStream.FromTrack(track, format: VideoBufferType.I420)`, lazily spawns the encoder on the first frame (so we know the source dims), drains in parallel via `DrainEncoderToVideoSocketAsync`.
- `LiveKitBridge.OnVideoSendStatusChanged` — picks the highest-resolution H.264 format Teams offers (preferring 1280×720) and pins it via a `volatile` field. The video pump waits for this to be non-null before emitting (Teams throws if `Send` is called before `Active`).
- `CallHandler.SupportedSendVideoFormats` — expanded to include the H264_320x180_* family. The Skype.Bots.Media docs require at least one for fallback interop; without it Teams may never raise `Active`.
- `FfmpegH264EncoderSmokeTests.Encoder_emits_at_least_one_access_unit` — actually spawns ffmpeg, feeds 20 frames of mid-grey I420, asserts a complete access unit comes out starting with an Annex-B start code. Catches silent-break failure modes (ffmpeg compiled without libx264, arg name shifts, parser drift).

**Consequences:**
- Decode→re-encode hop adds ~10–30 ms per frame on top of network latency. Acceptable for v1 demo; will revisit if p95 EoU→avatar-frame budget gets tight.
- One ffmpeg process per call. At our concurrent-meeting target (≤ 5 in v1), memory is ~250–750 MB extra resident. Fine for our VPS sizing.
- Key-frame requests from Teams (`IVideoSocket.RequestKeyFrame`) are **not** acted on in v1 — we rely on the periodic 1-second IDR cadence (`-g FPS -keyint_min FPS`) to satisfy any participant joining mid-call. Stronger response (force IDR on demand) would either need the wrapper patch or a libav restart per request; left for v2.
- License: subprocess execution of GPL FFmpeg does not contaminate the calling process (no static or dynamic linking from our code into libx264). Confirmed via the FSF GPL FAQ on "aggregation" vs "linking."
- ~~Track option 4 (upstream `livekit-server-sdk-dotnet#???` PR for `EncodedFrame`) as a follow-up.~~ **Update 2026-05-20:** option 4 is **not viable**. The Rust `livekit-ffi` itself doesn't surface encoded frames, and a closely related upstream request ([livekit/rust-sdks#601](https://github.com/livekit/rust-sdks/issues/601), publish-side encoded H.264) was closed as not planned with no maintainer comment. The .NET wrapper change isn't a small addition — it would need a Rust FFI change first that LiveKit appears to be intentionally declining. Full research in [`docs/proposals/upstream-encoded-frame.md`](proposals/upstream-encoded-frame.md). Option 1 (FFmpeg subprocess) is therefore the long-term answer, not an interim one.

