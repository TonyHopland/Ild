# Troubleshooting

**Login returns 401 even with the configured password**

`ILD_PASSWORD` is only used when the bootstrap user is first created. After that, auth uses the stored PBKDF2 hash and a persisted session token. Changing `ILD_PASSWORD` after the user exists has no effect.

**The poller is not claiming work**

Confirm the WorkItem Server is configured (under Settings → WorkItem Server) with a URL, a valid WorkItem API key, and poll settings. The poller remains effectively disabled until that configuration exists.

**A work item is stuck in `Running` remotely**

The WorkItem Server reclaims stale running items when heartbeats stop arriving. Check that ILD is still tracking the item and that the poller is reaching the remote server.

**Webhook updates are not reaching ILD**

The webhook routes take no ILD token: the git host proves itself with the provider's webhook secret alone, and a missing or mismatched secret is a 401. Set the same secret on the remote provider in ILD and in the git host's webhook settings, and remove any `?access_token=` from the webhook URL.

**Preview URLs are not reachable from the host**

A preview runs inside its run's container, so no port of it is published. The browser reaches it on a wildcard subdomain through ILD's own port (`http://wi-<id>.ild.localhost:8080` by default), built from `ILD_PREVIEW_PROXY_BASE`. If the URL does not load, check that `ILD_PREVIEW_PROXY_BASE` is set to a name your browser resolves, with the port ILD is published on (the same outside as inside the container), and that the name has a wildcard record or is under `.localhost`. See [Worktree preview proxy](deployment.md#worktree-preview-proxy).

**A preview service starts, but the app behaves as if it has no configuration**

A preview gets an environment ILD constructs, not one it inherits, so a service that used to come up on values nobody wrote down was reading ILD's own — and no longer does. Put what your app needs in the repository's preview `.env` (Repositories page, or a work item's **Preview** tab) or the service's `env` block; see [Configuration](configuration.md#giving-a-preview-its-own-configuration). A database connection string is the usual one.

**No run or chat can start: ILD cannot open the container engine's socket**

ILD's log says why at startup, and **Settings > General** shows the reason. Check that `ILD_CONTAINER_SOCKET_HOST` in `.env` is the socket of the engine compose talks to ([Which socket](deployment.md#which-socket)). A socket of the host's `root` group (rootful Podman on Linux) needs `ILD_CONTAINER_SOCKET_GRANT=1`. A log warning that the socket's group is unmapped (`userns-remap`) means the container needs `--userns=host`, or another socket. On a rootless engine, restart ILD after restarting the engine's socket. See [The engine socket](deployment.md#the-engine-socket).

**A run's container fails to start with `crun: open 'memory.max'`**

The rootless engine's cgroup controllers are not delegated. Set `ILD_RUN_CPU_LIMIT=0`, `ILD_RUN_MEMORY_LIMIT=0` and `ILD_RUN_PIDS_LIMIT=0` in `.env`; see [Deployment](deployment.md#which-socket).

**Open in VS Code does nothing, or VS Code cannot find the container**

The button on a work item's **Terminal** tab opens VS Code attached to the run's container, with the worktree open. It is there only when runs execute in Docker or Podman containers, and not while the run is executing. The browser hands the link to VS Code on the machine you browse from, so that machine must run the container engine ILD uses and have VS Code with the Dev Containers extension. With Podman, set `dev.containers.dockerPath` to `podman` in VS Code's settings. A stopped container is started again when you press the button.
