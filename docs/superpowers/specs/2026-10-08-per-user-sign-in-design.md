# dmc-mcp: signing in per user — design

Date: 2026-10-08. Status: approved in chat (Lenar), implemented in 0.9.0. Revised after review the same
day: tokens only with an allow-list, the server's own disk only for Publishers and by share, the keys
during a Keycloak outage.

## Why

Several people use Hermes. Behind the key there is one DMC account, so everything they publish is
Hermes's, and any of them can edit or delete what another uploaded. Hermes can run OAuth itself
(Danil) and send an access token for the person it acts for. So a request may now carry that
person's own token, and dmc-mcp then acts in DMC as that person. The key mode stays as it is.
stdio, `setup`, `login` and `doctor` do not change.

## The door (/mcp and /mcp/upload)

A request is let in by one of two credentials, cheapest check first:

1. The key, as in 0.8.0 — `Authorization: Bearer <key>` or `/mcp/<key>[/…]` (stripped before
   routing), compared in constant time. The call runs as the server's own signed-in account.
2. `Authorization: Bearer <access token>` of a person, from the trusted issuer. The call runs as
   that person.

A bearer that is not the key and looks like a JWT (three dot-separated parts) is checked as a
token. It decides who the call runs as even when the key is in the path too: a person's token is
never silently replaced by the shared account, and a token that fails the check is a 401 even then.
Without a token, the key in the path lets the request in as before.

The token check (`Microsoft.IdentityModel.JsonWebTokens`):

- the signature, against the issuer's keys from OIDC discovery (`<issuer>/.well-known/openid-configuration`
  → `jwks_uri`): fetched on first use and kept 12 hours, so no request waits on the network after
  that — when they are due, the keys in hand go on checking tokens while the refresh runs in the
  background. A token whose key is not among them makes the server fetch the keys again (rotation),
  at most once a minute, so an invented `kid` cannot make it hammer Keycloak. One fetch at a time:
  whoever needs keys while one runs waits for that one. After a failed fetch, none for a minute —
  until the keys were ever loaded, tokens in that minute get their 503 at once. Discovery's
  `issuer` must be the configured issuer;
- asymmetric algorithms only (RS, PS, ES 256–512): `alg: none` and HS* are refused;
- `iss` exactly the issuer; `exp` required; `exp` and `nbf` with 30 seconds of skew;
- Keycloak access tokens only: the payload's `typ` is `Bearer` — ID tokens (`ID`), refresh and
  offline tokens are refused;
- `sub` required — uploads are owned by it;
- two allow-lists, each enforced when set: `DMC_MCP_CLIENTS` (`azp`) and `DMC_MCP_AUDIENCE` (`aud`),
  comma-separated. At least one must be set, or tokens stay off and the key is the only way in, as
  in 0.8.0 (so an env file from 0.8.0 keeps meaning the key alone). Without a list any client of the
  realm would do, and that is anyone: a licsys account is a registration on encycam.com, and the
  public `dealer-space` client mints an access token for it with a password grant.

A refused token is 401 with `WWW-Authenticate: Bearer error="invalid_token", error_description="…",
resource_metadata="…"` and the same text as JSON; the description is fixed text, never the token or
a library message. When the keys cannot be fetched at all (Keycloak down on first use), 503.

## Who the call runs as

The door puts the caller into an `AsyncLocal` for the length of the HTTP request (cleared when it
ends); the tools and the upload endpoint read it per call — never a field of a singleton. With a
person, every DMC call of that request carries their token; with the key, the stored sign-in as
today. No tool takes an identity argument.

Errors name the account when it helps. A 401 or 403 from DMC under a person's token asks
`GET /api/auth/me` with that token — cached for a minute, keyed by the SHA-256 of the token — and
says "…as anna@example.com, who is not a Publisher in DMC", or that the token stopped being accepted
mid-call. A wait for an import that outlives the token says the import carries on and
`check_import` shows it later.

User tokens are never stored, logged or echoed: they live in memory for one request; the door logs a
refusal's reason, not the token; ASP.NET's request logging stays at Warning.

### The token goes on to DMC

dmc-mcp forwards the person's token to the DMC API. MCP's authorization spec warns against token
passthrough, because a token issued for one service should not be spent at another. Here the token
is a `licsys` token in the first place — the realm DMC's own site signs in with — and it goes to the
API of the same site (dmc.encycam.com), which checks it itself (signature and issuer) and resolves
the person from it. In DMC, then, a token does nothing through dmc-mcp that its holder could not do
with it directly. dmc-mcp's own resources are another matter: its disk and CPU — uploads, downloaded
links, unpacked zips — are nothing the DMC API offers, and any licsys account can mint a token. So
tokens are taken only from the clients, or for the audiences, the operator names, and the server
keeps nothing for someone DMC does not let publish ("The server's own disk" below).

MCP also asks that a server check a token was issued for it (the audience). Keycloak does that with
an audience mapper on Hermes's client adding, say, `dmc-mcp` to `aud`; then `DMC_MCP_AUDIENCE=dmc-mcp`
on the server. Until the mapper exists, `DMC_MCP_CLIENTS=<Hermes's client id>` (`azp`) is the
narrowest check Keycloak's tokens allow — strongest when Hermes's client is confidential, since then
only Hermes can turn a sign-in into a token.

