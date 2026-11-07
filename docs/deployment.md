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

The release that stores saved AI sessions in compressed chunks converts the existing
ones in the background after startup, one at a time; the app is usable meanwhile, and a
session previewed before its turn says it is still being converted. When
the log shows `Session snapshot conversion finished: … none left`, give the space the
old transcripts held back to the disk, since Postgres keeps it otherwise:

```sh
docker compose exec postgres psql -U ild_core -d IldCore -c 'VACUUM FULL "AdapterSessionSnapshots";'
```

`VACUUM FULL` locks the table while it runs, so saving and opening sessions waits for it;
`pg_repack` does the same without the lock if you have it installed. If the line reports
snapshots left unconverted, the warnings before it name them and the next start retries
them; vacuum after a start that leaves none. A later release drops the old column once
every installation has converted.

Do not roll back to a release from before the conversion once it has run: the old column
is emptied as each session converts, and older releases read only that column.

### Migration B: dropping the old session column

Once every installation has logged `Session snapshot conversion finished: … none left`, a
later release adds an EF migration that drops `AdapterSessionSnapshots.SessionJson` and
removes the code that only exists for unconverted rows. Each such place carries the marker
`Legacy (remove with migration B)`, so `grep -rn "Legacy (remove with migration B)"` lists
them:

- `AdapterSessionSnapshot.LegacySessionJsonColumn` and its mapping in `AppDbContext`
- `SessionSnapshotContentMigrator`, `SessionSnapshotMigrationService` and its registration
- in `AdapterSessionSnapshotStore`: the conversion in `GetMetadataAsync`, the old-column check
  in `AppendAsync`, emptying the old column in `ReplaceWithinAsync`, and `Unconverted` in
  `ToMetadata`
- `SessionSnapshotMetadata.Unconverted`
- the `Unconverted` handling in `LoopRunsController` (the sessions list and the preview's
  `converting` response), and in the frontend the `converting` field, its message and the
  nullable session metadata
- the test helpers that seed legacy rows (`SessionSnapshotTestSupport.SeedLegacyAsync` and
  those beside it) and the tests that use them

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

## Choosing a run host

`ILD_RUN_HOST` decides where a loop run's agent, commands and files live. It is always set explicitly and never detected; unset means `local`. Chats, scheduled jobs and the provider sign-in terminal always stay on the machine ILD runs on.

| | `local` (default) | `container` | `kubernetes` |
| --- | --- | --- | --- |
| A run is | A worktree on ILD's disk, its processes children of ILD under a lower-trust uid ([ADR-0014](adr/0014-agent-uid-isolation.md)) | A container, a volume and a network on the same machine, through the Docker-compatible API ([ADR-0034](adr/0034-container-run-host.md)) | A pod and a volume claim, spread across nodes ([ADR-0033](adr/0033-kubernetes-run-host.md)) |
| Needs | Nothing | A rootless Podman (or rootless Docker) socket and the runner image | A cluster, RBAC, a CNI that enforces NetworkPolicy |
| Isolates the agent from ILD | By uid: it cannot read ILD's secrets, but shares the kernel, the filesystem tree and the network of ILD's container | By container: its own filesystem, process and network namespaces, no capabilities, no route but to ILD | The same, by pod and network policy |
| Isolates runs from each other | By uid only: they share one uid and the worktree root | Yes: a volume, a container and a network each | Yes |
| Spreads load | No | No: one machine | Yes |
| Trust you hand over | None | Whoever holds the socket controls every container on the host | The service account's Role in the run namespace |

Pick `local` for a single-user install where the agent may see the machine. Pick `container` when you want per-run isolation on one machine. Pick `kubernetes` when you already run a cluster and want runs spread over it. Moving between them only affects new runs; a run keeps the host it started on until it is cleaned up.

## Kubernetes run host

`ILD_RUN_HOST=kubernetes` runs each loop run in a pod of its own, so runs are isolated from ILD and from each other and the scheduler spreads them across nodes. It makes no assumption about the cluster's CNI or storage backend. Every setting is in [Configuration](configuration.md#kubernetes-run-host); the decisions are in [ADR-0033](adr/0033-kubernetes-run-host.md).

### Prerequisites

- Kubernetes 1.30 or later, and a CNI that enforces NetworkPolicy. Without enforcement the pods start and work, but nothing keeps them off the rest of the cluster.
- A StorageClass that can provision `ReadWriteOnce` volumes: the cluster's default class, or the one you name in `ILD_K8S_STORAGE_CLASS`. Prefer a class with `reclaimPolicy: Delete`; ILD warns at startup if it is not.
- ILD's pods reachable from the run namespace at the Service in `ILD_API_URL`, on its HTTP port and on the egress proxy port (`ILD_NETWORK_PROXY_PORT`). If the namespace ILD runs in denies ingress by default, allow the run pods (label `app.kubernetes.io/managed-by=ild`, in the run namespace) to those two ports.

