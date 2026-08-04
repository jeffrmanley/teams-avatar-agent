# Cloudflare DNS and TLS for a Graph Communications bot

*Article 07 of the Iris series.*

**Date:** 2026-04-27
**Author:** Jeff Manley
**TL;DR:** Cloudflare's HTTP proxy (the "orange cloud") doesn't work for a Microsoft Graph Communications media bot. It breaks the cert chain Graph expects, and Cloudflare's proxy doesn't pass UDP at all — and Teams media is SRTP over UDP. The right setup is **DNS-only mode** (gray cloud) with a Let's Encrypt cert obtained via **Posh-ACME's DNS-01 challenge**, using a scoped Cloudflare API token. Cert in three minutes, port 80 never opened.

---

## The setup

The bot host needs:

- A **public DNS name** with a valid TLS cert that Microsoft Graph trusts.
- **Direct, addressable public IPv4** that Teams media servers can send SRTP packets to.
- **TCP 443 inbound** for HTTPS signaling from Graph.
- **A configurable UDP port range** for media (`8445` default, adjustable).

We chose `bot.example.com` as the FQDN, hosted on a Windows Server 2025 VM at Interserver with a directly-routable public IPv4 (no shared VIP). DNS for `example.com` is on Cloudflare.

## The proxy decision: DNS-only, not HTTP-proxied

Cloudflare offers two modes for any A or CNAME record:

- **Proxied (orange cloud):** Cloudflare's edge terminates incoming HTTP/HTTPS connections, applies their WAF, caches, optionally hides the origin IP, then proxies to your origin. The user's browser sees Cloudflare's IP, not yours.
- **DNS only (gray cloud):** Cloudflare just resolves the name to your IP. The connection goes directly to your origin. Cloudflare adds nothing; you get none of their proxy features.

For 99% of websites the proxied mode is the right answer. For a Graph Communications media bot it's wrong, for two specific reasons.

### Reason 1: cert chain trust

When Cloudflare proxies HTTPS traffic, **it terminates TLS at the edge**. The browser (or in our case, Microsoft Graph) negotiates TLS with Cloudflare's servers using a Cloudflare-issued cert. Cloudflare then opens a separate TLS connection to your origin using its own cert.

This is fine for browsers, which trust the public CA Cloudflare uses. It's not fine for Microsoft Graph, which validates the cert chain against the messaging endpoint configured in your Azure Bot resource. The cert that Graph sees needs to be issued for `bot.example.com` and chain to a public root CA. With Cloudflare proxy on, Graph would see Cloudflare's cert chain, presented for an SNI it can validate, but the **origin connection from Cloudflare to our box** is what carries the actual call data — and the cert there has to be valid and trusted independently.

You can configure Cloudflare's "Full (strict)" mode to require a valid cert at the origin, which addresses *part* of the problem. The deeper issue is that even with strict mode, you're now in a "two cert chains, both valid" world, and any disagreement between them — different SAN, different expiry, mismatched `notBefore`, anything — produces hard-to-debug Graph signaling failures.

The simpler path is to bypass the proxy entirely. The cert at our Kestrel server is the only cert in play. Graph validates it directly. There's nothing to mismatch.

### Reason 2: Cloudflare doesn't pass UDP

This is the deal-breaker even if you could solve cert chain trust.

Teams media — voice and video — is **SRTP over UDP**. Microsoft's media servers send UDP packets directly to your bot's IP on the configured media port (default 8445). The bot receives those packets via `Microsoft.Skype.Bots.Media`'s native sockets.

**Cloudflare's HTTP proxy doesn't pass arbitrary UDP traffic.** Cloudflare's edge is HTTP/HTTPS-aware (and now also QUIC-aware for HTTP/3, but that's still HTTP). For arbitrary UDP — anything that isn't HTTP-shaped — Cloudflare offers a separate product called **Spectrum** ($$$, available on Enterprise plans). On the standard Cloudflare plans we'd be on, UDP packets to a proxied A record simply don't make it through.

So even if Reason 1 didn't kill us, Reason 2 would. There's no way to run Teams media through Cloudflare's standard HTTP proxy.

### The setting that matters

In the Cloudflare DNS dashboard, you set this when you add the A record:

