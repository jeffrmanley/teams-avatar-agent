namespace TeamsAvatarBot.Core;

/// <summary>
/// Strongly-typed configuration loaded from environment variables (.env via
/// DotNetEnv at startup).
///
/// Required fields are needed for the host to boot and the network plane to
/// function. Azure-specific fields (tenant + app credentials) are nullable
/// because Phase 2.1 wants to be able to boot the bot, verify Cloudflare ↔
/// Let's Encrypt ↔ Kestrel reachability, and exercise /healthz before the
/// user has finished the M365 Developer Program tenant setup. Phase 2.2's
/// CallHandler will hard-fail if these are still null when an actual call
/// comes in.
/// </summary>
public sealed record BotConfig
{
    /// <summary>Public FQDN of this host, e.g. <c>bot.example.com</c>.</summary>
    public required string PublicFqdn { get; init; }

    /// <summary>Public IPv4 the bot binds for media (Teams sends SRTP here).</summary>
    public required string InstancePublicIp { get; init; }

    /// <summary>HTTPS / signaling port. 443 in prod, higher port in dev.</summary>
    public required int CallingPort { get; init; }

    /// <summary>UDP port for media (Microsoft.Skype.Bots.Media binds here).</summary>
    public required int MediaPort { get; init; }

    /// <summary>LiveKit project URL (wss://...livekit.cloud).</summary>
    public required string LiveKitUrl { get; init; }

    /// <summary>LiveKit API key.</summary>
    public required string LiveKitApiKey { get; init; }

    /// <summary>LiveKit API secret.</summary>
    public required string LiveKitApiSecret { get; init; }

    /// <summary>Azure AD tenant ID (Entra app registration). Null until Azure setup is done.</summary>
    public string? TenantId { get; init; }

    /// <summary>Application (client) ID — same as the Azure Bot resource MicrosoftAppId.</summary>
    public string? AppClientId { get; init; }

    /// <summary>Client secret. Loaded from .env; rotated per docs/runbook-rotate-secrets.md.</summary>
    public string? AppClientSecret { get; init; }

    /// <summary>True when all three Azure fields are populated; gating Phase 2.2 wiring.</summary>
    public bool IsAzureConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(AppClientId)
        && !string.IsNullOrWhiteSpace(AppClientSecret);

    /// <summary>
    /// Build a BotConfig from currently-set env vars. Throws if anything
    /// strictly required for boot is missing; Azure fields are loaded if
    /// present and left null if absent.
    /// </summary>
    public static BotConfig FromEnvironment() => new()
    {
        PublicFqdn       = Require("BOT_PUBLIC_FQDN"),
        InstancePublicIp = Require("BOT_INSTANCE_PUBLIC_IP"),
        CallingPort      = int.TryParse(Environment.GetEnvironmentVariable("BOT_CALLING_PORT"), out var cp) ? cp : 443,
        MediaPort        = int.TryParse(Environment.GetEnvironmentVariable("BOT_MEDIA_PORT"),   out var mp) ? mp : 8445,
        LiveKitUrl       = Require("LIVEKIT_URL"),
        LiveKitApiKey    = Require("LIVEKIT_API_KEY"),
        LiveKitApiSecret = Require("LIVEKIT_API_SECRET"),
        TenantId         = Optional("TENANT_ID"),
        AppClientId      = Optional("APP_CLIENT_ID"),
        AppClientSecret  = Optional("APP_CLIENT_SECRET"),
    };

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"missing required env var: {name} (see .env.example)");

    private static string? Optional(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
