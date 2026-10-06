# dmc-mcp

MCP server for publishing post-processors to [Digital Machine Center](https://dmc.encycam.com)
from Cursor, Claude Code or Codex — the DMC counterpart of
[ency-extension-mcp](https://github.com/EncySoftware/ency-extension-mcp).

| Tool | What it does |
|---|---|
| `publish_post` | Uploads a `.sppx` / `.dll` / `.stnci` / `.zip` as a draft. The backend unpacks the archive; AI fills in the description, control, machine and cover. Looks for similar names in DMC first and stops if it finds any (`force=true` uploads anyway). Returns the card link. |
| `check_import` | The result of an import that `publish_post` / `publish_folder` did not wait out — by the `importId` they returned. |
| `audit_drafts` | Your drafts against the moderation rules: ready, or what each one still lacks (name, machine maker, machine type, archive); no cover / no description as a note. |
| `submit_drafts` | Sends several drafts for moderation at once — ids, or `ALL` for every ready draft; the rest are listed with what they lack. |
| `describe_post` | The whole card: full description, control and machine, cover, files, price and trial, links — to judge what the AI wrote. |
| `inspect_archive` | What is inside a component archive — locally, no server, no AI: machine name, axes and travels, equipment, picture, posts, controls mentioned. The facts an agent needs to write the description itself. |
| `set_cover` | Your own cover picture — from the author or from the agent — uploaded and set on the card. No server AI involved. |
| `update_post` | Fixes what the AI guessed wrong on any card — post, schema or kit: name, description, control, machine, machine type, axes, travels X/Y/Z (mm). |
| `submit_post` | Sends the draft for moderation. If something is missing, it says what. |
| `check_post_status` | Draft / under review / published, with the link. |
| `find_posts` | Before uploading: components already in the catalogue and your own (drafts included) by text, control maker or machine maker — so the agent asks "update this one?" instead of creating a duplicate. Posts by default; `contentType` = MACHINE_SCHEMA, INTERPRETER, DIGITAL_MACHINE_KIT or ANY. |
| `replace_post_file` | A new version of an existing post: uploads the archive and updates the card; the old archive is removed and licensed copies refreshed. A published post's new archive goes live at once, without re-moderation. |
| `publish_folder` | Every component in a folder in one import, each its own draft: post files, and subfolders holding a machine schema (xml + osd) or a kit. An optional CSV manifest supplies exact names and fields instead of the AI's guesses; `nameHint` / `descriptionHint` steer the AI. `dryRun=true` shows the plan and uploads nothing; similar names in DMC stop the upload unless `force=true`. |
| `search_schemas` | Machine schemas in the catalogue by text or machine maker — to link a post to them. |
| `link_post_to_machines` | "Made for" links from a post to the schemas of the machines it targets; the cards then show "made for" / "recommended posts". |
| `list_my_posts` | Your posts with their statuses, optionally filtered by status — what is still a draft, what is already in the catalogue. `contentType` = ANY lists every component you own, with its type. |
| `delete_post` | Removes your own draft (or a rejected post) uploaded by mistake. Never a published one — unpublishing is a deliberate act in the cabinet. |
| `generate_description` | An AI description from the card's fields, saved to the card (or only shown with `save=false`). |
| `regenerate_cover` | A new cover: `archive` — the picture inside the component archive (no AI), `ai` — an AI render. |
| `generate_sample_code` / `generate_codes_list` | AI-generated sample NC output and the supported G/M-code list, attached to the card. |

Publishing does **not** submit for moderation by itself: the author looks at what the AI filled
in first.

## Two ways to fill in a card

**Server AI (default).** `publish_post` / `publish_folder` with `ai=true`: the backend reads the
archive and fills the description, control, machine, axes and cover with its own AI — the
company's keys, one house style, nothing for the author to write.

**Your own agent.** `publish_post(..., ai=false)` uploads the archive and nothing more. Then
`inspect_archive(file)` hands the agent the facts — machine name, axes and travels, equipment,
controls mentioned, whether there is a picture inside — and the agent writes the description
itself, sets the fields with `update_post`, and puts a cover with `set_cover` (its own picture)
or `regenerate_cover(source: "archive")` (the picture from the archive, no AI). The author sees
the text before it is saved, and pays for no AI but their own.

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
SWISS, GAS_PLASMA_LASER, ADDITIVE, OTHER), `numberOfAxes`, `travelXMm`, `travelYMm`, `travelZMm`
(mm, decimal point — the comma separates columns). An unknown column is an error, not a silently
dropped one.

## Install (Cursor, Claude Code, Codex)

Requires the .NET 8 SDK.

```bash
dotnet tool install -g EncySoftware.DmcMcp
dmc-mcp setup
```

Until the package is on nuget.org, install from the `.nupkg` attached to the
[latest release](https://github.com/EncySoftware/dmc-mcp/releases):
`dotnet tool install -g EncySoftware.DmcMcp --add-source <folder with the .nupkg>`.

`setup` writes the `dmc` server into `~/.cursor/mcp.json` (merging with your other servers),
registers it in Claude Code when its CLI is installed, adds it to Codex's `config.toml` when Codex
is on the machine (`~/.codex`, or `CODEX_HOME`; the CLI, the IDE extension and the app share that
file, and nothing else in it is touched), and signs you in. Restart the editor afterwards.
`--no-login` skips the sign-in. One sign-in serves every editor.

Sign-in — `dmc-mcp login` — opens the sign-in page in your browser (licsys account); the tool keeps
only a refresh token in `%APPDATA%\dmc-mcp\auth.json` and never sees the password. `--password` is
the fallback for a machine without a browser. A publisher role in DMC is required.

Something not working? `dmc-mcp doctor` checks the sign-in, whether DMC accepts the token and
grants the publisher role, and whether the server is registered in Cursor, Claude Code and Codex —
one line per check. `setup` and `doctor` also mention a newer version on nuget.org when there is one.

Long imports report progress into the editor (MCP progress notifications) while they run.

By hand instead of `setup` — `login`, plus in `~/.cursor/mcp.json`:

```json
{ "mcpServers": { "dmc": { "command": "dmc-mcp" } } }
```

and for Codex `codex mcp add dmc -- dmc-mcp`, or in `~/.codex/config.toml`:

```toml
[mcp_servers.dmc]
command = "dmc-mcp"
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

## Hosted server (for agents that cannot run a local process)

`dmc-mcp serve` runs the same tools over MCP Streamable HTTP. One DMC account (a publisher) is signed in once on
the server; callers need the server's key.

- **URL:** `https://dmc.encycam.com/mcp` with `Authorization: Bearer <key>`, or `https://dmc.encycam.com/mcp/<key>`
  for clients that take a URL only.
- **Files:** the server cannot read paths. Upload first and pass the returned `upload:<id>` (24 hours), or pass an
  https link. `publish_folder` takes the folder as a zip.

      curl -H "Authorization: Bearer <key>" -F file=@post.sppx https://dmc.encycam.com/mcp/upload
      {"file":"upload:3f0c…","name":"post.sppx","size":51234,"expiresAt":"…"}

**Running it (operators):** on the server, once —

    mkdir -p /opt/dmc-mcp/data && chown 1654:1654 /opt/dmc-mcp/data
    printf 'DMC_MCP_KEY=%s\n' "$(openssl rand -hex 32)" > /opt/dmc-mcp/dmc-mcp.env && chmod 600 /opt/dmc-mcp/dmc-mcp.env
    sh deploy/update.sh 0.8.0
    docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password    # the server's DMC account

then `sh deploy/update.sh <version>` for every release. `docker logs dmc-mcp` says whom the server is signed in as.
nginx proxies `/mcp` to `127.0.0.1:8095` without buffering, with 15-minute timeouts and no access log.

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