- Type: A
- Name: `iris`
- IPv4: `<your bot host's public IPv4>` (we use `203.0.113.10`, the Interserver VM)
- **Proxy status: DNS only (gray cloud)**
- TTL: Auto

The "DNS only" status is the only thing in this whole article that costs nothing and that absolutely must be right. Most other gotchas in this project are recoverable. This one would let you spin for days if you set it wrong.

## TLS: Let's Encrypt via Posh-ACME, DNS-01 challenge

The PRD originally specced **win-acme** for cert issuance. Win-acme is a perfectly fine tool, but it defaults to the **HTTP-01 challenge**, which requires inbound port 80 to be open while the cert is being issued. ACME spins up a temporary listener, Let's Encrypt validates it, and the cert is issued.

We didn't want to open port 80 inbound. The bot doesn't need it for anything else (HTTPS-only signaling), and every additional firewall hole is a net negative.

The alternative is the **DNS-01 challenge**, where ACME proves you control the domain by writing a TXT record at `_acme-challenge.<your-domain>`. No HTTP listener required, no port 80, no firewall change. Just a script that talks to your DNS provider's API.

For DNS-01 against Cloudflare, the right tool on Windows is **Posh-ACME** — a PowerShell module with first-class Cloudflare support and a nice scriptable API. ADR-006 in `docs/decisions.md` documents the switch from win-acme to Posh-ACME.

### The Cloudflare API token

The key piece is a **scoped API token**, not the global API key. Cloudflare's "Edit zone DNS" template gives you exactly the scope you need:

- Permissions: `Zone — DNS — Edit`
- Zone Resources: Include → Specific zone → `<your domain>`
- Client IP filter: empty
- TTL: default

