# Troubleshooting

**Login returns 401 even with the configured password**

`ILD_PASSWORD` is only used when the bootstrap user is first created. After that, auth uses the stored PBKDF2 hash and a persisted session token. Changing `ILD_PASSWORD` after the user exists has no effect.

**The poller is not claiming work**

Confirm the WorkItem Server is configured (under Settings → WorkItem Server) with a URL, a valid WorkItem API key, and poll settings. The poller remains effectively disabled until that configuration exists.

**A work item is stuck in `Running` remotely**

The WorkItem Server reclaims stale running items when heartbeats stop arriving. Check that ILD is still tracking the item and that the poller is reaching the remote server.

**Webhook updates are not reaching ILD**

The webhook route is not an anonymous bypass. Configure the expected bearer auth and HMAC settings together; a missing or mismatched secret causes rejection.

**Preview URLs are not reachable from the host**

Only ports published by compose are reachable from the host browser. An internal preview may still be valid for AI-driven checks even when it is not externally reachable.

**A preview service starts, but the app behaves as if it has no configuration**

A preview gets an environment ILD constructs, not one it inherits, so a service that used to come up on values nobody wrote down was reading ILD's own — and no longer does. Put what your app needs in the repository's preview `.env` (Repositories page, or a work item's **Preview** tab) or the service's `env` block; see [Configuration](configuration.md#giving-a-preview-its-own-configuration). A database connection string is the usual one.

**A preview build fails with `MSB3021` or `MSB3374`**

```
error MSB3021: Unable to copy file ".../obj/Debug/net10.0/apphost" to "bin/Debug/net10.0/YourApp".
Access to the path '.../YourApp/bin/Debug/net10.0/YourApp' is denied.

error MSB3374: The last access/last write time on file
"obj/Debug/net10.0/YourApp.AssemblyInfo.cache" cannot be set.
Access to the path '...' is denied.
```

Two different users have built in the same worktree. A file created `0755` by one of them is read-only for the other (`MSB3021`), and setting an explicit mtime through `utimensat` requires being the file's **owner** — group write is irrelevant and no mode or ACL scheme can grant it (`MSB3374`). Other toolchains that rewrite an existing output tree can hit the same wall; .NET just names it precisely.

Previews and Cmd nodes run in the run's container as the same user the coding agent builds under, so this cannot arise on a run started since. A worktree built by two users on an older build still holds mixed-ownership output and needs clearing **once**:

```sh
find /worktrees/<worktree> -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

Adjust the directory names for your toolchain (`target`, `build`, `node_modules/.cache`, …), then start the preview or rerun the Cmd node. If it recurs on a fresh worktree, something else is building as another user, and that is what to look at, not the file modes.

**No run or chat can start: ILD cannot open the container engine's socket**

ILD's log says why at startup, and **Settings > General** shows the reason. Check that `ILD_CONTAINER_SOCKET_HOST` in `.env` is the socket of the engine compose talks to ([Which socket](deployment.md#which-socket)). A socket of the host's `root` group (rootful Podman on Linux) needs `ILD_CONTAINER_SOCKET_GRANT=1`. A log warning that the socket's group is unmapped (`userns-remap`) means the container needs `--userns=host`, or another socket. On a rootless engine, restart ILD after restarting the engine's socket. See [The engine socket](deployment.md#the-engine-socket).

**A run's container fails to start with `crun: open 'memory.max'`**

The rootless engine's cgroup controllers are not delegated. Set `ILD_RUN_CPU_LIMIT=0` and `ILD_RUN_MEMORY_LIMIT=0` in `.env`; see [Deployment](deployment.md#which-socket).

**Open in VS Code does nothing, or VS Code cannot find the container**

The button on a work item's **Terminal** tab opens VS Code attached to the run's container, with the worktree open. It is there only when runs execute in Docker or Podman containers, and not while the run is executing. The browser hands the link to VS Code on the machine you browse from, so that machine must run the container engine ILD uses and have VS Code with the Dev Containers extension. With Podman, set `dev.containers.dockerPath` to `podman` in VS Code's settings. A stopped container is started again when you press the button.
