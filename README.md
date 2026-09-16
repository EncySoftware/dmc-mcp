# dmc-mcp

MCP server for publishing post-processors to [Digital Machine Center](https://dmc.encycam.com)
from Cursor or Claude Code — the DMC counterpart of
[ency-extension-mcp](https://github.com/EncySoftware/ency-extension-mcp).

| Tool | What it does |
|---|---|
| `publish_post` | Uploads a `.sppx` / `.dll` / `.stnci` / `.zip` as a draft. The backend unpacks the archive; AI fills in the description, control, machine and cover. Returns the card link. |
| `update_post` | Fixes what the AI guessed wrong: name, description, control, machine, machine type, axes. |
| `submit_post` | Sends the draft for moderation. If something is missing, it says what. |
| `check_post_status` | Draft / under review / published, with the link. |

Publishing does **not** submit for moderation by itself: the author looks at what the AI filled
in first.

## Install (Cursor, Claude Code)

Requires the .NET 8 SDK.

```bash
dotnet tool install -g EncySoftware.DmcMcp
dmc-mcp setup
```

Until the package is on nuget.org, install from the `.nupkg` attached to the
[latest release](https://github.com/EncySoftware/dmc-mcp/releases):
`dotnet tool install -g EncySoftware.DmcMcp --add-source <folder with the .nupkg>`.

`setup` writes the `dmc` server into `~/.cursor/mcp.json` (merging with your other servers),
registers it in Claude Code when its CLI is installed, and signs you in. Restart the editor
afterwards. `--no-login` skips the sign-in.

Sign-in — `dmc-mcp login` — opens the sign-in page in your browser (licsys account); the tool keeps
only a refresh token in `%APPDATA%\dmc-mcp\auth.json` and never sees the password. `--password` is
the fallback for a machine without a browser. A publisher role in DMC is required.

By hand instead of `setup` — `login`, plus in `~/.cursor/mcp.json`:

```json
{ "mcpServers": { "dmc": { "command": "dmc-mcp" } } }
```

## What it looks like

1. *"publish the post C:\posts\fanuc-0i.sppx, it's Fanuc 0i-MF for a Haas VF-2"* → `publish_post` —
   a draft link and what the AI filled in.
2. *"the control is Siemens 828D, not Fanuc"* → `update_post`.
3. *"submit it for review"* → `submit_post`.
4. *"is it published?"* → `check_post_status`.

## Settings

| Variable | Purpose |
|---|---|
| `DMC_API` | another API address (staging); default from `src/Brand.cs` |
| `DMC_SITE` | site address for card links |
| `DMC_TOKEN` | a ready token instead of the stored sign-in (debugging, CI) |
| `DMC_CLIENT_ID`, `DMC_BROWSER_CLIENT_ID`, `DMC_KEYCLOAK_TOKEN_ENDPOINT` | another client or Keycloak |

Everything that ties the tool to DMC lives in one file — `src/Brand.cs`.

## Development

```bash
dotnet test tests/DmcMcp.Tests.csproj   # logic; DMC is faked, no network
dotnet run --project src                 # stdio server, talk JSON-RPC to it
dotnet pack src -c Release -o pkg        # the tool's .nupkg
```

## Releases

Push a version tag (`v0.1.1`): `publish-tool.yml` runs the tests, packs the tool with that version,
attaches the `.nupkg` to the GitHub release and publishes it to nuget.org through trusted
publishing — no key is stored in this repository.
