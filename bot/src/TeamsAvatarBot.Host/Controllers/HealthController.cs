using Microsoft.AspNetCore.Mvc;
using TeamsAvatarBot.Core;

namespace TeamsAvatarBot.Host.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class HealthController(BotConfig config) : ControllerBase
{
    /// <summary>
    /// Liveness probe. Returns 200 with the key configured identifiers so
    /// you can eyeball "is this the right bot, is config loaded" in one
    /// curl. No secrets exposed. <c>azureConfigured=false</c> means Phase
    /// 2.2's call handler will refuse to start; routes that just exercise
    /// the network plane (this endpoint, /api/calling stub) still work.
    /// </summary>
    [HttpGet("/healthz")]
    public IActionResult Get() => Ok(new
    {
        status          = "ok",
        fqdn            = config.PublicFqdn,
        publicIp        = config.InstancePublicIp,
        livekitUrl      = config.LiveKitUrl,
        appClientId     = config.AppClientId ?? "(unset — pending Azure setup)",
        tenantId        = config.TenantId    ?? "(unset — pending Azure setup)",
        azureConfigured = config.IsAzureConfigured,
        time            = DateTimeOffset.UtcNow,
    });
}