## Uploads

An upload belongs to whoever sent it: the key's account, or the token's subject — stored as a hash
of issuer and `sub` in `.owner` beside the file, so no personal data sits on the disk. An
`upload:<id>` of another owner is answered exactly like an unknown one. The janitor does not change;
the limits gain a share per person (next section). An upload from 0.8.0 (no `.owner`) is the key's.
A file named like the store's own `.owner` / `.expires` is stored under another name.

## The server's own disk

An upload, a downloaded link and an unpacked zip land on the DMC server's disk — shared with the
backend and its database — and cost its two cores. The key is the operator's own: as in 0.8.0 it is
bound by the totals alone — two uploads at a time, 5 GB of uploads, and (new) two downloads or
unpackings at a time for everyone together. A person who came in with their own token:

- must be someone DMC lets publish. Before anything of theirs is stored, downloaded or unpacked, the
  server asks `GET /api/auth/me` with their token (the call DMC's own site makes at sign-in;
  remembered a minute per token hash, shared with the errors that name the account) and goes on only
  for a Publisher, or an Admin. Otherwise `/mcp/upload` answers 403 and a tool
  `ERROR: … is not a Publisher in DMC`; when DMC does not answer within 20 seconds or does not take
  the token, 503 with `Retry-After: 60` — nothing is kept on a guess;
- then has a share: one upload at a time (of the two) and 2 GB of unexpired uploads (of the 5), one
  download or unpacking at a time (of the two). Over it: 429 for a slot, 507 for space, an
  `ERROR:` from a tool.

A link is downloaded only while its request lasts: work whose request has ended gets nothing.

## Discovery (MCP authorization, 2025-06-18; RFC 9728)

- Every 401 from `/mcp` and `/mcp/upload` carries
  `resource_metadata="<public>/.well-known/oauth-protected-resource/mcp"`.
- `GET /.well-known/oauth-protected-resource/mcp` and its root form
  `/.well-known/oauth-protected-resource` answer, outside the key door:
  `{"resource":"<public>/mcp","authorization_servers":["<issuer>"],"bearer_methods_supported":["header"],"scopes_supported":["openid"]}`.
- `<public>` is `DMC_MCP_PUBLIC_URL` (default `https://dmc.encycam.com`): behind nginx the request
  itself says `http://…`.
- nginx (`nginx-kc-proxy.conf` in the DMC frontend repository): two exact locations send those paths
  to `127.0.0.1:8095`. They set no `add_header`, so the server's security headers still apply, and
  they keep their logs — no key travels there.
- The SDK's `McpAuthenticationHandler` (1.4.1) is not used: it builds the metadata address and the
  resource from the request's scheme and host (`http` behind nginx), compares a configured absolute
  address with the request's scheme, serves one path, and needs ASP.NET's authentication pipeline,
  while the key door rewrites the path before routing. Its `ProtectedResourceMetadata` type is used
  for the document.

## Settings (serve)

| Variable | Default | |
|---|---|---|
| `DMC_MCP_ISSUER` | `https://webservices.encycam.com/keycloak/realms/licsys` | `off` — the key alone, as 0.8.0 |
| `DMC_MCP_CLIENTS` | — | `azp` allow-list; tokens are off unless it or `DMC_MCP_AUDIENCE` is set |
| `DMC_MCP_AUDIENCE` | — | `aud` allow-list |
| `DMC_MCP_PUBLIC_URL` | `https://dmc.encycam.com` | base of `resource` and `resource_metadata` |

The startup log says which ways in are open: the key; tokens from `<issuer>`, clients `<list or any>`,
audience `<value or not checked>` — or that tokens are off, and why (`DMC_MCP_ISSUER=off`, or no list,
naming the variable that turns them on).

## Outside this repository

- Keycloak (realm `licsys`, its admin): an OAuth client for Hermes — confidential, authorization
  code + PKCE, Hermes's redirect URI, scope `openid`; optionally the audience mapper above.
- Hermes: sends the person's access token as `Authorization: Bearer` on `/mcp` and `/mcp/upload`,
  and refreshes it itself.
- The operator: `DMC_MCP_CLIENTS=<Hermes's client id>` in `dmc-mcp.env` (and `DMC_MCP_AUDIENCE` once
  the mapper exists) — without a list, tokens stay off.
- Each person: a `licsys` account, signed in once on dmc.encycam.com, and the Publisher role in DMC.

## Testing (no network)

The door: key by header and path, a valid token, expired, not yet valid, wrong issuer, `azp` / `aud`
outside the lists, `alg: none`, HS256, an ID token, garbage, key rotation and its rate limit. Per
call: two concurrent requests with different tokens each reach DMC with their own (a fake DMC
client), a key request with the server's. Uploads: another owner's id is not found. The metadata
endpoints and the 401 header. Logs captured through a whole session never contain a token.

After review: tokens off without an allow-list (an env file from 0.8.0), and what the environment
gives. The keys during an outage: one attempt a minute, tokens that arrive together share one, a
hanging refresh keeps no token waiting, a newly rotated key waits for the refresh under way. The
disk: a non-Publisher's upload and link refused before anything is read or fetched, 503 when DMC
does not say, the key not asked; one upload and one download or unpacking at a time per person, a
2 GB share each, the key bound by the totals alone.
