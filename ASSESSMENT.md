---
repo: jeffrmanley/teams-avatar-agent
assessed: 2026-10
model: claude-opus-5-5
what: Research capstone that puts a real-time AI avatar ("Iris": Claude + Deepgram + ElevenLabs + HeyGen/Beyond Presence) into a Microsoft Teams meeting as a separate participant, using a C# Graph Communications media bot bridged to a Python LiveKit Agents app.
stack: [C#, .NET 8, ASP.NET Core, Microsoft Graph Communications SDK, Skype.Bots.Media, LiveKit (.NET + Python), Python 3.12, livekit-agents, FFmpeg, PowerShell]
last_activity: 2026-08-04
builds: yes
tests: fail
completeness: 3
code_health: 4
security: 2
value: 3
effort_to_ship: L
verdict: ARCHIVE
merge_into:
top_risks: [inbound Graph webhook unauthenticated by default, /api/join open if BOT_DISPATCH_SECRET unset, 41 Python + 3 high NuGet advisories (pinned on purpose), live audio/video of meeting participants (privacy/consent), Windows-only media stack + Teams licensing blocker]
next_actions: [Set the GitHub repo to archived and pin the README warning (already written) so nobody deploys it, Publish the 13-article build journal as a blog or LinkedIn series since it is the main reusable asset, If you restart it: confirm the inbound JWT OIDC URL, make inbound JWT and the dispatch secret fail closed, and upgrade all dependencies before going further]
---

# Assessment: teams-avatar-agent (October 2026)

## 1. What it is

"Iris" is an AI avatar that joins a Teams meeting as its own roster entry, announces itself (EU AI Act Art. 50), listens, and talks back with a lip-synced photoreal video tile. There are two halves, and a LiveKit room sits between them:

- `bot/`: a C#/.NET 8 Graph Communications application-hosted media bot. It bridges Teams SRTP audio/video to and from a LiveKit room. Outbound video goes through an FFmpeg H.264 subprocess plus a NAL access-unit parser. About 2.0k LOC.
- `agent/`: a Python LiveKit Agents worker running Deepgram Nova-3 (speech-to-text), Claude Sonnet 4.5 (LLM), ElevenLabs Flash v2.5 (text-to-speech) and a switchable avatar vendor (Beyond Presence or HeyGen LiveAvatar). About 300 LOC.
- Supporting material: a 440-line PRD, ADRs, runbooks, a vendor cost sheet, PowerShell ops scripts and a 13-article build journal (`docs/articles/`).

## 2. Status

- **Git history:** one commit (`0390e76`, 2026-08-04, Jeff Manley) titled "Initial public release". The history was squashed, so the earlier commits mentioned in the docs (`0990d83`, `c12d335`) are not in this repo. The only branches are `main` and this assessment branch. There is no CI (`.github/` is absent).
- **What the README says about itself:** it is an *archived research capstone* and should not be deployed. The dev environment was torn down in June 2026, and dependencies were left at their mid-2026 versions on purpose.
- **Phase status:**
  - Phases 0–2.3 were validated live: the bot joins a real meeting and has two-way audio and video, with roughly 1–1.5 s round-trip latency.
  - Phase 2.4 (the Teams "+Add" sideload flow) is blocked by Teams licensing.
  - Phase 3 (hardening, metrics aggregation, idle behaviour) was never started.
- **Stale docstring:** `agent/agent.py`'s module docstring still names Cartesia TTS, but the code uses ElevenLabs.

## 3. Build & tests

All of this ran on Linux in this container. The .NET 8 SDK (8.0.425) was installed into a scratch directory.

| Step | Command | Result |
|---|---|---|
| Python deps | `uv sync --frozen --all-extras` (in `agent/`) | OK |
| Python tests | `uv run --frozen pytest -q` | **20 passed** (4.1 s) |
| Python lint | `ruff check .` | 2 errors, both auto-fixable |
| .NET build | `dotnet build bot/TeamsAvatarBot.sln` | **Build succeeded**, 0 errors, 4 warnings |
| .NET tests | `dotnet test --no-build` | **23 passed, 1 failed** |

**The one .NET failure:** `HealthCheckTests.Healthz_returns_200_with_config`. `Program.cs:117` throws `FileNotFoundException` because it cannot find a `.env` file walking up from the test's `bin/` directory.

- **Cause:** a test-isolation and design bug. Host startup hard-requires a `.env` file, so the test fails on any clean checkout or CI runner. It is not a logic failure.
- **Not tested:** live meeting join and real media. These need a Windows Server host, a public IP, a TLS certificate, an M365 tenant and keys for six vendors. The build passing on Linux does not mean the media stack runs there: `Skype.Bots.Media` is Windows-only at runtime.

## 4. Security findings

**Tools run:**
- `gitleaks git .` (v8.21.2): no leaks in the one commit.
- `osv-scanner -r .` (v1.9.2): "No issues found". This result is misleading: that version can't parse `uv.lock`, and it ignores PackageReference-only .NET projects, so in practice it scanned nothing.
- `pip-audit -r <uv export>`: 41 advisories across 8 packages.
- `dotnet list package --vulnerable --include-transitive`: 3 High.

All tenant IDs and hostnames in the repo are placeholders, as the README says.

| # | Sev | Location | Finding |
|---|---|---|---|
| 1 | **High** | `bot/src/TeamsAvatarBot.Core/GraphRequestAuthenticationProvider.cs:60,98` | Inbound JWT validation on `POST /api/calling` is **off by default** and returns `IsValid=true` for any request. Anyone who can reach the bot's public 443 can send forged call notifications to the Graph SDK. The author documents this as a known gap (article 13). |
| 2 | **High** | `bot/src/TeamsAvatarBot.Core/GraphInboundJwtValidator.cs:29-30` | Even with validation turned on, the OIDC metadata URL is the Bot Framework one, which the author confirmed is wrong (it 401'd every notification). The probable correct URL (`api.aps.skype.com`) was never verified, so there is no working authentication path for the webhook. |
| 3 | **High** | `bot/src/TeamsAvatarBot.Host/Controllers/CallingController.cs:86` | `GET /api/join` **fails open**: if `BOT_DISPATCH_SECRET` is unset, anyone can make the bot dial any Teams meeting URL. That spends the tenant's identity, vendor credits (LLM, TTS and avatar per minute) and puts an AI participant into third-party meetings. `.env.example` doesn't list the variable. It is also a GET with side effects. |
| 4 | **High** (dependencies) | `agent/uv.lock` | pip-audit found 41 advisories. pyjwt 2.12.1 has 15 (fixed in ≥2.15.0), aiohttp 3.13.5 has 14, and urllib3 2.6.3 has 5. anyio, transformers, fsspec, idna and multidict have the rest. These are knowingly pinned, per the README. |
| 5 | **High** (dependencies) | `bot/**.csproj` (transitive) | Three packages have High advisories: Microsoft.Kiota.Abstractions 1.17.1 (GHSA-7j59-v9qr-6fq9), SQLitePCLRaw.lib.e_sqlite3 2.1.2 (GHSA-2m69-gcr7-jv3q) and System.Text.RegularExpressions 4.3.0 (GHSA-cmhx-cq75-c4mj). The direct dependency `Microsoft.Skype.Bots.Media 1.33.0.258-preview` is a preview build. |
| 6 | Medium | `bot/src/TeamsAvatarBot.Host/Program.cs:24`, `scripts/install-tls-cert.ps1:58` | The TLS PFX password is hardcoded to `changeit` as a fallback. The private key on disk is effectively unprotected. |
| 7 | Medium | `bot/src/TeamsAvatarBot.Host/Controllers/HealthController.cs:17` | `/healthz` is unauthenticated and returns the tenant ID, app client ID, public IP and LiveKit URL. That is reconnaissance data for findings 1 and 3. |
| 8 | Medium | Whole system (privacy) | The bot processes live audio and video of meeting participants and sends it to Deepgram, Anthropic, ElevenLabs and the avatar vendor. Apart from the Art. 50 spoken disclosure there is no consent flow, retention policy or DPA mapping. This would block any commercial deployment. |
| 9 | Low | `bot/src/TeamsAvatarBot.Host/Controllers/CallingController.cs:89` | The dispatch secret is compared with `string.Equals`, which is not constant-time. That allows a timing side channel, though it is minor over the network. |
| 10 | Low | `bot/src/TeamsAvatarBot.Host/Controllers/CallingController.cs:69,108,113` | Exception messages from the SDK and Graph are returned to callers in 500/400 bodies. |
| 11 | Low | `bot/src/TeamsAvatarBot.Host/Program.cs:61` | If the certificate is missing, the bot silently falls back to plain HTTP on `0.0.0.0:5000`. |
| 12 | Low | `bot/src/TeamsAvatarBot.Host/TeamsAvatarBot.Host.csproj:11` | The floating version `8.*` makes builds non-reproducible. There is no NuGet lock file. |
| 13 | Info | repo | There is no CI, so there are no workflow or supply-chain risks to report. There is also no automated scanning. |

**Checked with nothing found:**
- No `eval`/`exec` or unsafe deserialization.
- FFmpeg arguments are built from integers only, so there is no command injection.
- `TeamsMeetingJoinUrl.Parse` checks the host.
- LiveKit tokens are minted server-side with scoped grants.
- Turn logs hold metrics only, not transcripts.

## 5. Code quality & architecture

**Architecture.** Using a LiveKit room as the seam is the right call. The Teams side and the AI side can each be replaced without touching the other, and the avatar vendor is swapped with an environment variable. The C# code is cleanly layered (Core / LiveKitBridge / Host) and has useful comments that explain *why*. The trickier pieces (H.264 AU parser, join-URL parser, JWT validator) have focused unit tests.

**Weaknesses:**
- A single 581-line `LiveKitBridge.cs`.
- Configuration lives in environment variables and is read ad hoc (`Environment.GetEnvironmentVariable` inside controllers).
- Host startup hard-requires a `.env` file, which breaks tests and containers.
- One bot process handles one call, and there is no scale-out design.
- The deployment is Windows Server on a single VM.
- There is no CI.

**Documentation is excellent and is the strongest part of the repo:** the PRD, ADRs, runbooks, a cost model and an honest 13-article build journal.

**Distance to shippable: far.** Phase 3 hardening was never started. The webhook authentication is unresolved. The Teams "+Add" flow is blocked by licensing (it needs a Resource Account workaround). Running it needs a Windows host with a public IP. Every dependency needs upgrading.

## 6. Value & opportunity

**Problem:** putting an AI agent into Teams meetings as a first-class *audio-visual* participant, not just a chat bot or a transcription bot. This is real but niche. Microsoft (Copilot in Teams, Facilitator agents), Recall.ai-style meeting-bot APIs and avatar vendors moving into meeting integrations are all closing in on it. A solo Teams media bot is expensive to run and support:

- a per-tenant M365 and Graph admin consent;
- Windows media hosts;
- per-minute avatar, TTS and STT costs (see `docs/vendor-costs.md`).

**Who might pay:** enterprise L&D or sales enablement (AI role-play partners in Teams), and possibly agencies wanting an AI "presenter". It would only sell as a hardened, multi-tenant service, which is an XL effort with heavy compliance work (Art. 50, GDPR, recording consent).

**Real value today is knowledge and content:**
- the build journal;
- the Teams media-bot ↔ LiveKit bridge pattern (rare, hard-won knowledge);
- `docs/proposals/upstream-encoded-frame.md` (a possible upstream LiveKit contribution).

**Overlap with Jeff's other repos:** I could not check this. The session's GitHub access was scoped to this repo only, so I can't name overlapping repos. Any of Jeff's other LiveKit, voice-agent or avatar projects would be the natural home for the `agent/` half.

## 7. Verdict & next actions

**Verdict: ARCHIVE.** The author already declared it a finished research capstone, and the code matches that: it proved the concept and stops before hardening. Shipping it means hardening, upgrading every dependency, a multi-tenant redesign and compliance work, all against fast-moving first-party competition.

1. **Archive the GitHub repo** (read-only). The README warning already covers "do not deploy". Optionally add a one-line note that inbound webhook authentication is disabled by default.
2. **Turn the docs into content.** The 13-article journal is publish-ready. It is good thought-leadership for withJZ, and the cheapest way to get value from this work.
3. **If it is ever revived:**
   - verify the Graph Communications OIDC URL against a captured header;
   - make inbound JWT validation and `BOT_DISPATCH_SECRET` fail closed;
   - move `/api/join` to POST;
   - drop the `changeit` fallback;
   - remove `.env` from the startup hard requirements and add CI;
   - upgrade every dependency (pyjwt ≥2.15, aiohttp ≥3.14.3, urllib3 ≥2.8, and the NuGet transitive packages).

### Assumptions and notes
- The brief asked for branch `claude/assessment-2026-10`. This session's tooling assigned `claude/assessment-2026-10-vxjp80` as the push branch, so the assessment was committed there.
- The CVE counts come from the pip-audit and NuGet advisory databases as of 2026-10-11. The README's "43 CVEs across 4 packages" figure dates from an earlier scan.
- `last_activity` is the date of the single commit. Docs show development ran from about March to June 2026, before the history was squashed.
