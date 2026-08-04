using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TeamsAvatarBot.Core;

/// <summary>
/// Validates the bearer JWT on inbound Graph Communications notifications
/// (POST /api/calling). Microsoft signs these tokens with keys published at
/// the Bot Framework OpenID Connect metadata endpoint; we fetch and cache
/// them via <see cref="ConfigurationManager{T}"/> (default 12-hour refresh).
///
/// The token shape we expect:
///   - issuer:   <c>https://api.botframework.com</c> (or the gov/cn variants)
///   - audience: our bot's AAD app id (<see cref="BotConfig.AppClientId"/>)
///   - exp:      in the future (5-min clock skew allowance)
///   - signed by one of the keys in the OIDC metadata
///
/// If any check fails we throw <see cref="SecurityTokenException"/>. The
/// caller (see <see cref="GraphRequestAuthenticationProvider"/>) translates
/// that into <c>IsValid = false</c> so the SDK returns 401 to Microsoft.
/// </summary>
public sealed class GraphInboundJwtValidator
{
    // Bot Framework OIDC metadata endpoint. The keys here are the ones
    // Microsoft uses to sign inbound calling notifications. Documented at
    // https://learn.microsoft.com/en-us/azure/bot-service/rest-api/bot-framework-rest-connector-authentication
    private const string DefaultMetadataAddress =
        "https://api.botframework.com/api/.well-known/openidconfiguration";

    // Issuers we accept. The Microsoft sample's AuthenticationProvider.cs
    // lists these as the canonical set for Bot Framework / Skype calling.
    private static readonly string[] DefaultValidIssuers =
    [
        "https://api.botframework.com",
        "https://api.botframework.us",
        "https://api.botframework.cn",
    ];

    private readonly string _audience;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configManager;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly string[] _validIssuers;

    /// <summary>Production constructor. Uses the live Bot Framework OIDC endpoint.</summary>
    public GraphInboundJwtValidator(string audience)
        : this(
            audience,
            DefaultValidIssuers,
            new ConfigurationManager<OpenIdConnectConfiguration>(
                DefaultMetadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever()))
    { }

    /// <summary>
    /// Test-friendly constructor — inject a fake <see cref="IConfigurationManager{T}"/>
    /// returning known signing keys and a custom issuer set.
    /// </summary>
    internal GraphInboundJwtValidator(
        string audience,
        string[] validIssuers,
        IConfigurationManager<OpenIdConnectConfiguration> configManager)
    {
        _audience       = audience;
        _validIssuers   = validIssuers;
        _configManager  = configManager;
    }

    /// <summary>
    /// Parse + validate the bearer token from an <c>Authorization</c> header
    /// value (with or without the <c>"Bearer "</c> prefix). Returns the
    /// validated principal's tenant id (the <c>tid</c> claim if present,
    /// else null — Bot Framework tokens don't always carry one).
    /// </summary>
    /// <exception cref="SecurityTokenException">If the header is missing, malformed, or the token fails validation.</exception>
    public async Task<string?> ValidateAndExtractTenantAsync(string? authorizationHeader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            throw new SecurityTokenException("missing Authorization header");

        var token = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader.Substring("Bearer ".Length).Trim()
            : authorizationHeader.Trim();

        if (string.IsNullOrEmpty(token))
            throw new SecurityTokenException("empty bearer token");

        var config = await _configManager.GetConfigurationAsync(ct).ConfigureAwait(false);

        var parameters = new TokenValidationParameters
        {
            ValidIssuers          = _validIssuers,
            ValidAudience         = _audience,
            IssuerSigningKeys     = config.SigningKeys,
            ValidateIssuer        = true,
            ValidateAudience      = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime      = true,
            ClockSkew             = TimeSpan.FromMinutes(5),
        };

        var principal = _handler.ValidateToken(token, parameters, out var validated);
        // Microsoft uses both "tid" (v2) and the AAD tenant in different
        // claim positions; we surface whichever we find so the caller can
        // pass it to the SDK as the resolved tenant.
        var tid = principal.FindFirst("tid")?.Value
               ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        _ = validated; // silence unused warning; the JwtSecurityToken isn't needed here
        return tid;
    }
}
