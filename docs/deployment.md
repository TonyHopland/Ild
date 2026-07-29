# Deployment

The supported deployment is the checked-in Docker Compose stack with PostgreSQL plus the two .NET services. ILD and the WorkItem Server both run EF Core migrations against PostgreSQL when connection strings are configured.

## Docker Compose

```bash
git clone <this repo> ild && cd ild
cp .env.example .env
# set ILD_PASSWORD before continuing
docker compose up --build
```

The compose stack starts three services:

- `postgres` on port `5432`
- `workitem-server` on port `8081`
- `ild` on port `8080`

Open <http://localhost:8080> and log in with the configured username (`admin` by default, or `ILD_USERNAME`) and the `ILD_PASSWORD` value you supplied.

Only `8080` is published. Worktree previews are reached through it on wildcard
subdomains rather than on ports of their own — see
[Worktree preview proxy](#worktree-preview-proxy).

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

Compose defaults it to `http://ild.localhost:8080`, which works with no setup —
browsers resolve `*.localhost` to loopback themselves. The UI itself stays on
`http://localhost:8080`; only `<label>.ild.localhost` hostnames are proxied, and
the apex `ild.localhost` is not one of them.

### Security

**Proxied previews are unauthenticated.** The proxy runs ahead of ILD's
authentication because a preview is a foreign application that knows nothing
about ILD sessions and cannot present a token. Anyone who can resolve a preview
hostname and reach ILD's port can therefore use the running service — including
any secrets a repository's preview `.env` (`Repository.PreviewEnv`) injected into
it, and any data the previewed branch's code touches.

`ILD_PREVIEW_PROXY_BASE` is the opt-in gate. Leave it unset and no request is ever
proxied; previews keep their direct `http://<publicHost>:<port>` URLs, reachable
only by whoever can already reach that port. When you do enable it, put ILD behind
an authenticating ingress or keep it on a trusted network.

### Cluster prerequisites

Two things must exist outside this repository before previews resolve in a
cluster, and neither can be created from here:

1. **Wildcard DNS** for `*.<base host>` pointing at the ingress.
2. **A wildcard SAN** (`*.<base host>`) on the certificate the ingress serves, if
   previews are on HTTPS.

Until both are in place, preview hostnames will not resolve and nothing about the
proxy will appear to work.

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

| Volume          | Purpose                                                |
| --------------- | ------------------------------------------------------ |
| `postgres-data` | PostgreSQL data for both ILD and the WorkItem Server   |
| `ild-data`      | ILD runtime files under `/data`                        |
| `ild-worktrees` | Per-run git worktrees                                  |
| `workitem-data` | Additional WorkItem Server runtime files under `/data` |

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

A `vX.Y.Z` git tag publishes `X.Y.Z`, `X.Y`, and `latest` for both images (amd64 + arm64); pushes to `main` build and test but publish nothing. The compose stack still builds locally with `--build` — it does not pull these images.

**One-time setup:** the first push lands each package **private**. Flip each to **public** once in its GHCR package settings (Package → Settings → Change visibility).

## First-startup behavior

On first successful ILD startup:

1. EF Core migrations are applied when a database connection string is configured.
2. The bootstrap user is created on first login from `ILD_USERNAME` (default `admin`) and `ILD_PASSWORD`.
3. Seed loop templates are created: `Simple Code Change`, `AI-Assisted Feature`, and `Plan`.
4. The global WorkItem Server connection is auto-seeded when `ILD_WORKITEM_SERVER_URL` and `ILD_WORKITEM_SERVER_API_KEY` are present and no URL is configured yet.
5. Recoverable runs are inspected and recovery is attempted according to each run's policy.
