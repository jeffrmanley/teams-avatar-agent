# The C# bot foundation: from scaffold to public reachability

*Article 08 of the Iris series. Covers Phase 2.1, 2.2, and 2.2.5 of the project.*

**Date:** 2026-04-27
**Author:** Jeff Manley
**TL;DR:** Three commits, three days. Phase 2.1 scaffolds the .NET 8 solution and the ASP.NET Core host with `/healthz`. Phase 2.2 wires the Microsoft Graph Communications SDK + MSAL via the `IRequestAuthenticationProvider` pattern, with a CallHandler skeleton that logs incoming calls but doesn't yet answer them. Phase 2.2.5 binds Kestrel directly to port 443 with the Posh-ACME-issued cert and skips IIS / reverse-proxy entirely. The payoff is a single curl from any external machine — `curl https://bot.example.com/healthz` — that returns a 200 with `azureConfigured=true`. That's the moment Microsoft Graph can reach our bot.

---

## Why this article spans three sub-phases

Phase 2 of the project is "build the C# Graph Communications media bot." That's a large task, so we sliced it into incremental commits, each with its own acceptance criterion:

| Sub-phase | What it produces | Acceptance |
|---|---|---|
| **2.1** | Solution + host scaffold | `dotnet build` succeeds, `/healthz` returns 200 in dev |
| **2.2** | Graph SDK + MSAL wired | `CommunicationsClient` builds at startup; `OnIncoming` event handler attached |
| **2.2.5** | Public reachability | `curl https://bot.example.com/healthz` returns 200 from external clients |
| 2.3 (next) | LiveKit bridge | Bot answers calls, moves audio/video frames |
| 2.4 (later) | Teams app manifest | Sideload-able from Teams Admin Center |

This article covers 2.1, 2.2, and 2.2.5. Phase 2.3 — the actual media-frame transport — is its own large piece of work and will get its own articles. The work in this article is the **scaffolding that makes Phase 2.3 possible**: the host process exists, the Graph SDK is wired, the cert is bound, the firewall is open, and the bot is reachable from the public internet. None of those are interesting individually. Together they're the bedrock everything else builds on.

## Phase 2.1: the solution scaffold

The .NET 8 solution has three projects, mirroring the layout the PRD specced:

```
bot/
├── TeamsAvatarBot.sln
├── src/
│   ├── TeamsAvatarBot.Host/         # ASP.NET Core host, controllers
│   ├── TeamsAvatarBot.Core/         # Call handling, Graph SDK glue
│   └── TeamsAvatarBot.LiveKitBridge/ # LiveKit peer + transcoder (Phase 2.3)
└── tests/
    └── TeamsAvatarBot.Host.Tests/
```

**`Host`** is the ASP.NET Core entry point. It owns the web server, the controllers (`/api/calling`, `/healthz`, `/api/join`), and the `Program.cs` startup wiring. It depends on `Core`.

**`Core`** is where the Graph Communications SDK lives. `BotConfig`, `CommunicationsService`, `CallHandler`, `GraphRequestAuthenticationProvider`. No HTTP concerns; just the SDK contract surface. This is the project that depends on `Microsoft.Graph.Communications.*` and `Microsoft.Skype.Bots.Media`.

**`LiveKitBridge`** is the per-call frame transport, currently a `NotImplementedException` stub. Phase 2.3 fills it in. Separated as its own project so it can be unit-tested independently of the Graph SDK.

Why three projects instead of one: the dependencies separate cleanly, and the `Microsoft.Skype.Bots.Media` package is **native and Windows-only**. Keeping it inside `Core` (and out of `Host`) makes it possible to write tests for `Host` that run on any .NET 8 platform — including, in CI, a Linux runner. Tests for `Core` need a Windows runner. Splitting was about an hour of work upfront and saves headaches in CI later.

The PackageReference list inside `Core` (the load-bearing one):

