# Deployment

The supported deployment is the checked-in Docker Compose stack with PostgreSQL plus the two .NET services. ILD and the WorkItem Server both run EF Core migrations against PostgreSQL when connection strings are configured.

## Docker Compose

```bash
git clone <this repo> ild && cd ild
cp .env.example .env
# fill in the required secrets before continuing
docker compose up --build
```

Compose refuses to start until `.env` supplies every secret it treats as required:
`WORKITEM_API_KEYS`, `ILD_SESSION_TOKEN_PEPPER`, and the three database passwords
`POSTGRES_PASSWORD`, `ILD_DB_PASSWORD`, `WORKITEM_DB_PASSWORD`. Generate each
independently — `openssl rand -hex 32` — and keep them out of version control. There
are deliberately no shipped defaults: a credential that is the same on every install is
a credential every install's attacker already knows.

`ILD_PASSWORD` is deliberately not one of them. It seeds the bootstrap user on first
login and is never read again once that account exists, so requiring it would stop every
already-seeded stack that has since dropped it from starting at all. Set it for a fresh
install: unset, the account is never created and every login is rejected.

The compose stack starts three services:

- `postgres`, reachable only on the compose network
- `workitem-server` on port `8081`
- `ild` on port `8080`

Open <http://localhost:8080> and log in with the configured username (`admin` by default, or `ILD_USERNAME`) and the `ILD_PASSWORD` value you supplied.

