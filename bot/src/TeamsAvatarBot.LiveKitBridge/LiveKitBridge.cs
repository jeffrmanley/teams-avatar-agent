using System.Buffers;
using System.Runtime.InteropServices;
using LiveKit.Rtc;
using LkRoom = LiveKit.Rtc.Room;
using LkRoomOptions = LiveKit.Rtc.RoomOptions;
using LkAudioFrame = LiveKit.Rtc.AudioFrame;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;
using TeamsAvatarBot.Core;
using SkypeMediaPlatform = Microsoft.Skype.Bots.Media.MediaPlatform;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Per-call bridge between Teams (Skype.Bots.Media sockets) and LiveKit.
///
/// Phase 2.3 status as of this commit:
///   - 2.3a (call answer):       done in CallHandler.AttachAsync
///   - 2.3b (Teams → LK audio):  WIRED — IAudioSocket.AudioMediaReceived
///                               forwards 16 kHz PCM into AudioSource.CaptureFrameAsync
///   - 2.3c (LK → Teams audio):  WIRED — see <see cref="OnTrackSubscribed"/>:
///                               opens an <see cref="AudioStream"/> at 16 kHz mono
///                               with 20 ms framing, pumps each AudioFrame into
///                               an <see cref="AudioSendBuffer"/> and hands it to
///                               <see cref="IAudioSocket.Send"/>. The handoff doc
///                               flagged a C# 13 ref-struct blocker but the actual
///                               wrapper enumerates AudioFrameEvent (regular class),
///                               so net8.0 with default LangVersion compiles fine.
///   - 2.3d (LK → Teams video):  WIRED via <see cref="FfmpegH264Encoder"/> —
///                               see <see cref="PumpRemoteVideoAsync"/>. Decision
///                               recorded in ADR-009: option 1 (FFmpeg subprocess).
///                               LiveKit decodes to I420 → ffmpeg encodes to H.264
///                               Annex-B → access-unit parser → IVideoSocket.Send.
///
/// Lifecycle:
///   - Constructed in <see cref="CallHandler.AttachAsync"/> alongside the
///     incoming call, before answer.
///   - <see cref="StartAsync"/> when call reaches <c>Established</c> state.
///   - <see cref="DisposeAsync"/> on call termination.
/// </summary>
public sealed class LiveKitBridge : IAsyncDisposable
{
    private const int SampleRate     = 16_000;
    private const int Channels       = 1;
    private const int FrameSamples   = 160;             // 10 ms at 16 kHz
    private const int BytesPerSample = 2;               // PCM16

    private readonly BotConfig            _config;
    private readonly string               _callId;
    private readonly string               _roomName;
    private readonly ILocalMediaSession   _mediaSession;
    private readonly ILogger<LiveKitBridge> _log;
    private readonly ILoggerFactory       _loggerFactory;

    private LkRoom?          _room;
    private AudioSource?     _audioSource;
    private LocalAudioTrack? _audioTrack;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task>              _pumps    = [];

    // StartAsync idempotency latch. The Graph SDK fires `call.OnUpdated`
    // every time *any* sub-resource changes (state, participants, modalities,
    // capabilities). CallHandler.WireBridge has its own outer latch so this
    // is belt-and-braces — but concurrent StartAsync calls would race on the
    // `_room` field and leave the AudioSource in InvalidState (the symptom
    // we hit in the 2026-05-02 live test where audio cut out mid-call).
    private int _startInvoked;

    // Diagnostic counters — log every ~1 sec (50 frames at 20ms each) so
    // we can see whether bidirectional audio is actually flowing without
    // drowning the log in per-frame entries.
    private long _teamsAudioReceivedCount;
    private long _teamsAudioCaptureFailures;
    private long _liveKitAudioFramesSent;

    // Set when Teams's IVideoSocket.VideoSendStatusChanged fires with Active.
    // Until then, the video pump buffers frames but doesn't emit (the SDK will
    // throw if Send is called before Active).
    private volatile VideoFormat? _negotiatedSendFormat;

    public LiveKitBridge(
        BotConfig          config,
        string             callId,
        ILocalMediaSession mediaSession,
        ILogger<LiveKitBridge> logger,
        ILoggerFactory     loggerFactory)
    {
        _config        = config;
        _callId        = callId;
        _roomName      = $"meeting-{callId}";
        _mediaSession  = mediaSession;
        _log           = logger;
        _loggerFactory = loggerFactory;
    }

