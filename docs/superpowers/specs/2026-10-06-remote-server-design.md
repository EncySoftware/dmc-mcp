# dmc-mcp as a hosted server — design

Date: 2026-10-06. Status: approved in chat, to be planned.

## Why

An agent called Hermes needs Digital Machine Center through MCP — both finding components and
publishing them. Hermes does not run on a publisher's machine, so it cannot start `dmc-mcp` over
stdio the way Cursor, Claude Code and Codex do. The ask (Andrei Kharatsidi, 6 October): run the
MCP server on the DMC VPS in its own container, so Danil Yakushev can connect Hermes to it.

## What changes

One new mode, `dmc-mcp serve`: the same 21 tools over MCP Streamable HTTP
(`ModelContextProtocol.AspNetCore` 1.4.1, the same SDK version as today), stateless — no session for a
restart, an update or an idle hour to end; the tools ask the client nothing, and progress goes on each
call's own response stream. The stdio mode, `setup`,
`login` and `doctor` stay exactly as they are; a publisher running the tool locally sees no
difference except tool descriptions that also mention the hosted way of passing files.

## Signing in to DMC: Hermes's own account

The server acts as one DMC user: a dedicated publisher account for Hermes (a Keycloak user in realm
`licsys`, Publisher role in DMC). Like every publisher, it can edit only what it uploaded, and its
components reach the catalogue only through moderation.

No password is kept on the server. Sign-in happens once, inside the container, with the existing
console flow (`login --password`: a password grant with `offline_access`); only the refresh token
is stored, in the data volume (`XDG_CONFIG_HOME=/data`, so `auth.json` lands in
`/data/dmc-mcp/`). Keycloak rotates refresh tokens and the provider already saves the newest one.
An offline session expires when idle, so the server refreshes the token every 12 hours whether or
not anyone calls it. If sign-in is ever lost, tools answer with the one command that restores it.

The startup log names the account and its roles from `/auth/me`, and warns when Publisher is
missing — the only check an operator needs.

## Who may call the server: a key

A random key (`DMC_MCP_KEY`, at least 32 characters; the server refuses to start without it). A
request is let in when it carries `Authorization: Bearer <key>`, or when the key is the first path
segment after `/mcp` (`/mcp/<key>`, `/mcp/<key>/upload`) — for clients that cannot set headers,
such as connectors configured by URL only. The segment is stripped before routing. Anything else is
401. The comparison is constant-time. Because the key may travel in the path, nginx keeps neither an
access log nor an error log for these locations (an error line — upstream refused during a deploy,
a timeout, a 413 — quotes the request line, key included), and ASP.NET's request logging is kept at
Warning. Clients that can set a header should use Bearer; the path form is for URL-only clients.

## Files

The container cannot see Hermes's disk. And a hosted server must never read its own: with a local
path, `publish_post("/proc/self/environ")` would upload the container's environment, key included,
as a draft. So in `serve` mode local paths are refused, and every tool that takes a file
(`publish_post`, `replace_post_file`, `set_cover`, `inspect_archive`) accepts instead:

- `upload:<id>` — the client first sends the file with one request,
  `curl -H "Authorization: Bearer <key>" -F file=@post.sppx https://dmc.encycam.com/mcp/upload`,
  and gets `{"file":"upload:<id>","name":…,"size":…,"expiresAt":…}`. Uploads live in
  `/data/uploads/<id>/` for 24 hours; ids are 128 random bits. Size limit 1 GB, as the backend's.
- an `https://` link — the server downloads it: https only, at most 5 redirects, 1 GB, 10 minutes,
  and the connection is refused unless the resolved address is public (no loopback, private,
  link-local, CGNAT, multicast; IPv4-mapped IPv6 included). The check runs at connect time, so a
  redirect or a DNS answer cannot steer around it. The file name comes from Content-Disposition or
  the last path segment; a name without a component extension is an error that says so.

`publish_folder` takes the folder as a zip, by either route. It is unpacked into a temporary
directory (no absolute or `..` entries, at most 5000 entries and 1 GB unpacked) and then follows the
existing folder logic, manifest included. Downloads and unpacked folders are deleted when the call
ends. In stdio mode a local path works as before, and an https link works too.

The server's MCP `instructions` describe the upload route, so an agent learns it on connect.

## Where it runs

- Docker image from the repository's `Dockerfile`: the dotnet tool exactly as published on nuget.org, unpacked
  onto the `aspnet:10.0` runtime (user `app`; the tool is built for net8.0 and rolls forward, .NET 8 support
  ending 10 November 2026). Nothing is compiled on the server — the VPS has two cores and
  3.8 GB, most of it the backend's:
  `docker build --build-arg VERSION=0.8.0 -t dmc-mcp:0.8.0 https://github.com/EncySoftware/dmc-mcp.git#v0.8.0`.
- Container `dmc-mcp`, `--restart unless-stopped`, `--memory 1g` and `--pids-limit 256` (if it runs away,
  the kernel stops it rather than the backend), published on `127.0.0.1:8095` only, env file
  `/opt/dmc-mcp/dmc-mcp.env` (root-only, holds the key), volume `/opt/dmc-mcp/data:/data`.
  `deploy/update.sh <tag>` rebuilds and replaces the container; first-time setup is in the README.
- nginx: `location = /mcp` and `location ^~ /mcp/` in the DMC frontend repository's
  `nginx-kc-proxy.conf` (deployed with the frontend, after `nginx -t`), proxying to
  `127.0.0.1:8095` with buffering off, 900 s read/send timeouts (imports stream progress for up to
  10 minutes), `client_max_body_size 1g`, `access_log off` and `error_log /dev/null`.

Danil gets `https://dmc.encycam.com/mcp/<key>` (or `/mcp` plus the header), and the upload command.

## Testing

- Unit tests, no network: the key check (header, path, wrong, missing, path rewriting); file inputs
  in both modes (paths refused in `serve`, upload ids, https only, names); the address filter;
  the upload store (save, resolve, expiry); zip unpacking limits; tool descriptions still built.
- Local smoke: `serve` with a test key; `initialize` and `tools/list` over HTTP — 401 without the
  key, 200 with either form; an upload returns an id.
- After deployment: the same smoke over https, then `find_posts` once Hermes's account is signed in.

## Prerequisites outside this repository

- Hermes's account: a `licsys` user without two-factor and with no pending required actions, and
  the Publisher role in DMC.
- Port 8095 free on the VPS (checked 6 October: 8080/8081 are the backend's blue/green, 8090/8091 a neighbour's).
