using Microsoft.Extensions.Logging;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Skype.Bots.Media;
using TeamsAvatarBot.Core;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Owns the singleton <see cref="ICommunicationsClient"/> that talks to
/// Microsoft Graph for the bot's lifetime.
///
/// Phase 2.3: also wires the <see cref="MediaPlatformSettings"/> so the
/// underlying Skype.Bots.Media stack knows which UDP port + cert + public
/// IP to bind for incoming SRTP. Per-call <see cref="ILocalMediaSession"/>
/// instances are created on demand via <see cref="CreateMediaSession"/>.
/// </summary>
public sealed class CommunicationsService : IAsyncDisposable
{
    private readonly ICommunicationsClient _client;
    private readonly ILogger<CommunicationsService> _log;
    private readonly CallHandler _handler;
    private readonly BotConfig _config;

    public CommunicationsService(
        BotConfig config,
        string certThumbprint,
        ILogger<CommunicationsService> log,
        ILogger<CallHandler> handlerLog,
        ILoggerFactory loggerFactory)
    {
        _log     = log;
        _config  = config;

        var graphLogger = new GraphLogger("TeamsAvatarBot");
        // Pipe the SDK's internal log stream (media-stack diagnostics, SDP
        // negotiations, etc.) into Serilog so we can actually see why calls
        // fail. Without this every problem inside the SDK is invisible and
        // we just see "Server Internal Error" from Graph.
        graphLogger.Subscribe(new GraphLoggerForwarder(loggerFactory.CreateLogger("GraphSdk")));

        var authProvider = new GraphRequestAuthenticationProvider(config, graphLogger);
        var mediaSettings = MediaPlatformSettingsFactory.Build(config, certThumbprint);

        var notificationUri = new Uri($"https://{config.PublicFqdn}/api/calling");

        _client = new CommunicationsClientBuilder("TeamsAvatarBot", config.AppClientId!, graphLogger)
            .SetAuthenticationProvider(authProvider)
            .SetNotificationUrl(notificationUri)
            .SetMediaPlatformSettings(mediaSettings)
            .SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0"))
            .Build();

        _handler = new CallHandler(_client, config, handlerLog, loggerFactory);

        _client.Calls().OnIncoming += OnIncoming;
        _client.Calls().OnUpdated  += OnUpdated;

        _log.LogInformation(
            "communications client built: notificationUri={Uri} appId={AppId} mediaPort={Port}",
            notificationUri, config.AppClientId, config.MediaPort);
    }

    /// <summary>The underlying SDK client. Used by the HTTP layer to forward notifications.</summary>
    public ICommunicationsClient Client => _client;

    /// <summary>
    /// Phase 2.3e: dial into a Teams meeting via Graph Communications.
    /// Supports both URL formats — legacy <c>/l/meetup-join/...</c> uses
    /// the SDK's <c>JoinMeetingParameters</c> helper; the modern
    /// <c>/meet/&lt;id&gt;?p=&lt;passcode&gt;</c> format uses the lower-level
    /// <c>ICallCollection.AddAsync(Call, IMediaSession)</c> overload because
    /// <c>JoinMeetingParameters</c>'s ctor requires a <c>ChatInfo</c> we
    /// don't have for short URLs.
    /// </summary>
    public async Task<string> JoinMeetingAsync(string meetingUrl, CancellationToken ct = default)
    {
        var parsed       = TeamsMeetingJoinUrl.Parse(meetingUrl);
        var mediaSession = _handler.CreateLocalMediaSession();

        ICall call = parsed switch
        {
            { ChatInfo: not null, MeetingInfo: Microsoft.Graph.Models.OrganizerMeetingInfo orgInfo, TenantId: { } tenant } =>
                await JoinViaJoinMeetingParametersAsync(parsed.ChatInfo, orgInfo, tenant, mediaSession, ct),
            { MeetingInfo: Microsoft.Graph.Models.JoinMeetingIdMeetingInfo idInfo } =>
                await JoinViaJoinMeetingIdAsync(idInfo, mediaSession, ct),
            _ =>
                throw new InvalidOperationException("parsed URL produced an unrecognised MeetingInfo shape"),
        };

        await _handler.AttachToOutboundAsync(call, mediaSession);
        _log.LogInformation("outbound call created callId={CallId}", call.Id);
        return call.Id;
    }

    private async Task<ICall> JoinViaJoinMeetingParametersAsync(
        Microsoft.Graph.Models.ChatInfo chatInfo,
        Microsoft.Graph.Models.OrganizerMeetingInfo meetingInfo,
        string tenantId,
        Microsoft.Graph.Communications.Calls.Media.ILocalMediaSession mediaSession,
        CancellationToken ct)
    {
        _log.LogInformation(
            "JoinMeetingAsync (legacy) threadId={Thread} tenantId={Tenant}",
            chatInfo.ThreadId, tenantId);

        var joinParams = new Microsoft.Graph.Communications.Calls.JoinMeetingParameters(
            chatInfo, meetingInfo, mediaSession)
        {
            TenantId = tenantId,
        };

        return await _client.Calls().AddAsync(joinParams, scenarioId: Guid.NewGuid(), cancellationToken: ct);
    }

    private async Task<ICall> JoinViaJoinMeetingIdAsync(
        Microsoft.Graph.Models.JoinMeetingIdMeetingInfo meetingInfo,
        Microsoft.Graph.Communications.Calls.Media.ILocalMediaSession mediaSession,
        CancellationToken ct)
    {
        // Short URLs don't carry the tenant — fall back to the bot's own
        // tenant. Works as long as the meeting is hosted in the same tenant
        // the bot's app registration belongs to (true for our dev setup).
        var tenantId = _config.TenantId
            ?? throw new InvalidOperationException("short meeting URL has no tenant id and BotConfig.TenantId is null");

        _log.LogInformation(
            "JoinMeetingAsync (short) joinMeetingId={MeetingId} tenantId={Tenant} hasPasscode={HasPasscode}",
            meetingInfo.JoinMeetingId, tenantId, !string.IsNullOrEmpty(meetingInfo.Passcode));

        var call = new Microsoft.Graph.Models.Call
        {
            Direction           = Microsoft.Graph.Models.CallDirection.Outgoing,
            CallbackUri         = new Uri($"https://{_config.PublicFqdn}/api/calling").ToString(),
            TenantId            = tenantId,
            MeetingInfo         = meetingInfo,
            RequestedModalities = new List<Microsoft.Graph.Models.Modality?>
            {
                Microsoft.Graph.Models.Modality.Audio,
                Microsoft.Graph.Models.Modality.Video,
            },
            MediaConfig = new Microsoft.Graph.Models.AppHostedMediaConfig(),
        };

        return await _client.Calls().AddAsync(call, mediaSession, scenarioId: Guid.NewGuid(), cancellationToken: ct);
    }

    private void OnIncoming(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (var call in args.AddedResources)
        {
            _log.LogInformation("OnIncoming call={CallId} state={State}", call.Id, call.Resource.State);
            _handler.AttachAsync(call).ContinueWith(t =>
            {
                if (t.Exception is not null)
                    _log.LogError(t.Exception, "CallHandler.AttachAsync failed");
            }, TaskScheduler.Default);
        }
    }

    private void OnUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (var call in args.AddedResources)
            _log.LogInformation("OnUpdated added call={CallId}", call.Id);
        foreach (var call in args.RemovedResources)
            _log.LogInformation("OnUpdated removed call={CallId}", call.Id);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
