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

**A preview build fails with `MSB3021` or `MSB3374`**

```
error MSB3021: Unable to copy file ".../obj/Debug/net10.0/apphost" to "bin/Debug/net10.0/ild-mcp-server".
Access to the path '.../ILD.McpServer/bin/Debug/net10.0/ild-mcp-server' is denied.

error MSB3374: The last access/last write time on file
"obj/Debug/net10.0/ILD.WorkItemServer.MvcApplicationPartsAssemblyInfo.cache" cannot be set.
Access to the path '...' is denied.
```

Two uids have built in the same worktree. A file created `0755` by one of them clamps the inherited `g:ild-agents:rwx` ACL to an effective `r-x` for the other (`MSB3021`), and setting an explicit mtime through `utimensat` requires being the file's **owner** — group write is irrelevant and no mode or ACL scheme can grant it (`MSB3374`).

Previews run as the `agent` uid, the same one the coding agent builds under ([ADR-0016](adr/0016-preview-runs-as-the-agent.md)), so this cannot arise on a worktree created since. A worktree that was previewed on an older build still holds mixed-ownership output and needs clearing **once**:

```sh
find /worktrees/<worktree> -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

Then start the preview again. If it recurs on a fresh worktree, some other command is building as the orchestrator — a Cmd node, for instance — and that is the thing to look at, not the file modes.

**A preview of ILD dies with `setpriv: setresuid failed: Operation not permitted`**

The nested instance inherited `ILD_AGENT_USER` from the outer one, concluded uid isolation was on, and tried to `setpriv --reuid` without holding `CAP_SETUID` — which a preview child does not have and must not have. Usually seen when opening an interactive provider terminal inside the preview.

Fixed on current builds: a preview inherits none of the orchestrator's uid topology, so the nested instance comes up single-uid. If you are on an older build and cannot upgrade yet, the per-service workaround is to neutralise the three variables in that repository's `ild.config.json`:

```json
"ILD_AGENT_USER": "",
"ILD_ORCHESTRATOR_PRIVATE_ROOT": "${STATE_DIR}/private",
"ILD_AGENT_SCRATCH_ROOT": "${STATE_DIR}/scratch"
```

Remove them again after upgrading — they are no longer needed, and leaving them reads as though every previewed repository requires them.
