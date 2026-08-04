# Iris Teams app — manifest + sideload

Everything Teams Admin Center needs to register Iris as a real Teams app, so a user can add Iris to a meeting via the standard **+ Add** participant flow instead of having to hit `/api/join` from a terminal.

## What's in here

| File | What it is |
|---|---|
| `manifest.json` | The Teams app manifest (schema v1.16). Points at the bot's AAD app id, declares calling + video support, lists `bot.example.com` as a valid domain. |
| `build.py` | PEP-723 inline-metadata Python script. Generates `color.png` (192×192) + `outline.png` (32×32) and zips them with the manifest into `iris-teams-app.zip`. |
| `color.png` / `outline.png` | Generated icons (committed so the manifest works without running `build.py` first). |
| `iris-teams-app.zip` | The artifact you upload to Teams. Regenerate any time. |

## Build the package

From the repo root:

```powershell
uv run teams-app/build.py
```

`uv` resolves Pillow on the fly; no `requirements.txt` or pyproject needed. ~2 s on a cold cache. Output: `teams-app/iris-teams-app.zip`.

## Sideload path A — Teams Admin Center (org-wide)

Requires Teams admin role on the tenant. Recommended for the dev tenant where Jeff is the admin.

1. Go to <https://admin.teams.microsoft.com/policies/manage-apps>.
2. Click **Upload new app** → **Upload**.
3. Pick `teams-app/iris-teams-app.zip`.
4. After upload, find "Iris" in the apps list → confirm **Status** is "Allowed."
5. (First time only) Under **Teams apps → Permission policies**, make sure the policy assigned to your user lets custom apps run. Default `Global (Org-wide default)` does this in dev tenants.

Once allowed, any user in the tenant can add Iris to a meeting from the Teams meeting UI: **People** panel → **+ Add people** → search "Iris" → Add.

## Sideload path B — Teams client (per-user, faster for dev)

Requires the tenant policy **Custom app setup → Upload custom apps** to be On for your user (it's on by default in the M365 E3 trial tenant).

1. Open Teams desktop or web.
2. Left sidebar → **Apps** → **Manage your apps** → **Upload an app**.
3. Click **Upload a custom app**.
4. Pick `teams-app/iris-teams-app.zip`.
5. Click **Add** when the install dialog appears.

The app is now installed for your user only — meetings you join can add Iris via the **+ Add** flow.

## Adding Iris to a meeting (after sideload)

1. Start or join the Teams meeting.
2. **People** panel (top right) → search box → type "Iris".
3. The Iris bot shows up under "In this meeting" suggestions → **Add**.

Teams will send a calling notification to our `/api/calling` webhook (validated against the Bot Framework signing keys via Phase 3's JWT validator). The bot answers with audio + video sockets exactly the way `/api/join` does today.

The `/api/join` endpoint stays available as a fallback for headless / scripted joins and for the e2e test runbook.

## Updating the manifest

The bot's AAD app id is hardcoded in two places in `manifest.json`:

- top-level `id` field
- `bots[0].botId`

If you ever rotate the bot's app registration (Phase 3 secrets rotation or after a Microsoft incident), update both fields and re-run `build.py`. The version field follows semver — bump `version` when uploading a new package, otherwise Teams may skip the update.

## Troubleshooting

**"App validation failed: file size too small"** on icon upload — the outline icon is intentionally minimal (a single white glyph on transparent). Teams sometimes flags this. Re-run `build.py`; if it persists, edit `_draw_glyph` in `build.py` to thicken the strokes.

**"App not approved for this organization"** — admin path: Teams Admin Center → Manage apps → find Iris → set Status to "Allowed."

**"Custom apps cannot be uploaded"** — per-user path: tenant policy blocks it. Use admin path instead, or have the tenant admin enable custom-app uploads under setup policies.

**Iris doesn't show up in the +Add search** — wait 1–2 minutes after upload for the tenant cache to refresh; try logging out + back in to Teams.

**Iris is added but never speaks** — that's the same Phase 2.3 e2e bug surface. Tail the bot logs at `dotnet run --project bot/src/TeamsAvatarBot.Host` and look at the call lifecycle: are we reaching `state=Established`? See `docs/PHASE_2_3_HANDOFF.md` §"How to start the next session" for the diagnostic flow.
