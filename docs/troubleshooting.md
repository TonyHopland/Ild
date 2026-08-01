# Troubleshooting

**Login returns 401 even with the configured password**

`ILD_PASSWORD` is only used when the bootstrap user is first created. After that, auth uses the stored PBKDF2 hash and a persisted session token. Changing `ILD_PASSWORD` after the user exists has no effect.

**The poller is not claiming work**

Confirm the WorkItem Server is configured (its own tab in the UI) with a URL, a valid WorkItem API key, and poll settings. The poller remains effectively disabled until that configuration exists.

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

Two different users have built in the same worktree. A file created `0755` by one of them clamps the inherited `g:ild-agents:rwx` ACL to an effective `r-x` for the other (`MSB3021`), and setting an explicit mtime through `utimensat` requires being the file's **owner** — group write is irrelevant and no mode or ACL scheme can grant it (`MSB3374`). Other toolchains that rewrite an existing output tree can hit the same wall; .NET just names it precisely.

Previews now run as the `agent` user, the same one the coding agent builds under ([ADR-0016](adr/0016-preview-runs-as-the-agent.md)), so this cannot arise on a worktree used since. A worktree previewed on an older build still holds mixed-ownership output and needs clearing **once**:

```sh
find /worktrees/<worktree> -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

Adjust the directory names for your toolchain (`target`, `build`, `node_modules/.cache`, …), then start the preview again. If it recurs on a fresh worktree, something else is building as the orchestrator — a Cmd node, for instance — and that is what to look at, not the file modes.

**`setpriv: setresuid failed: Operation not permitted` inside a preview**

Only reachable if the application you are previewing is itself an ILD, or reads the same `ILD_AGENT_USER` variable: it inherited that value from the outer instance, concluded uid isolation was on, and tried to `setpriv --reuid` without holding `CAP_SETUID` — which a preview child does not have and must not have. Usually seen when opening an interactive provider terminal inside the preview.

Fixed on current builds, which pass none of the outer instance's uid topology down. On an older build, neutralise the three variables in that repository's `ild.config.json` and remove them again after upgrading:

```json
"ILD_AGENT_USER": "",
"ILD_ORCHESTRATOR_PRIVATE_ROOT": "${STATE_DIR}/private",
"ILD_AGENT_SCRATCH_ROOT": "${STATE_DIR}/scratch"
```
