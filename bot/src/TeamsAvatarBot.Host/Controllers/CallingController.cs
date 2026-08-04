using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Client;
using TeamsAvatarBot.Core;
using TeamsAvatarBot.LiveKitBridge;

namespace TeamsAvatarBot.Host.Controllers;

/// <summary>
/// Webhook endpoints invoked by Microsoft Graph during call setup and
/// per-call notifications. PRD §5.3.
///
/// Phase 2.2 — accepts notifications and forwards them to the Graph
/// Communications SDK via <see cref="CommunicationsClientExtensions.ProcessNotificationAsync"/>.
/// The SDK handles JWT validation (currently stubbed in our auth provider —
/// Phase 3 hardens it) and dispatches to <see cref="CommunicationsService"/>'s
/// OnIncoming / OnUpdated handlers.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class CallingController(ILogger<CallingController> log) : ControllerBase
{
    /// <summary>
    /// POST /api/calling — primary Graph webhook. Must respond &lt;3 s or
    /// Graph retries. Returns 503 if the bot was started before Azure
    /// config was populated (CommunicationsService is conditionally
    /// registered in Program.cs).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Calling(
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var commsService = services.GetService<CommunicationsService>();
        if (commsService is null)
        {
            log.LogWarning("/api/calling hit but CommunicationsService not registered (no Azure config)");
            return StatusCode(503, new { error = "bot not configured for calling — see logs" });
        }

        // Build an HttpRequestMessage that mirrors the inbound request so
        // the SDK can validate + dispatch. We have to re-serialise the body
        // because ASP.NET Core's HttpRequest stream is one-shot.
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        using var sdkRequest = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"https://{Request.Host}{Request.Path}{Request.QueryString}"))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, Request.ContentType ?? "application/json"),
        };
        foreach (var (key, value) in Request.Headers)
        {
            // Skip content-* (lives on Content), and Host (set by HttpRequestMessage URI).
            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            sdkRequest.Headers.TryAddWithoutValidation(key, value.ToArray());
        }

        try
        {
            var response = await commsService.Client.ProcessNotificationAsync(sdkRequest);
            return StatusCode((int)response.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ProcessNotificationAsync failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/join?meetingUrl=... — dev endpoint that programmatically
    /// dials Iris into a Teams meeting. Requires <c>X-Bot-Secret</c> header
    /// to match <c>BOT_DISPATCH_SECRET</c> in env so this isn't a drive-by
    /// abuse vector while we're sideloading manifests later (Phase 2.4).
    /// </summary>
    [HttpGet("/api/join")]
    public async Task<IActionResult> Join(
        [FromQuery] string meetingUrl,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var expected = Environment.GetEnvironmentVariable("BOT_DISPATCH_SECRET");
        if (!string.IsNullOrEmpty(expected))
        {
            var provided = Request.Headers["X-Bot-Secret"].ToString();
            if (!string.Equals(provided, expected, StringComparison.Ordinal))
                return StatusCode(401, new { error = "X-Bot-Secret missing or mismatched" });
        }

        var commsService = services.GetService<CommunicationsService>();
        if (commsService is null)
            return StatusCode(503, new { error = "bot not configured for calling — see logs" });

        if (string.IsNullOrWhiteSpace(meetingUrl))
            return BadRequest(new { error = "meetingUrl query parameter is required" });

        try
        {
            var callId = await commsService.JoinMeetingAsync(meetingUrl, ct);
            log.LogInformation("dispatched bot to {Url} as callId={CallId}", meetingUrl, callId);
            return Accepted(new { meetingUrl, callId, room = $"meeting-{callId}" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "JoinMeetingAsync failed for {Url}", meetingUrl);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