    public string RoomName => _roomName;

    /// <summary>
    /// Mint a token a separate participant (e.g. the LiveAvatar publisher)
    /// can use to join the same room. Phase 2.3e will POST this to the
    /// agent so the avatar joins the right meeting room.
    /// </summary>
    public string MintParticipantToken(string identity, bool canPublish, bool canSubscribe) =>
        new AccessToken(_config.LiveKitApiKey, _config.LiveKitApiSecret)
            .WithIdentity(identity)
            .WithGrants(new VideoGrants
            {
                RoomJoin     = true,
                Room         = _roomName,
                CanPublish   = canPublish,
                CanSubscribe = canSubscribe,
            })
            .WithTtl(TimeSpan.FromHours(2))
            .ToJwt();

    /// <summary>
    /// Connect to LiveKit, publish an audio track for Teams audio, register
    /// for incoming track subscriptions (TODO: pump them to Teams).
    /// </summary>
    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _startInvoked, 1) != 0)
        {
            _log.LogDebug("bridge StartAsync ignored — already invoked for call {CallId}", _callId);
            return;
        }

        _log.LogInformation("bridge starting room={Room} call={CallId}", _roomName, _callId);

        var botToken = new AccessToken(_config.LiveKitApiKey, _config.LiveKitApiSecret)
            .WithIdentity($"bot-{_callId}")
            .WithGrants(new VideoGrants
            {
                RoomJoin     = true,
                Room         = _roomName,
                CanPublish   = true,
                CanSubscribe = true,
            })
            .WithTtl(TimeSpan.FromHours(2))
            .ToJwt();

        _room = new LkRoom();
        _room.TrackSubscribed         += OnTrackSubscribed;
        _room.ParticipantConnected    += (_, p) => _log.LogInformation("LK participant connected {Id}", p.Identity);
        _room.ParticipantDisconnected += (_, p) => _log.LogInformation("LK participant disconnected {Id}", p.Identity);
        _room.Disconnected            += (_, _) => _log.LogInformation("LK room disconnected");

        await _room.ConnectAsync(_config.LiveKitUrl, botToken, new LkRoomOptions { AutoSubscribe = true });

        _audioSource = new AudioSource(SampleRate, Channels);
        _audioTrack  = LocalAudioTrack.Create($"teams-audio-{_callId}", _audioSource);
        var localParticipant = _room.LocalParticipant
            ?? throw new InvalidOperationException(
                $"LiveKit ConnectAsync returned but LocalParticipant is null (call {_callId})");
        // Tag the source as Microphone — LiveKit Agents' RoomIO subscribes
        // only to mic-source tracks by default. Without this the agent's
        // STT would silently ignore our Teams-audio publish (Iris can't
        // hear the human in the meeting).
        await localParticipant.PublishTrackAsync(_audioTrack, new LiveKit.Rtc.TrackPublishOptions
        {
            Source = LiveKit.Proto.TrackSource.SourceMicrophone,
        });

        if (_mediaSession.AudioSocket is { } audioSocket)
            audioSocket.AudioMediaReceived += OnTeamsAudioReceived;
        else
            _log.LogWarning("media session has no audio socket — Teams→LK audio disabled");

        if (TryGetVideoSocket() is { } videoSocket)
        {
            videoSocket.VideoSendStatusChanged += OnVideoSendStatusChanged;
            // RequestKeyFrame fires when a Teams participant joins or recovers
            // from packet loss. v1 relies on the 1-second IDR cadence in
            // FfmpegH264Encoder; we just log the request for now.
            // (No on-demand keyframe injection yet — option-4 wrapper PR or
            // a libav restart per request would solve it.)
        }
        else
        {
            _log.LogWarning("media session has no video socket — LK→Teams video disabled");
        }

        _log.LogInformation("bridge started room={Room}", _roomName);
    }

    private IVideoSocket? TryGetVideoSocket()
    {
        if (_mediaSession.VideoSockets is { Count: > 0 } list) return list[0];
        return null;
    }

    /// <summary>
    /// Teams tells us "you may send video now and here are the encoded
    /// formats I'll accept." We pick the highest-resolution format from the
    /// preferred list that we know how to produce, and pin it for the rest
    /// of the call. If Teams later renegotiates a different list, the
    /// volatile field swap will make the next encoded frame pick up the new
    /// format on its next access-unit boundary.
    /// </summary>
    private void OnVideoSendStatusChanged(object? sender, VideoSendStatusChangedEventArgs e)
    {
        _log.LogInformation(
            "VideoSendStatusChanged status={Status} preferredEnc={Pref}",
            e.MediaSendStatus,
            e.PreferredEncodedVideoSourceFormats is null
                ? "null"
                : string.Join(",", e.PreferredEncodedVideoSourceFormats.Select(f => $"{f.Width}x{f.Height}@{f.FrameRate}")));

        if (e.MediaSendStatus != MediaSendStatus.Active)
        {
            _negotiatedSendFormat = null;
            return;
        }

        // Prefer 1280x720 if Teams will accept it; else take whichever
        // preferred format has the largest area.
        var formats = e.PreferredEncodedVideoSourceFormats?.ToList() ?? new();
        if (formats.Count == 0) { _log.LogWarning("Active with no preferred formats — ignoring"); return; }

        var picked =
            formats.FirstOrDefault(f => f.Width == 1280 && f.Height == 720)
            ?? formats.OrderByDescending(f => f.Width * f.Height).First();

        _negotiatedSendFormat = picked;
        _log.LogInformation("negotiated send format = {W}x{H}@{Fps}", picked.Width, picked.Height, picked.FrameRate);
    }

    /// <summary>
    /// Forward an arrived Teams PCM frame into the LiveKit AudioSource.
    /// Must call <c>e.Buffer.Dispose()</c> at end — releases native memory
    /// owned by the SDK (PRD §5.4-class gotcha; see Microsoft sample
    /// PolicyRecordingBot/BotMediaStream.cs).
    /// </summary>
    private void OnTeamsAudioReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            var src = _audioSource;
            if (src is null) return;

            var n = Interlocked.Increment(ref _teamsAudioReceivedCount);
            if (n == 1)
                _log.LogInformation(
                    "first Teams→LK audio frame: format={Format} bytes={Bytes} silent={Silent}",
                    e.Buffer.AudioFormat, e.Buffer.Length, e.Buffer.IsSilence);
            // ~1-second cadence (20 ms frames × 50 = 1 s)
            else if (n % 50 == 0)
                _log.LogInformation(
                    "Teams→LK audio frame#{N} bytes={Bytes} silent={Silent} captureFails={Fails}",
                    n, e.Buffer.Length, e.Buffer.IsSilence,
                    Interlocked.Read(ref _teamsAudioCaptureFailures));

            // Teams hands us PCM at 16 kHz mono — typically 20 ms = 320
            // samples = 640 bytes per frame. Slice into 10 ms (160 sample)
            // chunks for AudioSource.
            var totalSamples = (int)(e.Buffer.Length / BytesPerSample);
            var sampleBuffer = ArrayPool<short>.Shared.Rent(totalSamples);
            try
            {
                Marshal.Copy(e.Buffer.Data, sampleBuffer, 0, totalSamples);
                for (var off = 0; off < totalSamples; off += FrameSamples)
                {
                    var len = Math.Min(FrameSamples, totalSamples - off);
                    // Each 10 ms frame gets its OWN backing array. CaptureFrameAsync
                    // is fire-and-forget and the LiveKit wrapper marshals the buffer
                    // to native on its own schedule, so a shared/reused array would
                    // race: a later chunk — or a concurrent AudioMediaReceived event
                    // on another SDK thread — could overwrite samples the FFI hasn't
                    // read yet, producing garbled audio. A fresh 320-byte array per
                    // frame is trivial GC churn next to that correctness win. A short
                    // final chunk's tail stays zero (silence) because new short[] is
                    // zero-initialized, so no explicit Array.Clear is needed.
                    var chunk = new short[FrameSamples];
                    Array.Copy(sampleBuffer, off, chunk, 0, len);

                    var frame = new LkAudioFrame(chunk, SampleRate, Channels, FrameSamples);
                    _ = src.CaptureFrameAsync(frame).ContinueWith(t =>
                    {
                        if (t.Exception is not null)
                        {
                            var fails = Interlocked.Increment(ref _teamsAudioCaptureFailures);
                            // Log first failure loudly, then sample.
                            if (fails == 1 || fails % 50 == 0)
                                _log.LogWarning(t.Exception, "LK CaptureFrameAsync failed (#{Fails})", fails);
                        }
                    }, TaskScheduler.Default);
                }
            }
            finally
            {
                ArrayPool<short>.Shared.Return(sampleBuffer);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OnTeamsAudioReceived failed");
        }
        finally
        {
            e.Buffer.Dispose();
        }
    }

    /// <summary>
    /// LiveAvatar publishes audio + video tracks into our room. We pump
    /// remote audio frames into the Teams audio socket here. Remote video
    /// is logged but not yet forwarded — see ADR-009 in docs/decisions.md.
    /// </summary>
    private void OnTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        _log.LogInformation(
            "LK track subscribed sid={Sid} kind={Kind} from={Identity}",
            e.Publication.Sid, e.Track.Kind, e.Participant.Identity);

        switch (e.Track)
        {
            case RemoteAudioTrack audio:
                var pump = Task.Run(() => PumpRemoteAudioAsync(audio, _shutdown.Token));
                lock (_pumps) _pumps.Add(pump);
                break;
            case RemoteVideoTrack video:
                var vpump = Task.Run(() => PumpRemoteVideoAsync(video, _shutdown.Token));
                lock (_pumps) _pumps.Add(vpump);
                break;
        }
    }

    /// <summary>
    /// Drain LiveKit audio frames at 16 kHz mono / 20 ms framing into the
    /// Teams audio send socket. Loops until the room disconnects or the
    /// bridge is disposed.
    ///
    /// Sends are paced to real-time (one frame per 20 ms wall-clock).
    /// LiveKit's <see cref="AudioStream"/> can deliver frames in bursts
    /// when the publisher buffers — without pacing those bursts pile into
    /// Teams's jitter buffer and you get choppy/garbled playback. With
    /// pacing the SDK sees a smooth 50 Hz cadence regardless of source
    /// burstiness.
    /// </summary>
    private async Task PumpRemoteAudioAsync(RemoteAudioTrack track, CancellationToken ct)
    {
        const int FrameMs = 20;

        // 20 ms × 16 000 Hz × 1 ch = 320 samples = 640 bytes per frame —
        // exactly what IAudioSocket.Send expects for AudioFormat.Pcm16K.
        AudioStream? stream = null;
        try
        {
            stream = AudioStream.FromTrack(
                track,
                sampleRate:  SampleRate,
                numChannels: (uint)Channels,
                frameSizeMs: FrameMs);

            var samples = new short[SampleRate / 50 * Channels]; // 320 for 20 ms mono
            var pacing  = System.Diagnostics.Stopwatch.StartNew();
            long sent   = 0;

            await foreach (var evt in stream.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                var socket = _mediaSession.AudioSocket;
                if (socket is null) continue;

                var frame  = evt.Frame;
                var arr    = frame.DataArray; // short[] — managed copy of the FFI buffer
                var copyLen = Math.Min(arr.Length, samples.Length);
                Array.Copy(arr, samples, copyLen);
                if (copyLen < samples.Length) Array.Clear(samples, copyLen, samples.Length - copyLen);

                var nSent = Interlocked.Increment(ref _liveKitAudioFramesSent);
                if (nSent == 1)
                    _log.LogInformation(
                        "first LK→Teams audio frame from track sid={Sid}: srcSamples={Src} dstSamples={Dst} sampleRate={Rate}",
                        track.Sid, arr.Length, samples.Length, frame.SampleRate);
                else if (nSent % 50 == 0)
                    _log.LogInformation("LK→Teams audio frame#{N}", nSent);

                AudioSendBuffer buf;
                try
                {
                    buf = new AudioSendBuffer(
                        samples,
                        samples.Length,
                        AudioFormat.Pcm16K,
                        SkypeMediaPlatform.GetCurrentTimestamp());
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "audio send buffer alloc failed (1 frame dropped)");
                    continue;
                }

                try
                {
                    socket.Send(buf);
                    sent++;
                }
                catch (Exception ex)
                {
                    // Send didn't take ownership — release the unmanaged block ourselves.
                    buf.Dispose();
                    _log.LogDebug(ex, "audio send failed (1 frame dropped)");
                }

                // Pace to wall-clock: target moment for the Nth send is
                // sent * FrameMs ms after the pump started. If we're ahead,
                // sleep until the target. If we're behind, fire immediately
                // (don't try to "catch up" by skipping; let the SDK handle
                // late frames via its jitter buffer).
                var targetMs = sent * FrameMs;
                var aheadMs  = targetMs - pacing.ElapsedMilliseconds;
                if (aheadMs > 0)
                    await Task.Delay((int)aheadMs, ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "remote audio pump terminated for track sid={Sid}", track.Sid);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Drain LiveKit video frames (decoded I420), encode to H.264 via the
    /// FFmpeg subprocess, and push each access unit into the Teams video
    /// socket once the socket is in <see cref="MediaSendStatus.Active"/>.
    /// Encoder is lazily spawned after the first frame arrives so we can
    /// match its input dims to the source resolution (LiveAvatar publishes
    /// at variable sizes; we scale to whatever Teams negotiated).
    /// </summary>
    private async Task PumpRemoteVideoAsync(RemoteVideoTrack track, CancellationToken ct)
    {
        VideoStream?         stream  = null;
        FfmpegH264Encoder?   encoder = null;
        Task?                sendPump = null;
        var                  videoSocket = TryGetVideoSocket();
        if (videoSocket is null)
        {
            _log.LogWarning("no video socket — dropping remote video track sid={Sid}", track.Sid);
            return;
        }

        try
        {
            stream = VideoStream.FromTrack(track, format: LiveKit.Proto.VideoBufferType.I420, capacity: 16);

            await foreach (var evt in stream.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                var frame = evt.Frame;
                var negotiated = _negotiatedSendFormat;
                if (negotiated is null) continue; // Teams hasn't said go yet.

                if (encoder is null)
                {
                    var inW   = frame.Width;
                    var inH   = frame.Height;
                    var outW  = negotiated.Width;
                    var outH  = negotiated.Height;
                    // FrameRate is float on VideoFormat; round to int for ffmpeg's -r flag
                    var fps   = Math.Max(1, (int)Math.Round(negotiated.FrameRate));
                    var br    = BitrateKbpsFor(outW, outH);
                    encoder = new FfmpegH264Encoder(
                        inW, inH, outW, outH, fps, br,
                        _loggerFactory.CreateLogger<FfmpegH264Encoder>());
                    sendPump = Task.Run(() => DrainEncoderToVideoSocketAsync(encoder, videoSocket, ct));
                }

                try
                {
                    await encoder.WriteFrameAsync(frame.DataBytes, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "encoder write failed — terminating video pump");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "remote video pump terminated for track sid={Sid}", track.Sid);
        }
        finally
        {
            stream?.Dispose();
            if (encoder is not null) await encoder.DisposeAsync();
            if (sendPump is not null)
            {
                try { await sendPump.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* swallowed for shutdown */ }
            }
        }
    }

    private async Task DrainEncoderToVideoSocketAsync(
        FfmpegH264Encoder encoder, IVideoSocket socket, CancellationToken ct)
    {
        try
        {
            await foreach (var nal in encoder.EncodedReader.ReadAllAsync(ct))
            {
                var format = _negotiatedSendFormat;
                if (format is null) continue;
                VideoSendBuffer? buf = null;
                try
                {
                    buf = new VideoSendBuffer(nal, nal.Length, format, SkypeMediaPlatform.GetCurrentTimestamp());
                    socket.Send(buf);
                }
                catch (Exception ex)
                {
                    buf?.Dispose();
                    _log.LogDebug(ex, "video send failed (1 access unit dropped)");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "video send pump terminated");
        }
    }

    /// <summary>
    /// Bitrate target for a given output resolution. Conservative numbers
    /// suitable for libx264 ultrafast/zerolatency on a network we don't
    /// control.
    /// </summary>
    private static int BitrateKbpsFor(int w, int h) => (w, h) switch
    {
        (1280, 720) => 1500,
        (640, 360)  => 600,
        (320, 180)  => 200,
        _           => Math.Max(200, w * h / 600),
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            _shutdown.Cancel();

            if (_mediaSession.AudioSocket is { } a)
                a.AudioMediaReceived -= OnTeamsAudioReceived;

            Task[] pumps;
            lock (_pumps) pumps = _pumps.ToArray();
            if (pumps.Length > 0)
            {
                try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { _log.LogWarning("pumps did not finish within 2s"); }
                catch (Exception ex)     { _log.LogDebug(ex, "pump shutdown threw"); }
            }

            _audioTrack?.Dispose();
            _audioSource?.Dispose();
            if (_room is not null)
            {
                await _room.DisconnectAsync();
                _room.Dispose();
            }
            _shutdown.Dispose();
            _log.LogInformation("bridge disposed room={Room}", _roomName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "bridge dispose error");
        }
    }
}
