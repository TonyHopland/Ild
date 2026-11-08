# Deployment

The supported deployment is the checked-in Docker Compose stack with PostgreSQL plus the two .NET services, on a Docker or Podman engine that also runs ILD's agents. ILD and the WorkItem Server both run EF Core migrations against PostgreSQL when connection strings are configured.

## Docker Compose

```bash
git clone <this repo> ild && cd ild
cp .env.example .env
# fill in the required secrets and ILD_CONTAINER_SOCKET_HOST before continuing
docker compose up -d --build    # or: podman compose up -d --build
```

Compose refuses to start until `.env` supplies every secret it treats as required:
`WORKITEM_API_KEYS`, `ILD_SESSION_TOKEN_PEPPER`, and the three database passwords
`POSTGRES_PASSWORD`, `ILD_DB_PASSWORD`, `WORKITEM_DB_PASSWORD`. Generate each
independently — `openssl rand -hex 32` — and keep them out of version control. There
are deliberately no shipped defaults: a credential that is the same on every install is
a credential every install's attacker already knows.

It also needs `ILD_CONTAINER_SOCKET_HOST`, the socket of the engine compose talks to:
ILD runs every loop run, chat and provider sign-in in a container of its own on that
engine. [Which socket](#which-socket) lists the paths, and
[Container run host](#container-run-host) says what ILD does with it.

`ILD_PASSWORD` is deliberately not one of them. It seeds the bootstrap user on first
login and is never read again once that account exists, so requiring it would stop every
already-seeded stack that has since dropped it from starting at all. Set it for a fresh
install: unset, the account is never created and every login is rejected.

The compose stack starts these services:

- `postgres`, reachable only on the compose network
- `workitem-server` on port `8081`
- `ild` on port `8080`
- `runner`, which builds the image runs start from, `localhost/ild-runner:latest`, on
  the same engine (several minutes the first time). Its container, `ild-runner-build`,
  exits at once and stays listed as exited.

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

A stack from before agents always ran in containers needs `ILD_CONTAINER_SOCKET_HOST`
in `.env` (and `ILD_CONTAINER_SOCKET_GRANT=1` for a socket of the host's `root` group),
and is started with `docker-compose.yml` alone: `docker-compose.container-runs.yml` is
gone. On its first start ILD's entrypoint gives ILD back the files the old agent user
left in `/data` and `/worktrees`, which can take a while on a large volume.

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

### Previews for agents

An agent does not get the address above. In a chat's or run's container (or pod) `*.localhost` is that container, not ILD, and a `*.kube`-style name may not resolve there. `get_preview` gives an agent:

- **In the work item's own run:** the service's own address, `http://127.0.0.1:<port>`, since the preview runs beside it.
- **Anywhere else** (a chat, another work item's run): `http://wi-<workItemId>.preview.ild/`. Every agent is launched with ILD's egress proxy in `HTTP_PROXY`, and the proxy hands any `*.preview.ild` request to ILD's own web port, where it is served as the preview it names. It needs no DNS where the agent runs and works the same with every run host. The network policy does not apply to it, since it never leaves ILD; it is served only when `ILD_PREVIEW_PROXY_BASE` is set, and only over plain `http://`.

It works for clients that leave the name to the proxy: browsers (the chrome-devtools MCP server's Chrome included), curl, git, Python. Node's built-in `fetch` with `NODE_USE_ENV_PROXY` looks the name up itself first and fails on it, as on any name that does not resolve where it runs; use the Chrome tools, curl, or Node with an explicit proxy agent there. The egress proxy needs to know ILD's web port: the first `http://` address in `ASPNETCORE_URLS`, else `ASPNETCORE_HTTP_PORTS`.

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

## Where runs execute

ILD's image always runs agents apart from ILD: every loop run, chat and provider sign-in in a container on the engine whose socket it is given, or in pods when the image runs in a Kubernetes cluster. This is not a setting ([ADR-0036](adr/0036-runs-always-in-containers.md)). ILD started with `dotnet run` runs agents as its own processes, which is for development only ([Configuration](configuration.md#where-runs-execute)).

| | Containers | Kubernetes |
| --- | --- | --- |
| A run is | A container, a volume and a network on the engine, through the Docker-compatible API ([ADR-0034](adr/0034-container-run-host.md)) | A pod and a volume claim, spread across nodes ([ADR-0033](adr/0033-kubernetes-run-host.md)) |
| Needs | A Docker or Podman socket (rootless Podman preferred) and the runner image | A cluster, RBAC, a CNI that enforces NetworkPolicy, and a ReadWriteMany claim for toolchains |
| Isolates the agent from ILD | By container: its own filesystem, process and network namespaces, no capabilities, no route but to ILD | The same, by pod and network policy |
| Isolates runs from each other | Yes: a volume, a container and a network each | Yes |
| Spreads load | No: one machine | Yes |
| Trust you hand over | Whoever holds the socket controls every container on the engine | The service account's Role in the run namespace |

## Kubernetes run host

When ILD's image runs in a Kubernetes cluster, it runs each loop run in a pod of its own, so runs are isolated from ILD and from each other and the scheduler spreads them across nodes. Chats share one pod. ILD only uses pods when it runs in the cluster itself: an ILD outside it runs its agents in containers on its own engine. It makes no assumption about the cluster's CNI or storage backend. Every setting is in [Configuration](configuration.md#kubernetes-run-host); the decisions are in [ADR-0033](adr/0033-kubernetes-run-host.md).

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
     - { name: ILD_API_URL, value: "http://ild.ild.svc:8080" }
     - { name: ILD_K8S_NAMESPACE, value: ild-runs }
     - { name: ILD_K8S_RUNNER_IMAGE, value: "ghcr.io/tonyhopland/ild-runner:latest" }
     - { name: ILD_K8S_TOOLS_CLAIM, value: ild-tools }
   ```

   The runner image is published with the same tags as the ILD image; pin the version ILD runs. ILD's pods need the label `app.kubernetes.io/name=ild`, or set `ILD_K8S_ILD_POD_SELECTOR` to the labels they have.

   `ild-tools` is a **ReadWriteMany** claim you create in the run namespace (NFS, CephFS, Longhorn's RWX volumes, a cloud file share). It holds the [toolchain store](configuration.md#run-toolchains): ILD fills it from a short-lived pod labelled `ild/role=tools`, which needs to reach the internet, so allow that label egress if the namespace denies it by default. The provider sign-in terminal's short-lived pod, labelled `ild/role=signin`, needs the same. Without a ReadWriteMany class, build a runner image of your own `FROM ghcr.io/tonyhopland/ild-runner` with the toolchains and agent CLIs your runs need, and set `ILD_RUN_TOOLCHAINS=off`.
4. The preview proxy forwards to the pod's address, so ILD must be able to reach pod IPs, as any pod in the cluster can.

### Storage

`ILD_K8S_STORAGE_CLASS` and `ILD_K8S_STORAGE_SIZE` decide the volume of each run; unset, the cluster's default class is used. For run scratch, a class that keeps one copy next to the pod is enough, since a run's code is also on the remote. On Longhorn, [`storageclass-longhorn-example.yaml`](kubernetes/storageclass-longhorn-example.yaml) is such a class (`numberOfReplicas: 1`, `dataLocality: best-effort`). It is an operator's choice; ILD has no Longhorn-specific behaviour.

### Cleanup

Deleting a run's Secret deletes its pod, volume claim and network policy through their owner references. A sweeper runs at startup and every `ILD_K8S_SWEEP_INTERVAL_MINUTES`, and deletes the labelled objects of runs that are finished or no longer exist; it only touches objects whose `ild/instance` is its own. An ILD that is killed mid-run therefore leaves nothing behind after its next start.

## Container run host

ILD's image, outside Kubernetes, gives each loop run a container of its own on the engine whose socket is mounted into it, runs chats in one shared container, and runs the provider sign-in terminal in a short-lived container that keeps the login when it closes. It talks to Docker or Podman through the Docker-compatible REST API on a unix socket, so one implementation covers both. It does not spread load, but it isolates a run from ILD and from other runs the way a pod does. Every setting is in [Configuration](configuration.md#container-run-host); the decisions are in [ADR-0034](adr/0034-container-run-host.md); the live check is [`run-hosts/container-verification.md`](run-hosts/container-verification.md).

### Socket trust

Whoever holds the socket controls every container on the host: it can start a privileged container that mounts the host's root. So **the socket you give ILD is a trust decision, and ILD never looks for one**; `ILD_CONTAINER_SOCKET_HOST` has no default.

- **Rootless Podman is the supported setup.** Its socket is your user account's: a container it starts has no more reach than the account that runs it. Enable it with `systemctl --user enable --now podman.socket`; the socket is `$XDG_RUNTIME_DIR/podman/podman.sock`.
- **A rootful Docker or Podman socket is root-equivalent on the host**, and so is ILD, and so is anything that gets code execution in ILD. Use it only on a machine you would give ILD root on. ILD asks the engine whether it is rootless (Podman's `/libpod/info`, else Docker's `/info`) and shows a warning in **Settings > General** and in its log when it is not. If the engine does not say, ILD shows nothing rather than guess; this paragraph is then the warning.
- **When ILD cannot reach the socket at all** (not mounted, no permission, the engine stopped), the same card and the log say so, with the reason: no run can start until it can. The warning goes by itself once the socket answers.

### What a run gets

- A named volume for its files, an `internal` network of its own, and a container from the runner image started with `sleep infinity` as uid 10001, all capabilities dropped, `no-new-privileges`, the engine's default seccomp profile, and the CPU and memory limits of `ILD_RUN_CPU_LIMIT` / `ILD_RUN_MEMORY_LIMIT`. The three are made at Start and deleted at Cleanup; the container stays up while a Human node waits, so previews keep working.
- The network is `internal`, so the container cannot reach the internet or the host's other networks. ILD connects its own container (`ILD_CONTAINER_ILD_CONTAINER`) to the network, so the run reaches ILD and nothing else: the model gateway, the MCP endpoint and the egress proxy. Anything else the agent connects to goes through the egress proxy and its [limits](configuration.md#agent-network-limits).
- The container is given the host name in `ILD_API_URL` as ILD's address on its own network. The code goes in and out as bundles over exec, so the container never holds the git credential or the mirror.
- The toolchains its repository needs and the agents ILD has installed, from the [toolchain store](configuration.md#run-toolchains): the `ild-tools` volume, mounted read-only. The first run that needs a version installs it, in a short-lived `ild-tools-…` container of the runner image on the engine's default network; later runs reuse it. The volume is shared by every run, and never pruned.
- When ILD stops, the container is stopped, and started again when the run next needs it ([What happens to the containers and pods ILD made](#what-happens-to-the-containers-and-pods-ild-made)).
- Every object is labelled `app.kubernetes.io/managed-by=ild`, `ild/instance=<ILD_CONTAINER_INSTANCE>` and `ild/run=<run id>`. There are no owner references on a single host, so Cleanup deletes the container, the volume and the network explicitly, and the sweeper (once at startup, then every `ILD_CONTAINER_SWEEP_INTERVAL_MINUTES`) deletes the labelled objects of runs that are finished or gone. It only touches its own instance. An ILD that is killed mid-run leaves nothing behind after its next start.

### The engine socket

Compose mounts the socket named by `ILD_CONTAINER_SOCKET_HOST` in `.env` at `/run/container.sock` in ILD's container (and refuses to start without it), tells ILD its own container's name (`ild`), and builds the runner image as `localhost/ild-runner:latest` on the same engine. ILD connects itself to each run's network and the run reaches it at its address there; nothing is needed from the host's network. To run from the published runner image instead, set `ILD_CONTAINER_RUNNER_IMAGE` to `ghcr.io/tonyhopland/ild-runner:<the version ILD runs>` in `.env`. The runner image must be on the engine the socket belongs to; ILD does not pull it and says so when it is missing.

ILD's entrypoint gives ILD's user a group that can open the mounted socket, before it drops from root. `group_add` does not work for this, since the drop rebuilds the user's groups. What it does depends on the socket:

- **Rootless Podman or Docker.** Inside ILD's container the socket shows as `root:root`. The entrypoint gives it a group of its own, `ild-engine`, which only ILD's user is in. On the host the socket stays yours; only its group changes. Restarting the engine's socket (`systemctl --user restart podman.socket`) makes a new one with the old group; restart ILD after it.
- **A socket with a group of its own,** such as rootful Docker's `docker` group, or a Podman machine's rootful socket (`root:wheel`): ILD's user joins that group. Nothing outside the container changes.
- **A socket of the host's `root` group** (rootful Podman on Linux): the entrypoint leaves it alone and says so, and ILD cannot open it, unless you set `ILD_CONTAINER_SOCKET_GRANT=1`. Then the entrypoint gives the socket ILD's own group (`ild`, gid 10001; the host sees the same number) every time ILD's container starts, so a socket the engine makes anew after a restart of the engine or the machine is covered as soon as ILD starts again. That makes ILD root-equivalent on that machine, which is why it is your choice and off by default.

No run or chat container is ever given the socket.

Once ILD is up, its log says `Container engine socket /run/container.sock: ild can open it through group …` (after `ILD_CONTAINER_SOCKET_GRANT=1: gave /run/container.sock … to group ild` when that setting applied), and **Settings > General** shows no "cannot reach" warning (on a rootful engine it warns that the engine is not rootless).

Keep ILD's port the same outside its container as inside (`8080:8080` and the default `ILD_PREVIEW_PROXY_BASE=http://ild.localhost:8080`). A preview URL is used by your browser outside the container and by a chat's browser inside it, where `*.localhost` is ILD's container itself; mapped to another port outside (`8090:8080`), one of the two cannot load it. To move ILD to another port, move it inside too: `ASPNETCORE_URLS=http://+:8090`, `ILD_API_URL=http://ild:8090`, `8090:8090` and the proxy base on `8090`.

The first run of a repository installs its toolchains and ILD's agents into the `ild-tools` volume before it starts, which takes from seconds to a few minutes; later runs reuse them. With a local model (LM Studio, Ollama) behind the provider, the agent nodes take as long as the model does.

### Which socket

`ILD_CONTAINER_SOCKET_HOST` is the socket's path on the machine the engine runs on:

| Engine | `ILD_CONTAINER_SOCKET_HOST` | Notes |
| --- | --- | --- |
| Rootless Podman on Linux | `/run/user/<uid>/podman/podman.sock` (`$XDG_RUNTIME_DIR/podman/podman.sock`) | The supported setup. Needs `systemctl --user enable --now podman.socket` |
| Podman machine (Windows, macOS), rootless | `/run/user/1000/podman/podman.sock` | The path inside the machine's VM, where the containers run |
| Podman machine, rootful | `/run/podman/podman.sock` | The machine makes it `root:wheel` on every start, and ILD's user joins a group of that gid: nothing to do |
| Rootful Podman on Linux | `/run/podman/podman.sock` | Belongs to the host's `root` group: also set `ILD_CONTAINER_SOCKET_GRANT=1` (below) |
| Rootful Docker | `/var/run/docker.sock` | ILD's user joins its `docker` group |

On a Podman machine, compose talks to whichever of the two engines the machine forwards its API to: the rootful one when the machine is rootful (`podman machine inspect --format '{{.Rootful}}'`), the rootless one otherwise. The stack, its volumes and the runner image all live on that engine, and the socket you mount must be that engine's. `podman machine set --rootful=false` switches a stopped machine to rootless; containers and volumes made on the rootful side stay there.

**Rootful Podman.** A Podman machine needs nothing: its image ships a `podman.socket` drop-in (`SocketGroup=wheel`), so `podman machine ssh stat -c %G /run/podman/podman.sock` prints `wheel`, and the entrypoint joins a group of that gid inside ILD's container. That is root-equivalent on the machine all the same, and Settings warns about it. Rootful Podman on Linux, or any engine whose socket prints `root` there, belongs to the host's `root` group, which the entrypoint does not join. To use it anyway, accepting that ILD is then root-equivalent on that machine (Settings warns about it), set `ILD_CONTAINER_SOCKET_GRANT=1` in `.env`. The entrypoint then gives the socket ILD's group on each start. Systemd makes the socket anew each time the host or `podman.socket` starts, so a change made by hand (`chgrp`) would not last; after a restart, start ILD again (`podman compose … up -d`) if it is not already restarting, and it fixes the new socket itself.

**Limits on a rootless engine.** When the user's cgroup controllers are not delegated (a Podman machine, or a Linux host without systemd's `Delegate=`), a run's container fails to start with `crun: open 'memory.max' … No such file or directory`. Set both `ILD_RUN_CPU_LIMIT=0` and `ILD_RUN_MEMORY_LIMIT=0` in `.env`, which compose passes on; runs then start without limits.

## Volumes

| Volume          | Purpose                                                                                                                                                                          |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `postgres-data` | PostgreSQL data for both ILD and the WorkItem Server. The role passwords are baked in when it is first created — see [Upgrading an existing stack](#upgrading-an-existing-stack) |
| `ild-data`      | ILD runtime files under `/data`                                                                                                                                                  |
| `ild-worktrees` | Per-run git worktrees                                                                                                                                                            |
| `ild-config`    | The provider logins, ILD's alone                                                                                                                                                 |
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

The main `Dockerfile` builds the frontend and publishes the .NET host into a slim runtime image with git and Node, and its `runner` target builds the image runs start from (see [Configuration](./configuration.md#build-time-container-options)). `Dockerfile.WorkItemServer` builds the separate WorkItem Server image.

### Published images

CI also builds and pushes these images to GHCR (see [ADR-0012](./adr/0012-ghcr-image-tagging-strategy.md)):

- `ghcr.io/tonyhopland/ild` — ILD plus the bundled MCP server
- `ghcr.io/tonyhopland/ild-workitem-server` — the WorkItem Server
- `ghcr.io/tonyhopland/ild-runner` — the slim image of a run's container or pod: an OS, the tools every project uses, Chrome and mise, with no toolchain or agent ([ADR-0035](./adr/0035-run-toolchains.md))

A project that needs system packages beyond the slim runner (a database client, system libraries a native module links against) builds its own runner image `FROM ghcr.io/tonyhopland/ild-runner` and points `ILD_CONTAINER_RUNNER_IMAGE` or `ILD_K8S_RUNNER_IMAGE` at it. A runner image that carries its own toolchains and agent CLIs, for a cluster without a ReadWriteMany claim, also sets `ILD_RUN_TOOLCHAINS=off`.

A `vX.Y.Z` git tag publishes `X.Y.Z`, `X.Y`, and `latest` for all of them (amd64 + arm64, each built on a native runner and joined into one multi-arch manifest); pushes to `main` build and test but publish nothing. The compose stack still builds ILD and the runner image locally with `--build` — it does not pull these images.

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

### What happens to the containers and pods ILD made

After the drain, ILD puts away what it made, within a few seconds of the grace
period:

| | Docker or Podman | Kubernetes |
| --- | --- | --- |
| A run's container or pod | **Stopped**, with its volume and network kept: nothing runs while ILD is down, a preview included. ILD starts it again when the run next needs it, which also covers a restart of the engine or the machine | **Kept running**: a pod cannot be stopped, and it is what a parked run resumes in |
| The chat container or pod | **Removed**. The chats' folders are in its volume or claim; the next chat makes it again from the runner image as it is then | **Deleted**, the same way |
| A sign-in container or pod | **Removed** | **Deleted** |

The chat container or pod is long-lived, so ILD also replaces one that was made
from another runner image or with other settings: at startup, and before a chat
uses it. A `docker compose up --build` that rebuilds the runner image therefore
reaches the chats too. A run keeps the image it started with to its end; the next
run gets the new one.

A run's objects are removed when the run is cleaned up, and the sweep (at startup
and every 10 minutes) removes those of runs that finished or that ILD no longer
knows, such as after `docker compose down -v`. A container left running by an
ILD that was killed outright is stopped or replaced the next time ILD needs it.

### Removing ILD for good

`docker compose down -v` removes ILD's own containers and volumes, but not what
ILD made through the engine, since compose did not make it, and an ILD that never
starts again never sweeps it. Remove it by its label, on the engine ILD used:

```sh
docker rm -f $(docker ps -aq --filter label=app.kubernetes.io/managed-by=ild)
docker volume rm $(docker volume ls -q --filter label=app.kubernetes.io/managed-by=ild)
docker network rm $(docker network ls -q --filter label=app.kubernetes.io/managed-by=ild)
docker volume rm ild-tools   # the toolchain store
```

(`podman` takes the same commands.) On Kubernetes, run
`kubectl delete pods,pvc,secrets,networkpolicies -l app.kubernetes.io/managed-by=ild`
in the run namespace, then delete the `ILD_K8S_TOOLS_CLAIM` claim if you made one.

## First-startup behavior

On first successful ILD startup:

1. EF Core migrations are applied when a database connection string is configured.
2. The bootstrap user is created on first login from `ILD_USERNAME` (default `admin`) and `ILD_PASSWORD`.
3. Seed loop templates are created: `Simple Code Change`, `AI-Assisted Feature`, and `Plan`.
4. The global WorkItem Server connection is auto-seeded when `ILD_WORKITEM_SERVER_URL` and `ILD_WORKITEM_SERVER_API_KEY` are present and no URL is configured yet.
5. Recoverable runs are inspected and recovery is attempted according to each run's policy. Two shapes count as recoverable: a run left `Running` by a crash, which is re-driven from its current node, and a run the shutdown drain parked on the way out, which is resumed against the agent session it was parked on rather than re-run cold (see [Shutdown and run draining](#shutdown-and-run-draining)). A halt a **human** pressed is neither, and is left exactly where they left it.
