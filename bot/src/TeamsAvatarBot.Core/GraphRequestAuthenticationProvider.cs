using System.Net.Http.Headers;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;

namespace TeamsAvatarBot.Core;

/// <summary>
/// IRequestAuthenticationProvider for the Graph Communications SDK.
/// Pattern follows microsoftgraph/microsoft-graph-comms-samples
/// (Samples/Common/Sample.Common/Authentication/AuthenticationProvider.cs).
///
/// Outbound: acquires app-only (client-credentials) bearer tokens via MSAL
/// for the Microsoft Graph audience and stamps them onto outbound requests.
/// MSAL caches tokens internally so we don't pay for a token acquisition
/// per call.
///
/// Inbound: validates the JWT on every <c>/api/calling</c> notification via
/// <see cref="GraphInboundJwtValidator"/> when
/// <c>BOT_VALIDATE_INBOUND_JWT=true</c> is set in the environment.
///
/// The env-var gate exists because the OpenID metadata endpoint Microsoft
/// uses for these tokens has moved historically (Bot Framework v1 →
/// Skype calling → various). A bad endpoint URL means every notification
/// is rejected with 401 and the bot becomes a ghost in the meeting
/// (visible roster entry, no media). When in doubt, leave the validator
/// off; the bot's existing TLS + the SDK's tenant scoping are the
/// remaining protection layers.
/// </summary>
public sealed class GraphRequestAuthenticationProvider : IRequestAuthenticationProvider
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly IConfidentialClientApplication _msal;
    private readonly string                          _tenantId;
    private readonly IGraphLogger                    _logger;
    private readonly GraphInboundJwtValidator?       _inboundValidator;

    public GraphRequestAuthenticationProvider(BotConfig config, IGraphLogger logger)
    {
        if (!config.IsAzureConfigured)
        {
            throw new InvalidOperationException(
                "Azure config is incomplete (TENANT_ID / APP_CLIENT_ID / APP_CLIENT_SECRET). "
                + "Run docs/azure-setup-runbook.md before starting the bot.");
        }
        _tenantId = config.TenantId!;
        _logger   = logger;
        _msal = ConfidentialClientApplicationBuilder
            .Create(config.AppClientId!)
            .WithClientSecret(config.AppClientSecret!)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{_tenantId}"))
            .Build();

        // JWT validation is opt-in via env var. The Bot Framework OIDC
        // endpoint URL has shifted historically; until the right one is
        // re-confirmed for this SDK version (1.2.0), default to trust-all
        // to avoid silently bricking the bot.
        var validateJwt = string.Equals(
            Environment.GetEnvironmentVariable("BOT_VALIDATE_INBOUND_JWT"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        _inboundValidator = validateJwt
            ? new GraphInboundJwtValidator(audience: config.AppClientId!)
            : null;
        _logger.Info(
            validateJwt
                ? "inbound JWT validation ENABLED (BOT_VALIDATE_INBOUND_JWT=true)"
                : "inbound JWT validation DISABLED (set BOT_VALIDATE_INBOUND_JWT=true to enable)");
    }

    /// <summary>
    /// Stamp an Authorization: Bearer header on outbound Graph calls.
    /// Tenant id is ignored here because we authenticate as our app, not on
    /// behalf of any specific tenant — the bot's home tenant owns the app
    /// registration regardless of which tenant the meeting belongs to.
    /// </summary>
    public async Task AuthenticateOutboundRequestAsync(HttpRequestMessage request, string tenant)
    {
        var result = await _msal
            .AcquireTokenForClient(new[] { GraphScope })
            .ExecuteAsync()
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
    }

    /// <summary>
    /// Validate the bearer JWT on an inbound notification from Microsoft.
    /// When validation is enabled, signature/issuer/audience/lifetime
    /// checks are delegated to <see cref="GraphInboundJwtValidator"/>; on
    /// failure we return <c>IsValid = false</c> so the SDK responds 401.
    /// When validation is disabled (default), we trust the request and
    /// pass the bot's home tenant through to the SDK.
    /// </summary>
    public async Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
    {
        if (_inboundValidator is null)
        {
            // Trust-all path: SDK still uses its own transport-layer checks
            // (TLS, request shape). The tenant id is informational only.
            return new RequestValidationResult { IsValid = true, TenantId = _tenantId };
        }

        var authHeader = request.Headers.Authorization?.ToString();
        try
        {
            var tid = await _inboundValidator
                .ValidateAndExtractTenantAsync(authHeader)
                .ConfigureAwait(false);
            // Token didn't carry a tid (Bot Framework tokens often don't) —
            // fall back to our own home tenant. The SDK uses the value only
            // to scope subsequent outbound calls within the notification's
            // processing chain.
            return new RequestValidationResult { IsValid = true, TenantId = tid ?? _tenantId };
        }
        catch (SecurityTokenException ex)
        {
            _logger.Warn($"inbound JWT rejected: {ex.Message}");
            return new RequestValidationResult { IsValid = false };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "inbound JWT validation threw");
            return new RequestValidationResult { IsValid = false };
        }
    }
}