You create this in Cloudflare → My Profile → API Tokens → Create Token → "Edit zone DNS" template. Copy the token once (it's shown once and never again) into `.env` as `CLOUDFLARE_API_TOKEN`.

Why scoped, not global: the global API key has full account access. Anyone with it could change your billing email, transfer your domains, or delete your zones. The scoped token is the principle of least privilege — it can write a TXT record to the one zone you specified, and that's it. If the token leaks, you blow it away and create a new one without panic.

### The cert script

[`scripts/install-tls-cert.ps1`](../../scripts/install-tls-cert.ps1) is the issuance script. It's idempotent — running it on a host that already has a current cert (more than 30 days from expiry) just exits zero with no work. Reading from `.env`:

- `BOT_PUBLIC_FQDN` — the FQDN to issue for, e.g. `bot.example.com`
- `CLOUDFLARE_API_TOKEN` — the scoped token from above
- `CERT_CONTACT_EMAIL` — falls back to `you@example.com`
- `CERT_PFX_PASSWORD` — falls back to `changeit` if not set

The relevant flow:

```powershell
# Pick LE prod (or staging if -Staging flag — useful for debugging the
# DNS-01 plugin without exhausting LE's prod rate limits)
Set-PAServer LE_PROD

# Make sure we have an ACME account
$account = Get-PAAccount
if (-not $account) {
    New-PAAccount -Contact $contact -AcceptTOS | Out-Null
}

# Cloudflare DNS plugin args - secure-stringed token
$pluginArgs = @{
    CFToken = (ConvertTo-SecureString $token -AsPlainText -Force)
}

# Request cert via DNS-01
$cert = New-PACertificate `
    -Domain $fqdn `
    -Plugin Cloudflare `
    -PluginArgs $pluginArgs `
    -PfxPass $pfxPwd `
    -AcceptTOS `
    -Contact $contact `
    -Force

# Posh-ACME drops the artifacts in its own store; copy them into ./certs/
Copy-Item $cert.PfxFile       certs/iris.pfx       -Force
Copy-Item $cert.FullChainFile certs/iris.fullchain.pem -Force
Copy-Item $cert.KeyFile       certs/iris.key.pem   -Force
```

Three artifacts get produced:

- `certs/iris.pfx` — Windows-native PFX bundle with cert + key. This is what Kestrel reads.
- `certs/iris.fullchain.pem` — full cert chain in PEM, for non-Windows tooling.
- `certs/iris.key.pem` — private key in PEM.

The cert is valid for 90 days. The script's idempotent skip-if-fresh logic means you can wire it into Task Scheduler to run weekly; it'll only do real work once a quarter.

### What renewal looks like

The renewal flow is identical to issuance — `New-PACertificate` re-runs the DNS-01 challenge against Cloudflare, gets a new cert, and copies the artifacts. The script's 30-day-before-expiry check means a weekly Task Scheduler run will catch the renewal window with plenty of margin.

For Phase 3 we'll wire this into a scheduled task with a logging hook, so a renewal failure produces an actionable alert rather than a silent expiry. For Phase 0/2.0 we just made sure the script worked and noted the next renewal date in `docs/decisions.md` (cert valid through 2026-07-25 at the time we issued).

## Putting it together

After DNS and TLS are configured, the bot host has:

| Resource | State |
|---|---|
| `bot.example.com` A record | Points to `203.0.113.10` (Interserver public IP), Cloudflare proxy **off** (gray cloud) |
| Cloudflare API token | Scoped: `Zone:DNS:Edit` for `example.com` only |
| `certs/iris.pfx` | Let's Encrypt cert, valid 90 days, issued via DNS-01 |
| Windows Firewall | TCP 443 inbound allowed; TCP 80 closed; UDP 8445 will be opened in Phase 2.3 for media |
| Cert renewal | Manual for now; Posh-ACME script idempotent and scheduled in Phase 3 |

Verifying from outside, before the bot was even running:

```bash
$ openssl s_client -connect bot.example.com:443 -servername bot.example.com 2>/dev/null </dev/null | openssl x509 -noout -subject -issuer -dates
subject=CN = bot.example.com
issuer=C = US, O = Let's Encrypt, CN = R3
notBefore=Apr 27 ... 2026 GMT
notAfter=Jul 25 ... 2026 GMT
```

That's the cert Microsoft Graph will validate. Single cert, single chain, single source of truth.

## Why this took less time than M365

It's worth noting how much faster this article's work went compared to article 06's. The Microsoft tenant odyssey took about a workday end-to-end. The DNS + TLS work took about an hour, including writing the script, debugging the first DNS-01 attempt (the token initially had `Zone:Read` instead of `Zone:DNS:Edit` — a minute to fix), and verifying.

The reason is that Cloudflare's product is **boring** in the best sense. The dashboard does what it says. The API is documented. Posh-ACME is mature. There's no fraud detection, no plan-tier matrix, no "your tenant might need a support ticket." It's infrastructure plumbing, and the people building infrastructure plumbing have had a long time to get the rough edges off.

Microsoft's tenant story is comparatively the opposite. The product surface is huge, the plan SKUs overlap and contradict, the failure modes are subtle, and the docs assume you've already navigated the product matrix. They're not technically harder problems — they're harder *to know what to do about*.

## Lessons

1. **For Graph Communications media bots, Cloudflare proxy must be off (gray cloud).** Two reasons stack here: the HTTP proxy breaks the cert chain Graph expects, and Cloudflare doesn't pass arbitrary UDP. Either reason alone is a deal-breaker.
2. **Use DNS-01 over HTTP-01 for cert issuance** if you can. No port 80 inbound, no temporary listener, fully scriptable from any DNS provider with an API.
3. **Use a scoped API token, not the global key.** Cloudflare's "Edit zone DNS" template gives Posh-ACME the exact permission it needs and nothing more. If the token leaks, the blast radius is the TXT records on one zone.
4. **Posh-ACME on Windows is the right ACME client for DNS-01** in 2026. Mature, scriptable, idempotent, supports every major DNS provider. Easier than win-acme for any non-HTTP-01 path.
5. **Bake idempotence into the cert script from day one.** Re-running the script should be free if a current cert exists. That property is what lets you put it in a scheduled task without worrying about side effects.

---

*Script: [`scripts/install-tls-cert.ps1`](../../scripts/install-tls-cert.ps1). ADR-006 (Posh-ACME with DNS-01) in [`docs/decisions.md`](../decisions.md). Next: [the C# bot foundation](08-csharp-bot-foundation.md), where the cert from this article gets bound to Kestrel directly on port 443.*