```xml
<PackageReference Include="Microsoft.Graph.Communications.Calls"        Version="1.2.0.15690" />
<PackageReference Include="Microsoft.Graph.Communications.Calls.Media"  Version="1.2.0.15690" />
<PackageReference Include="Microsoft.Graph.Communications.Client"        Version="1.2.0.15690" />
<PackageReference Include="Microsoft.Skype.Bots.Media"                  Version="1.27.0.19-alpha" />
<PackageReference Include="Microsoft.Identity.Client"                    Version="4.*" />
```

The `Microsoft.Skype.Bots.Media` version is alpha. It has been alpha for years. That's not a red flag — it's the canonical Microsoft package for app-hosted media bots, and "alpha" in this case means "Microsoft hasn't moved the version label in a long time," not "it's unstable." It's what every Microsoft sample uses.

[`bot/src/TeamsAvatarBot.Core/BotConfig.cs`](../../bot/src/TeamsAvatarBot.Core/BotConfig.cs) is where strongly-typed config gets loaded from environment variables. The shape:

```csharp
public sealed record BotConfig
{
    public required string PublicFqdn       { get; init; }
    public required string InstancePublicIp { get; init; }
    public required int    CallingPort      { get; init; }   // 443
    public required int    MediaPort        { get; init; }   // 8445 default
    public required string LiveKitUrl       { get; init; }
    public required string LiveKitApiKey    { get; init; }
    public required string LiveKitApiSecret { get; init; }
    public string? TenantId        { get; init; }
    public string? AppClientId     { get; init; }
    public string? AppClientSecret { get; init; }

    public bool IsAzureConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(AppClientId)
        && !string.IsNullOrWhiteSpace(AppClientSecret);
}
```

The Azure fields (`TenantId`, `AppClientId`, `AppClientSecret`) are **nullable on purpose**. In Phase 2.1 we wanted to be able to boot the bot, verify Cloudflare ↔ Let's Encrypt ↔ Kestrel reachability via `/healthz`, and exercise the basic HTTP plumbing **before** the Microsoft tenant work was done. The `IsAzureConfigured` flag becomes the gate for wiring the actual Graph SDK.

The `/healthz` endpoint reports the flag in its JSON payload, which is how we'd diagnose later whether the tenant credentials had loaded:

```json
{
  "status": "ok",
  "fqdn": "bot.example.com",
  "azureConfigured": true,
  "tenantId": "<...>",
  "appClientId": "<...>"
}
```

The "is the bot reachable" and "is Azure config loaded" questions decompose cleanly. Each can fail independently and we can diagnose without the other being in the way.

## Phase 2.2: wiring the Graph Communications SDK

Phase 2.2 added three classes and made one design decision worth dwelling on.

### `CommunicationsService`

[`CommunicationsService`](../../bot/src/TeamsAvatarBot.Core/CommunicationsService.cs) is the lifetime-scoped wrapper around the SDK's `ICommunicationsClient`. It builds the client at startup, subscribes to `OnIncoming` events, and exposes the underlying client for the HTTP webhook to forward notifications to.

Building the client looks like this:

```csharp
var graphLogger  = new GraphLogger("TeamsAvatarBot");
var authProvider = new GraphRequestAuthenticationProvider(config, graphLogger);
var notificationUri = new Uri($"https://{config.PublicFqdn}/api/calling");

_client = new CommunicationsClientBuilder("TeamsAvatarBot", config.AppClientId!, graphLogger)
    .SetAuthenticationProvider(authProvider)
    .SetNotificationUrl(notificationUri)
    .SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0"))
    .Build();

_client.Calls().OnIncoming += OnIncoming;
_client.Calls().OnUpdated  += OnUpdated;
```

Three things to note:

1. **The `notificationUri` is the same URL we configured in Azure Bot's messaging endpoint.** That URL is the pivot — Microsoft Graph posts call notifications there, and our server forwards them into the SDK. If the URL doesn't match what's in Azure Bot's config, Graph posts notifications into the void.
2. **`SetServiceBaseUrl` points at `graph.microsoft.com/v1.0`** — global cloud. If we were targeting a sovereign cloud (gov, China), we'd use a different base URL. We're not.
3. **`OnIncoming` fires when Graph posts an incoming-call notification to `/api/calling`.** That's where we attach a per-call handler. The SDK manages the call lifetime; we just hook into the events we care about.

