using Microsoft.Extensions.Logging;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Skype.Bots.Media;
using TeamsAvatarBot.Core;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Per-call handler. One instance handles many calls (it's a service, not a
/// per-call object — per-call state lives on each <see cref="ICall"/> via
/// <c>call.MediaSession</c> and the <see cref="LiveKitBridge"/> instance we
/// stash in <see cref="ICall.PrivateContext"/>).
///
/// Phase 2.3 responsibilities:
///   1. Build a <see cref="ILocalMediaSession"/> with one IAudioSocket
///      (PCM 16 kHz, send+receive) and one IVideoSocket (H.264 send+recv).
///   2. Answer the incoming call with that media session.
///   3. Instantiate a <see cref="LiveKitBridge"/> for the call, pass it the
///      sockets, and let it ferry frames to/from a per-call LiveKit room.
///   4. Tear down on call termination.
/// </summary>
public sealed class CallHandler
{
    private readonly ICommunicationsClient _client;
    private readonly BotConfig             _config;
    private readonly ILogger<CallHandler>  _log;
    private readonly ILoggerFactory        _loggerFactory;

    // Teams negotiates the send format from this list. The Skype.Bots.Media docs
    // require **at least one** H264_320x180 format for fallback interop — without
    // it Teams may not raise VideoSendStatusChanged with Active=true and we'll
    // never get a chance to send. Keep our preferred 720p first; the rest are
    // safety-net rungs Teams can step down to under network pressure.
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

    public CallHandler(
        ICommunicationsClient client,
        BotConfig config,
        ILogger<CallHandler> log,
        ILoggerFactory loggerFactory)
    {
        _client        = client;
        _config        = config;
        _log           = log;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Wire per-call event handlers and answer the call with a media
    /// session. Called from <see cref="CommunicationsService"/>'s
    /// <c>OnIncoming</c>. Inbound calls only — outbound calls created via
    /// <see cref="AttachToOutboundAsync"/> are already in the Establishing
    /// state and don't need answering.
    /// </summary>
    public async Task AttachAsync(ICall call)
    {
        _log.LogInformation("attaching to inbound call {CallId}", call.Id);

        var mediaSessionId = Guid.TryParse(call.Id, out var g) ? g : Guid.NewGuid();
        var mediaSession   = CreateLocalMediaSession(mediaSessionId);

        WireBridge(call, mediaSession);

        try
        {
            await call.AnswerAsync(mediaSession);
            _log.LogInformation("answered call {CallId} with mediaSession={Sid}", call.Id, mediaSessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "answer failed for call {CallId}", call.Id);
            throw;
        }
    }

    /// <summary>
    /// Attach to an outbound call already created via
    /// <c>ICallCollection.AddAsync(JoinMeetingParameters)</c>. The media
    /// session was supplied at AddAsync time, so we just wire the bridge
    /// and wait for Established. No AnswerAsync.
    /// </summary>
    public Task AttachToOutboundAsync(ICall call, ILocalMediaSession mediaSession)
    {
        _log.LogInformation("attaching to outbound call {CallId} state={State}", call.Id, call.Resource.State);
        WireBridge(call, mediaSession);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wire <see cref="LiveKitBridge"/> lifecycle to the call's state
    /// transitions and forward participant events to the log.
    /// </summary>
    private void WireBridge(ICall call, ILocalMediaSession mediaSession)
    {
        var bridge = new LiveKitBridge(
            _config,
            call.Id,
            mediaSession,
            _loggerFactory.CreateLogger<LiveKitBridge>(),
            _loggerFactory);

        // Graph fires call.OnUpdated for *every* sub-resource change
        // (participants, modalities, capabilities), not just state
        // transitions. Track whether we've already kicked off StartAsync /
        // DisposeAsync so the bridge isn't double-invoked. Concurrent
        // StartAsync calls were observed to race on the `_room` field and
        // brick audio capture (2026-05-02 live test).
        var startLatch    = 0;
        var disposeLatch  = 0;

        call.OnUpdated += async (_, _) =>
        {
            var state    = call.Resource.State;
            var info     = call.Resource.ResultInfo;
            var code     = info?.Code;
            var subcode  = info?.Subcode;
            var msg      = info?.Message;

            _log.LogInformation(
                "call {CallId} updated state={State} resultCode={Code} resultSubcode={Subcode} resultMsg={Msg}",
                call.Id, state, code, subcode, msg);

            if (state == Microsoft.Graph.Models.CallState.Established)
            {
                if (Interlocked.Exchange(ref startLatch, 1) != 0) return;
                try { await bridge.StartAsync(); }
                catch (Exception ex) { _log.LogError(ex, "bridge start failed for call {CallId}", call.Id); }
            }
            else if (state == Microsoft.Graph.Models.CallState.Terminated)
            {
                if (Interlocked.Exchange(ref disposeLatch, 1) != 0) return;
                if (code is not null and not 0)
                    _log.LogWarning(
                        "call {CallId} TERMINATED by Graph: code={Code} subcode={Subcode} message={Msg}",
                        call.Id, code, subcode, msg);
                try { await bridge.DisposeAsync(); }
                catch (Exception ex) { _log.LogError(ex, "bridge dispose failed for call {CallId}", call.Id); }
            }
        };

        call.Participants.OnUpdated += (_, e) =>
        {
            foreach (var p in e.AddedResources)
                _log.LogInformation("participant joined call={CallId} id={Id}", call.Id, p.Id);
            foreach (var p in e.RemovedResources)
                _log.LogInformation("participant left   call={CallId} id={Id}", call.Id, p.Id);
        };
    }

    /// <summary>
    /// Build a fresh <see cref="ILocalMediaSession"/> with the standard
    /// audio + video socket settings used by both inbound and outbound calls.
    /// </summary>
    public ILocalMediaSession CreateLocalMediaSession() =>
        CreateLocalMediaSession(Guid.NewGuid());

    private ILocalMediaSession CreateLocalMediaSession(Guid mediaSessionId)
    {
        var audioSettings = new AudioSocketSettings
        {
            StreamDirections     = StreamDirection.Sendrecv,
            SupportedAudioFormat = AudioFormat.Pcm16K,
        };

        var videoSettings = new[]
        {
            new VideoSocketSettings
            {
                StreamDirections          = StreamDirection.Sendrecv,
                ReceiveColorFormat        = VideoColorFormat.H264,
                SupportedSendVideoFormats = SupportedSendVideoFormats,
                MaxConcurrentSendStreams  = 1,
            },
        };

        return _client.CreateMediaSession(
            audioSettings,
            videoSettings,
            mediaSessionId: mediaSessionId);
    }
}
