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
| `find_posts` | Before uploading: posts already in the catalogue and your own (drafts included) by text, control maker or machine maker — so the agent asks "update this one?" instead of creating a duplicate. |
| `replace_post_file` | A new version of an existing post: uploads the archive and updates the card; the old archive is removed and licensed copies refreshed. A published post's new archive goes live at once, without re-moderation. |
| `publish_folder` | Every component in a folder in one import, each its own draft: post files, and subfolders holding a machine schema (xml + osd) or a kit. An optional CSV manifest supplies exact names and fields instead of the AI's guesses. |
| `search_schemas` | Machine schemas in the catalogue by text or machine maker — to link a post to them. |
| `link_post_to_machines` | "Made for" links from a post to the schemas of the machines it targets; the cards then show "made for" / "recommended posts". |
| `list_my_posts` | Your posts with their statuses, optionally filtered — what is still a draft, what is already in the catalogue. |
| `delete_post` | Removes your own draft (or a rejected post) uploaded by mistake. Never a published one — unpublishing is a deliberate act in the cabinet. |
| `generate_description` | An AI description from the card's fields, saved to the card (or only shown with `save=false`). |
| `regenerate_cover` | A new cover: `archive` — the picture inside the component archive (no AI), `ai` — an AI render. |
| `generate_sample_code` / `generate_codes_list` | AI-generated sample NC output and the supported G/M-code list, attached to the card. |

Publishing does **not** submit for moderation by itself: the author looks at what the AI filled
in first.

### Manifest for `publish_folder`

A CSV next to the files; only the `file` column is required, the rest are optional and only
override what they name:

```csv
file,name,controllerManufacturer,controllerSeries,controllerModel,machineManufacturer,machineModel,machineType,numberOfAxes
fanuc-0i.sppx,"Fanuc 0i-MF for Haas VF-2",Fanuc,0i,MF,Haas,VF-2,MILLING,3
siemens.sppx,Siemens 828D for DMG,Siemens,828D,,DMG MORI,,MILLING,3
```

Columns: `file`, `name`, `description`, `controllerManufacturer`, `controllerSeries`,
`controllerModel`, `machineManufacturer`, `machineSeries`, `machineModel`, `machineType`
(MILLING, TURNING, MILL_TURN, WIRE_EDM, LASER, PLASMA, WATERJET, GRINDING, ROBOT, EDM, ROUTER,
SWISS, GAS_PLASMA_LASER, ADDITIVE, OTHER), `numberOfAxes`. An unknown column is an error, not a
silently dropped one.

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