### `GraphRequestAuthenticationProvider`

The Graph Communications SDK's `IRequestAuthenticationProvider` interface has two methods:

- `AuthenticateOutboundRequestAsync(HttpRequestMessage, string tenant)` — stamp an `Authorization: Bearer ...` header onto outbound requests to Graph.
- `ValidateInboundRequestAsync(HttpRequestMessage)` — validate inbound requests from Graph.

Outbound is straightforward MSAL. We use `ConfidentialClientApplicationBuilder` with our app's tenant + client ID + client secret, request a token for the Graph audience (`https://graph.microsoft.com/.default`), and stamp the bearer header:

```csharp
var result = await _msal
    .AcquireTokenForClient(new[] { GraphScope })
    .ExecuteAsync()
    .ConfigureAwait(false);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
```

MSAL caches tokens internally, so we don't pay a token-acquisition round-trip per call.

Inbound is **a stub for now**. Microsoft signs notifications it sends to `/api/calling` with a JWT in the `Authorization` header; that JWT can be validated against Microsoft's OpenID Connect metadata to confirm the request actually came from Graph. Phase 3 hardening will swap in a `JwtSecurityTokenHandler` that does proper validation. Phase 2.2 returns "valid" for everything:

```csharp
public Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
{
    _logger.Verbose("ValidateInboundRequestAsync (skeleton — accepts all)");
    return Task.FromResult(new RequestValidationResult { IsValid = true, TenantId = _tenantId });
}
```

The comment `// skeleton — accepts all` is for the future-us who will read this and wonder why we're not validating. It's tracked as a Phase 3 TODO.

This is the kind of thing that's easy to leave in production by accident. We mitigated by:

1. Adding a structured log line on every inbound validation, so the logs scream "skeleton" if you grep them.
2. Putting the TODO in `docs/decisions.md` so it can't get lost in a code review.
3. Plan to add a Phase 3 acceptance test that fails if the validator returns `IsValid = true` for an unsigned request.

### `CallHandler`

[`CallHandler`](../../bot/src/TeamsAvatarBot.Core/CallHandler.cs) is where the per-call lifecycle hooks live. Phase 2.2 only logs:

```csharp
public Task AttachAsync(ICall call)
{
    _log.LogInformation(
        "attaching to call {CallId} (Phase 2.2 skeleton — no answer yet)",
        call.Id);

    call.OnUpdated += (s, e) =>
        _log.LogInformation("call.OnUpdated {CallId} state={State}",
            call.Id, call.Resource.State);

    call.Participants.OnUpdated += (s, e) =>
    {
        foreach (var p in e.AddedResources)
            _log.LogInformation("participant joined call={CallId} id={Id}", call.Id, p.Id);
        foreach (var p in e.RemovedResources)
            _log.LogInformation("participant left   call={CallId} id={Id}", call.Id, p.Id);
    };

    // Phase 2.3 will:
    //   - answer the call with applicationHostedMedia
    //   - construct LocalMediaSession (audio + video sockets)
    //   - new LiveKitBridge(config, call.Id).StartAsync(localMediaSession, ...)
    return Task.CompletedTask;
}
```

The Phase 2.3 todos are written down in code, not just in a tracker, so anyone reading `CallHandler.cs` knows what's coming next.

## Phase 2.2.5: Kestrel direct on 443, no reverse proxy

This is the sub-phase with the architectural decision worth justifying.

The PRD originally implied an IIS or reverse-proxy fronted bot. Most production .NET deployments on Windows put IIS in front of Kestrel for static-file handling, request filtering, URL rewrite, and a few other reasons. We considered three options for our bot:

