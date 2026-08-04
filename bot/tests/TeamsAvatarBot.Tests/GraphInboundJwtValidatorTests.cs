using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TeamsAvatarBot.Core;

namespace TeamsAvatarBot.Tests;

/// <summary>
/// Security-critical: the bot will accept call notifications and drive
/// state changes off them. If JWT validation regresses, anyone who can
/// reach <c>/api/calling</c> can drive bot behaviour. These tests pin the
/// negative + positive cases against a self-signed RSA key so we don't
/// depend on Microsoft's live OIDC endpoint.
/// </summary>
public sealed class GraphInboundJwtValidatorTests
{
    private const string Audience  = "11111111-1111-1111-1111-111111111111";
    private const string Issuer    = "https://api.botframework.com";
    private const string TenantId  = "22222222-2222-2222-2222-222222222222";

    private readonly RsaSecurityKey         _signingKey;
    private readonly SigningCredentials     _signingCreds;
    private readonly GraphInboundJwtValidator _validator;

    public GraphInboundJwtValidatorTests()
    {
        var rsa  = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(rsa) { KeyId = "test-key-1" };
        _signingCreds = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);

        var config = new OpenIdConnectConfiguration { Issuer = Issuer };
        config.SigningKeys.Add(_signingKey);

        _validator = new GraphInboundJwtValidator(
            audience:      Audience,
            validIssuers:  new[] { Issuer },
            configManager: new StaticConfigurationManager<OpenIdConnectConfiguration>(config));
    }

    private string MintToken(
        string? audience  = Audience,
        string? issuer    = Issuer,
        string? tenantId  = TenantId,
        DateTime? expires = null,
        SigningCredentials? creds = null)
    {
        var claims    = new List<Claim>();
        if (tenantId is not null) claims.Add(new Claim("tid", tenantId));
        var expiry    = expires ?? DateTime.UtcNow.AddMinutes(10);
        // notBefore must be in the past *and* before expiry. For the
        // expired-token case, we still want a sane window — set notBefore
        // a minute before expiry so the JWT ctor accepts it.
        var nbf       = expiry.AddMinutes(-1);
        if (nbf > DateTime.UtcNow.AddMinutes(-1)) nbf = DateTime.UtcNow.AddMinutes(-1);
        var token     = new JwtSecurityToken(
            issuer:    issuer,
            audience:  audience,
            claims:    claims,
            notBefore: nbf,
            expires:   expiry,
            signingCredentials: creds ?? _signingCreds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Valid_token_returns_tenant_id_from_tid_claim()
    {
        var token = MintToken();
        var tid = await _validator.ValidateAndExtractTenantAsync($"Bearer {token}");
        Assert.Equal(TenantId, tid);
    }

    [Fact]
    public async Task Header_without_bearer_prefix_still_validates()
    {
        // Some clients send the raw token without the "Bearer " prefix.
        var token = MintToken();
        var tid = await _validator.ValidateAndExtractTenantAsync(token);
        Assert.Equal(TenantId, tid);
    }

    [Fact]
    public async Task Missing_header_throws()
    {
        await Assert.ThrowsAsync<SecurityTokenException>(() =>
            _validator.ValidateAndExtractTenantAsync(null));
    }

    [Fact]
    public async Task Empty_bearer_throws()
    {
        await Assert.ThrowsAsync<SecurityTokenException>(() =>
            _validator.ValidateAndExtractTenantAsync("Bearer "));
    }

    [Fact]
    public async Task Garbage_token_throws()
    {
        // The JWT handler throws SecurityTokenMalformedException (a derived
        // type) on un-parseable input — the caller catches the base type.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _validator.ValidateAndExtractTenantAsync("Bearer not-a-jwt"));
    }

    [Fact]
    public async Task Wrong_audience_throws()
    {
        var token = MintToken(audience: "00000000-0000-0000-0000-000000000000");
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() =>
            _validator.ValidateAndExtractTenantAsync($"Bearer {token}"));
    }

    [Fact]
    public async Task Wrong_issuer_throws()
    {
        var token = MintToken(issuer: "https://evil.example.com");
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() =>
            _validator.ValidateAndExtractTenantAsync($"Bearer {token}"));
    }

    [Fact]
    public async Task Expired_token_throws()
    {
        var token = MintToken(expires: DateTime.UtcNow.AddMinutes(-30));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() =>
            _validator.ValidateAndExtractTenantAsync($"Bearer {token}"));
    }

    [Fact]
    public async Task Token_signed_with_unknown_key_throws()
    {
        var rogueRsa  = RSA.Create(2048);
        var rogueKey  = new RsaSecurityKey(rogueRsa) { KeyId = "rogue-key" };
        var rogueCreds = new SigningCredentials(rogueKey, SecurityAlgorithms.RsaSha256);
        var token = MintToken(creds: rogueCreds);
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() =>
            _validator.ValidateAndExtractTenantAsync($"Bearer {token}"));
    }

    [Fact]
    public async Task Token_without_tid_falls_back_to_null()
    {
        var token = MintToken(tenantId: null);
        var tid = await _validator.ValidateAndExtractTenantAsync($"Bearer {token}");
        Assert.Null(tid);
    }
}
