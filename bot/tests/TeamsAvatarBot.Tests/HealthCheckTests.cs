using Microsoft.AspNetCore.Mvc.Testing;

namespace TeamsAvatarBot.Tests;

/// <summary>
/// Minimal liveness-probe test. Spins up the Host's TestServer and asserts
/// that /healthz returns 200 and includes the loaded configuration fields.
/// Lets us catch DI / config-loading regressions before deploy.
/// </summary>
public sealed class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthCheckTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_200_with_config()
    {
        using var client = _factory.CreateClient();
        var res  = await client.GetAsync("/healthz");
        var body = await res.Content.ReadAsStringAsync();

        Assert.True(res.IsSuccessStatusCode, $"healthz returned {(int)res.StatusCode}: {body}");
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"fqdn\":", body);
        Assert.Contains("\"livekitUrl\":", body);
    }
}