1. **IIS + ARR proxy → Kestrel.** Standard pattern. Adds a hop, more moving parts, more cert-management surface (you have to give IIS the cert and configure the binding, which is a different code path than Kestrel's).
2. **Cloudflare Tunnel / ngrok.** Solves NAT/IP exposure if you don't have a directly-routable public IP. But Microsoft Graph won't trust the cert chain through a managed tunnel (it terminates at the tunnel provider, not at our origin). Same problem as Cloudflare's HTTP proxy from article 07.
3. **Kestrel directly on port 443.** Simpler, fewer moving parts. The cert is loaded directly into Kestrel from `certs/iris.pfx`. No IIS. No tunnel. Microsoft Graph talks directly to our process.

We picked option 3. ADR-008 in `docs/decisions.md` captures the reasoning.

The Kestrel binding code:

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    var certPath = Path.Combine(repoRoot, "certs", "iris.pfx");
    var certPwd  = Environment.GetEnvironmentVariable("CERT_PFX_PASSWORD") ?? "changeit";

    if (File.Exists(certPath))
    {
        options.Listen(IPAddress.Any, config.CallingPort, listen =>
        {
            listen.UseHttps(certPath, certPwd);
        });
        Log.Information("Kestrel HTTPS bound on :{Port} using {Cert}", config.CallingPort, certPath);
    }
    else
    {
        options.Listen(IPAddress.Any, 5000);
        Log.Warning(
            "Cert not found at {CertPath}; falling back to plain HTTP :5000. "
            + "Run scripts/install-tls-cert.ps1 to issue the cert.",
            certPath);
    }
});
```

Two things this gets right:

1. **Cert presence is checked at startup.** If `certs/iris.pfx` is missing, we fall back to plain HTTP on port 5000 with a loud warning. That keeps dev iteration fast pre-cert. In prod, the cert will always be there.
2. **`CallingPort` comes from config.** Production = 443. Dev = some higher port if you don't have admin / can't bind 443. Either way, the value lives in `.env` (`BOT_CALLING_PORT`) and isn't hard-coded.

To bind port 443 on Windows, the process needs to be elevated (Administrator) **or** have a URL ACL reservation (`netsh http add urlacl ...`). For v1 we run from an elevated PowerShell session. Phase 3 will swap to a service account with a URL ACL so it can run non-privileged.

### The reachability moment

After 2.2.5 was committed, the validation was a single curl from a non-VM machine:

```
$ curl -i https://bot.example.com/healthz
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Date: Mon, 27 Apr 2026 ... GMT
Server: Kestrel
Transfer-Encoding: chunked

{"status":"ok","fqdn":"bot.example.com","azureConfigured":true,"tenantId":"...","appClientId":"...","timestamp":"2026-04-27T..."}
```

That was the moment. Eight things had to be right simultaneously for that response to come back:

1. DNS for `bot.example.com` resolves to the right IPv4 (article 07).
2. Cloudflare's proxy is off (gray cloud) so the connection goes direct (article 07).
3. The Windows Server VM has a directly-routable public IP — no NAT, no shared VIP.
4. Windows Firewall has an inbound rule for TCP 443.
5. Kestrel is running and bound to port 443 (requires elevated process).
6. The TLS cert in `certs/iris.pfx` is valid, current, and matches the FQDN.
7. The .NET 8 process is running with `BotConfig` loaded from `.env`.
8. The `azureConfigured=true` payload field means the Microsoft tenant credentials (article 06) are loaded.

If any of those are wrong, the curl fails in a different and diagnosable way:

- DNS wrong → `nslookup` would have caught it earlier; curl reports `Could not resolve host`.
- Cloudflare proxy on → curl gets a Cloudflare cert, not ours; TLS error.
- No public IP → curl times out connecting.
- Firewall closed → curl times out (or `connection refused` depending on the rule).
- Kestrel not bound → `connection refused`.
- Cert wrong → TLS error with specific reason (`unable to verify the first certificate` etc).
- Bot not running → `connection refused`.
- Azure unconfigured → 200 OK but `azureConfigured=false`.

This is why we cared about decomposability earlier in the article. Each layer can fail independently and the diagnostic surface tells you which one.

## What "Microsoft Graph can reach our bot" means

The reason `curl https://bot.example.com/healthz` returning 200 is the Phase 2.2.5 acceptance criterion is that **Microsoft Graph reaches our bot the same way curl does**. Same DNS, same TLS handshake, same routing. If a curl from any external machine works, Graph's webhook will work too.