Only `8080` and `8081` are published. PostgreSQL is not: both services reach it on the
compose network, and nothing outside needs to. Uncomment the `ports` line on the
`postgres` service to attach a client from the host. Worktree previews are reached
through `8080` on wildcard subdomains rather than on ports of their own — see
[Worktree preview proxy](#worktree-preview-proxy).

### Upgrading an existing stack

The database passwords are written into the `ild_core` and `ild_workitems` roles the
first time the `postgres-data` volume is initialised, so a stack created before these
variables existed still has the old shipped values, and putting new ones in `.env` does
not reach them — the containers start and then fail to authenticate. Before upgrading,
either `ALTER ROLE ild_core WITH PASSWORD '<new>';` and the same for `ild_workitems`,
matching the values you put in `.env`, or discard the volume and let the init script
recreate the roles. `POSTGRES_PASSWORD` needs no such step: the image only reads it when
it creates the cluster, and nothing connects as the superuser afterwards.

Setting `ILD_SESSION_TOKEN_PEPPER` for the first time signs every device out once; that
is expected and costs a re-login.

## Worktree preview proxy

A [Worktree Preview](../CONTEXT.md) binds each of its services to a port picked at
runtime inside the container. Those ports are not published, so
`http://<host>:<port>` is a URL that resolves to nothing the moment ILD is behind
an ingress or a container boundary. Setting `ILD_PREVIEW_PROXY_BASE` makes ILD
serve previews on wildcard subdomains of that origin instead, through its own
port — see [ADR-0015](./adr/0015-wildcard-subdomain-preview-routing.md) for why
subdomains rather than path prefixes.

| Hostname                               | Reaches                                                |
| -------------------------------------- | ------------------------------------------------------ |
| `wi-<workItemId>.<base>`               | The profile's service marked `"public": true`          |
| `wi-<workItemId>-<serviceName>.<base>` | That one service, whatever its name (hyphens included) |

The work item id in the hostname is the numeric id shown in the UI. With the
variable set, the `publicUrl` in the work item's Preview tab is the proxy URL; an
explicit `publicUrl` in `ild.config.json` still wins over it.

The bare `wi-<workItemId>` form names a single service, so it is only advertised
when the profile has exactly one service marked `"public": true`. A profile with
several is advertised — and must be addressed — as `wi-<workItemId>-<serviceName>`
for each; the bare form then 404s rather than picking one, and the reason is logged.
A service whose name is not a legal DNS label cannot appear in a hostname at all,
and keeps its direct URL.

**Anything the proxy will not forward is a flat 404.** A hostname that isn't a
preview address, a work item that doesn't exist, one with no worktree, a preview
nobody started, a service that is stopped or has crashed — all answer with the same
page, so the response says nothing about what does or doesn't exist inside ILD. Why
a particular request wasn't served is written to ILD's log instead, at `Debug` for
the routine cases and `Warning` for a misconfiguration.

`ILD_PREVIEW_PROXY_BASE` also declares the scheme browsers use. Set it to
`https://…` when an ingress terminates TLS in front of ILD: ILD itself listens on
plain HTTP, so it is the configured base — not the incoming request — that decides
the scheme in rewritten redirects, in the `X-Forwarded-Proto` handed to the
preview, and whether a preview's `Secure` cookies are left intact.

Compose defaults it to `http://ild.localhost:8080`, which works with no setup —
browsers resolve `*.localhost` to loopback themselves. The UI itself stays on
`http://localhost:8080`; only `<label>.ild.localhost` hostnames are proxied, and
the apex `ild.localhost` is not one of them.

### Security

**A running proxied preview is unauthenticated.** The proxy runs ahead of ILD's
authentication because a preview is a foreign application that knows nothing about
ILD sessions and cannot present a token. Anyone who reaches a running preview can
use it — including any secrets a repository's preview `.env`
(`Repository.PreviewEnv`) injected into it, and any data the previewed branch's
code touches.

Be clear about what "reaches" means: the preview hostname is carried in the `Host`
header, which the client chooses. **Wildcard DNS is a convenience for browsers, not
a control** — anyone who can open a connection to ILD's port can send
`Host: wi-12.<base>` by hand, with no DNS involved. The boundary is therefore
network access to ILD's port, nothing more. Put ILD behind an authenticating
ingress or keep it on a trusted network.

What the boundary protects is narrower than it looks, because nothing is served
unless someone has started that preview: with no preview running, every preview
hostname is a 404 that reveals nothing. The exposure lasts as long as the preview
does.

`ILD_PREVIEW_PROXY_BASE` remains the switch. Leave it unset — or set it to an empty
value, which is why compose uses `${ILD_PREVIEW_PROXY_BASE-…}` rather than `:-` —
and no request is ever proxied; previews keep their direct
`http://<publicHost>:<port>` URLs, reachable only by whoever can already reach that
port.

A proxied response also does not inherit the response headers ILD sets for its own
UI — the `Content-Security-Policy`, `X-Frame-Options` and friends. Those are tuned
for ILD (`default-src 'self'`) and would break an application that loads a font or
a script from anywhere else. A preview's own headers are passed through as the
previewed application sent them.

### Cluster prerequisites

Two things must exist outside this repository before a browser can reach a preview
in a cluster, and neither can be created from here:

1. **Wildcard DNS** for `*.<base host>` pointing at the ingress.
2. **A wildcard SAN** (`*.<base host>`) on the certificate the ingress serves, if
   previews are on HTTPS.

Until both are in place, preview hostnames will not resolve in a browser and
nothing about the proxy will appear to work. Note again that this is about
usability, not access control — see [Security](#security).

The Kubernetes manifests on `feature/flux-example-setup` also still need updating:
a wildcard host on the Ingress, `ILD_PREVIEW_PROXY_BASE` in the Deployment,
`ILD_ALLOWED_ORIGINS` extended to cover preview origins, and removal of the
per-preview `containerPort`/`servicePort` entries that this feature makes
unnecessary.

### Hot module reload

A proxied preview **loads**, but a dev server's live-reload channel generally does
not reconnect: the client is told to connect to the origin the server thinks it is
on, which is the loopback port the browser cannot reach. Edits are picked up on a
manual refresh. This is documented rather than solved — each framework needs its
own hint, set in the service's `env` or command:

| Dev server         | Hint                                                                             |
| ------------------ | -------------------------------------------------------------------------------- |
| Vite               | `server.hmr.clientPort` / `server.hmr.protocol` in `vite.config.ts`, or `--host` |
| webpack-dev-server | `client.webSocketURL` in the devServer config                                    |
| Next.js            | Works over the same origin; set `assetPrefix` only if you also change the path   |
| Angular CLI        | `--live-reload-client <preview URL>`                                             |

Services that need the browser-facing hostname to build those URLs themselves can
set `"rewriteHost": false` (see [Configuration](./configuration.md#service-fields))
and add the preview wildcard to their own allowed-hosts list.

## Volumes

| Volume          | Purpose                                                                                                                                                                          |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `postgres-data` | PostgreSQL data for both ILD and the WorkItem Server. The role passwords are baked in when it is first created — see [Upgrading an existing stack](#upgrading-an-existing-stack) |
| `ild-data`      | ILD runtime files under `/data`                                                                                                                                                  |
| `ild-worktrees` | Per-run git worktrees                                                                                                                                                            |
| `workitem-data` | Additional WorkItem Server runtime files under `/data`                                                                                                                           |

Your host `~/.gitconfig` is mounted read-only into the ILD container, so agent commits inherit your local name and email by default. Point `GIT_CONFIG` at a different file, or override just the identity with `GIT_AUTHOR_NAME`/`GIT_AUTHOR_EMAIL` (and the matching `GIT_COMMITTER_*` vars) in your `.env` — git reads those natively and they take precedence over the mounted file.

For host bind mounts instead of named volumes:

```yaml
volumes:
  - ./.local/ild-data:/data
  - ./.local/ild-worktrees:/worktrees
  - ./.local/workitem-data:/data
```

## Images

The main `Dockerfile` builds the frontend, publishes the .NET host, and optionally installs additional runtime tooling used by work-item execution (see [Configuration](./configuration.md#build-time-container-options)). `Dockerfile.WorkItemServer` builds the separate WorkItem Server image.

### Published images

CI also builds and pushes both images to GHCR (see [ADR-0012](./adr/0012-ghcr-image-tagging-strategy.md)):

- `ghcr.io/tonyhopland/ild` — batteries-included app plus the bundled MCP server
- `ghcr.io/tonyhopland/ild-workitem-server` — the WorkItem Server

A `vX.Y.Z` git tag publishes `X.Y.Z`, `X.Y`, and `latest` for both images (amd64 + arm64, each built on a native runner and joined into one multi-arch manifest); pushes to `main` build and test but publish nothing. The compose stack still builds locally with `--build` — it does not pull these images.

**One-time setup:** the first push lands each package **private**. Flip each to **public** once in its GHCR package settings (Package → Settings → Change visibility).

## Shutdown and run draining

On SIGTERM, ILD stops claiming work and parks the runs it is driving — in-flight
AI nodes are halted at a known point, keeping their agent session, and resumed
automatically on the next start. The mechanics and the resume rules are in
[Configuration](./configuration.md#graceful-shutdown); what matters here is the
one budget that lives outside ILD, the supervisor's grace period. The nesting is
`ILD_SHUTDOWN_DRAIN_SECONDS` (20s) < host shutdown timeout (drain + 5s, derived)
< supervisor grace period. If the outermost is too small the supervisor SIGKILLs
mid-park and you are back to the hard kill draining exists to replace.

- **docker-compose:** the default `stop_grace_period` is **10s** — shorter than
  the drain. This repository's `docker-compose.yml` sets `stop_grace_period: 30s`
  on the `ild` service. Any compose file of your own must do the same.
- **Kubernetes:** the default `terminationGracePeriodSeconds` is **30s**, which
  already clears the 25s host timeout. No manifest change is needed unless you
  raise `ILD_SHUTDOWN_DRAIN_SECONDS`; if you do, raise this to match.

Note that draining bounds how long a stop takes, not whether a run survives one:
a process down longer than the work-item server's stale-claim window (~15
minutes) still has its item reclaimed and its local run cancelled on startup, as
it always did.

## First-startup behavior

On first successful ILD startup:

1. EF Core migrations are applied when a database connection string is configured.
2. The bootstrap user is created on first login from `ILD_USERNAME` (default `admin`) and `ILD_PASSWORD`.
3. Seed loop templates are created: `Simple Code Change`, `AI-Assisted Feature`, and `Plan`.
4. The global WorkItem Server connection is auto-seeded when `ILD_WORKITEM_SERVER_URL` and `ILD_WORKITEM_SERVER_API_KEY` are present and no URL is configured yet.
5. Recoverable runs are inspected and recovery is attempted according to each run's policy. Two shapes count as recoverable: a run left `Running` by a crash, which is re-driven from its current node, and a run the shutdown drain parked on the way out, which is resumed against the agent session it was parked on rather than re-run cold (see [Shutdown and run draining](#shutdown-and-run-draining)). A halt a **human** pressed is neither, and is left exactly where they left it.