### Setup

1. Pick a namespace for the run objects. The examples use `ild-runs`, and ILD in `ild`.
2. Apply the manifests in [`docs/kubernetes`](kubernetes/): the ServiceAccount, the Role with its RoleBinding (pods, pods/exec, volume claims, Secrets and network policies in the run namespace, and nothing else), and optionally the ClusterRole that lets ILD read StorageClasses for the reclaim-policy warning.
3. Set `serviceAccountName: ild` on the ILD Deployment, and:

   ```yaml
   env:
     - { name: ILD_RUN_HOST, value: kubernetes }
     - { name: ILD_API_URL, value: "http://ild.ild.svc:8080" }
     - { name: ILD_K8S_NAMESPACE, value: ild-runs }
     - { name: ILD_K8S_RUNNER_IMAGE, value: "ghcr.io/tonyhopland/ild-runner:latest" }
   ```

   The runner image is published with the same tags as the ILD image; pin the version ILD runs. ILD's pods need the label `app.kubernetes.io/name=ild`, or set `ILD_K8S_ILD_POD_SELECTOR` to the labels they have.
4. The preview proxy forwards to the pod's address, so ILD must be able to reach pod IPs, as any pod in the cluster can.

### Storage

`ILD_K8S_STORAGE_CLASS` and `ILD_K8S_STORAGE_SIZE` decide the volume of each run; unset, the cluster's default class is used. For run scratch, a class that keeps one copy next to the pod is enough, since a run's code is also on the remote. On Longhorn, [`storageclass-longhorn-example.yaml`](kubernetes/storageclass-longhorn-example.yaml) is such a class (`numberOfReplicas: 1`, `dataLocality: best-effort`). It is an operator's choice; ILD has no Longhorn-specific behaviour.

### Cleanup

Deleting a run's Secret deletes its pod, volume claim and network policy through their owner references. A sweeper runs at startup and every `ILD_K8S_SWEEP_INTERVAL_MINUTES`, and deletes the labelled objects of runs that are finished or no longer exist; it only touches objects whose `ild/instance` is its own. An ILD that is killed mid-run therefore leaves nothing behind after its next start.

## Container run host