We can't actually test "did Graph reach us" without a call notification, and we don't have a Phase 2.3 yet to receive one. But the `/healthz` curl is the necessary precondition. If that fails, no amount of Graph-side work can succeed. If it passes, we've cleared the network/cert/config gate and Phase 2.3 is just "make the bot do something useful when a notification comes in."

## What's missing

By the end of Phase 2.2.5, the bot:

- ✅ Builds, runs, binds 443, serves `/healthz`
- ✅ Wires the Graph Communications SDK
- ✅ Loads MSAL with tenant credentials and can acquire Graph tokens
- ✅ Subscribes to `OnIncoming` events
- ✅ Logs incoming-call notifications

It does **not yet**:

- Answer calls (Phase 2.3)
- Attach a `LocalMediaSession` (Phase 2.3)
- Receive or send any audio frames (Phase 2.3)
- Receive or send any video frames (Phase 2.3)
- Bridge to LiveKit (Phase 2.3)
- Validate inbound JWTs (Phase 3)
- Render a video tile in the Teams UI (Phase 2.3 risk; PRD §5.4.3 calls this a project-defining unknown)

Phase 2.3 is the LiveKit bridge — the audio/video frame transport between Teams' SRTP streams and the LiveKit room. That's where the project gets technically interesting (and where the PRD's known gotchas, `docs/PHASE_2_3_HANDOFF.md`, become real). It deserves its own articles.

## Three commits, summarized

The commits that produced this article's output:

```
c849bd3 Phase 2.1: scaffold C# .NET 8 solution + ASP.NET Core host
8b49e84 Phase 2.2: wire Graph Communications SDK + CallHandler skeleton
e7df1a5 Phase 2.2.5: bind Kestrel to 443 with Let's Encrypt cert; reachability proven
```

Each commit message body explains the *why*, not just the *what*. That's load-bearing on a project being co-developed with an AI: future sessions need to be able to load context from `git log` without re-asking what was done and why.

## Lessons

1. **Decompose the foundation.** "Bot can be reached on the public internet" decomposes into eight independently testable concerns. Build them so they can fail independently and surface diagnostically. Then debug each one separately.
2. **Make Azure config nullable in early phases.** Letting the bot boot and serve `/healthz` before the M365 tenant work is done means the network and cert work can proceed in parallel with the Microsoft odyssey (article 06). Nullable Azure fields with an `IsAzureConfigured` gate did this for us.
3. **Kestrel directly on 443 beats IIS for app-hosted media bots.** One process, one cert, one binding. Less to misconfigure. Trade-off: the process has to run elevated until you swap to a URL ACL reservation (Phase 3 work).
4. **Don't let auth-skeleton stubs ship to prod silently.** Log loudly on every skeleton call, write the TODO in the ADR log, and plan a Phase 3 test that will fail if the stub is still there. The cost of forgetting "skeleton — accepts all" in production is bad.
5. **The reachability curl is the foundation acceptance test.** Once `curl https://bot.example.com/healthz` returns 200 from the public internet with `azureConfigured=true`, every prerequisite for accepting Graph notifications is in place. Wire that curl into your acceptance criteria explicitly.

---

*Code: [`bot/src/TeamsAvatarBot.Host/Program.cs`](../../bot/src/TeamsAvatarBot.Host/Program.cs), [`bot/src/TeamsAvatarBot.Core/`](../../bot/src/TeamsAvatarBot.Core/). ADR-008 (Kestrel direct deployment) in [`docs/decisions.md`](../decisions.md). Phase 2.3 handoff: [`docs/PHASE_2_3_HANDOFF.md`](../PHASE_2_3_HANDOFF.md). End of the current article series; Phase 2.3 articles to follow once the LiveKit bridge work lands.*