`ILD_RUN_HOST=container` gives each loop run a container of its own on the machine ILD runs on. It talks to Docker or Podman through the Docker-compatible REST API on a unix socket, so one implementation covers both. It does not spread load, but it isolates a run from ILD and from other runs the way a pod does. Every setting is in [Configuration](configuration.md#container-run-host); the decisions are in [ADR-0034](adr/0034-container-run-host.md); the live check is [`run-hosts/container-verification.md`](run-hosts/container-verification.md).

### Socket trust

Whoever holds the socket controls every container on the host: it can start a privileged container that mounts the host's root. So **the socket you give ILD is a trust decision, and ILD never looks for one**; `ILD_CONTAINER_SOCKET` has no default.

- **Rootless Podman is the supported setup.** Its socket is your user account's: a container it starts has no more reach than the account that runs it. Enable it with `systemctl --user enable --now podman.socket`; the socket is `$XDG_RUNTIME_DIR/podman/podman.sock`.
- **A rootful Docker or Podman socket is root-equivalent on the host**, and so is ILD, and so is anything that gets code execution in ILD. Use it only on a machine you would give ILD root on. ILD asks the engine whether it is rootless (Podman's `/libpod/info`, else Docker's `/info`) and shows a warning in **Settings > General** and in its log when it is not. If the engine does not say, ILD shows nothing rather than guess; this paragraph is then the warning.

### What a run gets

- A named volume for its files, an `internal` network of its own, and a container from the runner image started with `sleep infinity` as uid 10001, all capabilities dropped, `no-new-privileges`, the engine's default seccomp profile, and the CPU and memory limits of `ILD_RUN_CPU_LIMIT` / `ILD_RUN_MEMORY_LIMIT`. The three are made at Start and deleted at Cleanup; the container stays up while a Human node waits, so previews keep working.
- The network is `internal`, so the container cannot reach the internet or the host's other networks. **With ILD in a container (below), it reaches ILD and nothing else**: the model gateway, the MCP endpoint and the egress proxy. **With ILD on the host it does not:** the run reaches ILD at the network's gateway address, which is the host, so it can connect to every service listening on the host (SSH, a database, anything bound to all interfaces), and the egress proxy listens on all of the host's interfaces and accepts connections from the LAN as well as from runs. Run ILD in a container to isolate runs; if ILD must be on the host, add host firewall rules that let only the run networks' subnets reach ILD's port and the proxy port, and block the proxy port from the LAN. Anything else the agent connects to goes through the egress proxy and its limits.
- The container is given the host name in `ILD_API_URL` as ILD's address on its own network. The code goes in and out as bundles over exec, so the container never holds the git credential or the mirror.
- Every object is labelled `app.kubernetes.io/managed-by=ild`, `ild/instance=<ILD_CONTAINER_INSTANCE>` and `ild/run=<run id>`. There are no owner references on a single host, so Cleanup deletes the container, the volume and the network explicitly, and the sweeper (once at startup, then every `ILD_CONTAINER_SWEEP_INTERVAL_MINUTES`) deletes the labelled objects of runs that are finished or gone. It only touches its own instance. An ILD that is killed mid-run leaves nothing behind after its next start.

### ILD on the host

This setup does **not** isolate a run from the host's services (see above); use it for trying the host out, or with the firewall rules described there. Run ILD as a normal process and point it at the socket:

```sh
ILD_RUN_HOST=container
ILD_CONTAINER_SOCKET=$XDG_RUNTIME_DIR/podman/podman.sock
ILD_CONTAINER_RUNNER_IMAGE=ghcr.io/tonyhopland/ild-runner:latest
ILD_API_URL=http://$(hostname):8080
ASPNETCORE_URLS=http://0.0.0.0:8080
```

- Build or pull the runner image first (`podman pull`, or `podman build --target runner -t ild-runner .`); ILD does not pull it and says so when it is missing.
- `ILD_API_URL` must name ILD by a host name that is not `localhost` and not an IP address, and that resolves on this machine too (the machine's name, or an `/etc/hosts` entry): each run's container is given that name for ILD's address on its network, which is the network's gateway, that is, the host.
- ILD must listen on an address the run's network can reach, not only on loopback, and the host's firewall must let the run network's subnet reach ILD's port and the egress proxy port (`ILD_NETWORK_PROXY_PORT`).
- The preview proxy forwards to the container's address on its network, so ILD must be able to route to it. With rootful Docker or Podman it can. A rootless Podman's networks live in a user namespace the host does not route into; run ILD in a container (below) in that case.

### ILD in a container

Mount the socket into ILD's container and tell ILD its own container's name. ILD then connects itself to each run's network and the run reaches it at its address there; nothing is needed from the host's network. As a compose override:

```yaml
# docker-compose.container-runs.yml
#   docker compose -f docker-compose.yml -f docker-compose.container-runs.yml up -d
services:
  ild:
    environment:
      - ILD_RUN_HOST=container
      - ILD_CONTAINER_SOCKET=/run/container.sock
      - ILD_CONTAINER_RUNNER_IMAGE=ghcr.io/tonyhopland/ild-runner:latest
      - ILD_CONTAINER_ILD_CONTAINER=ild # this service's container_name
      - ILD_API_URL=http://ild:8080
    volumes:
      # Rootless Podman: ${XDG_RUNTIME_DIR}/podman/podman.sock. Rootful engines: see Socket trust above.
      - ${ILD_CONTAINER_SOCKET_HOST:?Set ILD_CONTAINER_SOCKET_HOST to the engine socket on the host}:/run/container.sock
```

ILD's process must be able to open the socket: ILD drops to uid 10001 after its entrypoint, so the socket needs to be accessible to that uid inside the container (with rootless Podman, the socket's owner is `root` inside a container run by the same user, and a `group_add` or a looser mode on the socket may be needed). The runner image must be on the host the socket belongs to.

## Agent network limits (`NET_ADMIN`)

The coding agent's network access can be limited from Settings (see
[Configuration → Agent network limits](./configuration.md#agent-network-limits)).
The filtering itself happens inside the container: the orchestrator runs a proxy
on loopback that every agent launch is pointed at, and the entrypoint installs
firewall rules keyed on the agent uid so that a connection which skips the proxy
is dropped. Installing those rules needs one capability, `NET_ADMIN`, which the
root entrypoint spends once before dropping privilege — the orchestrator keeps
only its usual three ambient capabilities and the agent gets none. Per runtime
that is one knob:

| Runtime          | Knob                                                                          |
| ---------------- | ----------------------------------------------------------------------------- |
| docker compose   | `cap_add: [NET_ADMIN]` on the `ild` service — already set in this repo's file |
| docker / podman  | `--cap-add=NET_ADMIN`                                                         |
| Kubernetes / k3s | `securityContext.capabilities.add: ["NET_ADMIN"]` on the `ild` container      |

`security_opt: no-new-privileges` stays on; the rules persist in the network
namespace after the capability is gone. Network mode `off` also depends on it: the
entrypoint only lets the agent out directly when `no-new-privileges` is set (podman
and docker: `--security-opt=no-new-privileges`; Kubernetes:
`allowPrivilegeEscalation: false`), see
[Agent network limits](configuration.md#agent-network-limits). The Kubernetes manifests on
`feature/flux-example-setup` need the `securityContext` addition; nothing in this
repository can make it for you. Behaviour is the same on every runtime because
nothing outside the container is involved.

**Without `NET_ADMIN` the container still starts, in advisory mode.** The proxy
still runs, still logs every destination and still applies the lists to every
client that honours `HTTP_PROXY` — but a hostile agent could bypass it. The
entrypoint logs a `WARNING:` saying so, `GET /api/v1/network/status` answers
`advisory` with the reason, and the Settings page shows a banner. Advisory is
also what you get with uid isolation off (`AGENT_USER=`), since there is then no
separate uid to key rules on.

The rules open only loopback (the proxy, ILD's own API for the agent's MCP
callback, and previews, but not DNS) for the agent uid; everything else it
sends is dropped, IP literal or not. The orchestrator is not subject to them.
The image ships `nftables` and falls back to `iptables` where nf_tables is
unavailable.

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

## Backup and restore

**Settings > Storage > Backup and restore** takes and restores a backup of ILD's data, for any signed-in user (the agent cannot). It is two separate flows, each with its own file and its own passphrase ([ADR-0029](./adr/0029-backup-and-restore.md)):

- **ILD data** (`IldCore`): templates, runs, chats, providers, settings, users.
- **Work item data** (`IldWorkitems`): the built-in WorkItem Server's work items, history and attachments. With an external tracker this reads "This tracker does not support backup" and has no controls.

A backup is one encrypted file. Its passphrase (at least 12 characters) is never stored: without it the file cannot be read, and ILD cannot recover it. Files on disk that are not in a database (run worktrees, repository clones, preview data) are not included.

**Keep `.env` safe, separately.** A backup never contains `ILD_SECRET_KEY`, `ILD_SESSION_TOKEN_PEPPER` or the database passwords. A restore of ILD data needs the same `ILD_SECRET_KEY` the backup was made with, or the credentials it holds could not be read: a restore with a different key (or a key onto an instance without one) is refused before anything changes. A backup made without a key stores those credentials in plain text inside the encrypted file, and the UI asks you to acknowledge that before it creates one. A different `ILD_SESSION_TOKEN_PEPPER` only signs everyone out. ILD's saved WorkItem Server connection, API key included, comes back with the ILD data, so `WORKITEM_API_KEYS` in the new `.env` has to hold that key. The database passwords are those of the new cluster, set by its init script from `.env`.

**Taking a backup.** Pause the scheduler (the scheduler chip on the Taskboard) and let the runs finish, then back up the work items and the ILD data. The browser holds the whole file in memory while it downloads and uploads it, so a backup of many gigabytes (mostly attachments) can fail in the browser; there is no command-line alternative.

**Restoring.** A restore replaces all current data in that database, and restoring one database without the other can leave them inconsistent. Restore both, in this order, with the scheduler paused:

1. Restore the **work items** first.
2. Restore the **ILD data**, within 15 minutes.

The order is not a convention: when ILD starts it cancels every run whose work item is gone, Done or reclaimed, so the work items must be there first. The WorkItem Server hands a claimed item back to Ready after 15 minutes without a heartbeat. Its import sets the heartbeat of every claimed item to the import time, and ILD takes the heartbeats over once the ILD data is restored and ILD has restarted, which is why the second step has 15 minutes.

A restore needs the typed word `RESTORE`, and is refused with a 409 while a loop run is executing, a worktree preview is running or a chat is answering. Before it replaces anything it stores the data it is about to replace as an encrypted backup (same passphrase), which stays on the Storage page to download until the next restore of that database replaces it. A backup from a newer ILD is refused; one from an older ILD restores and is migrated forward. After the ILD data is restored ILD restarts itself, so the container needs `restart: unless-stopped` (the shipped compose file has it); sign in again after a minute.

### Upgrading PostgreSQL 17 to 18

The `postgres:18` image keeps its data under `/var/lib/postgresql` (the data directory moved into a version subdirectory), not `/var/lib/postgresql/data`. Keeping the old mount path leaves the new cluster outside the volume you meant it to use.

1. On the running 17 stack, pause the scheduler, let runs finish, and take both backups. Keep `.env`.
2. `docker compose down`. Leave the `postgres-data` volume alone: it is your way back.
3. In `docker-compose.yml` set `image: postgres:18-alpine` and mount a **new** volume at the new path: `- postgres-data-18:/var/lib/postgresql` (declare `postgres-data-18` under `volumes:`).
4. `docker compose up -d`. The init script creates the roles from `.env`, and both services create their schema on an empty database.
5. Sign in, pause the scheduler, and restore the work items, then the ILD data, as above.
6. Check a work item, a run and a provider connection test, then remove the old volume when you are done with it.

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
