# ADR-0016: A Worktree Preview runs as the agent, with an environment ILD constructs

A **Worktree Preview** executes the commands in the worktree's `ild.config.json`
— a file the coding agent writes, and can trigger through the ILD MCP tools
whenever it likes. Those commands used to run as the **orchestrator** uid with the
orchestrator's whole environment inherited. They now run as the **agent** uid,
with an environment ILD builds deliberately rather than one the child picks up by
default. The principle is that agent-authored code executes with exactly the
privileges the agent already has and nothing more — which is what
[ADR-0014](./0014-agent-uid-isolation.md) set out to establish and listed the
preview as the outstanding exception to.

## What was actually wrong

Three separate failures, one root cause. .NET pre-populates a child process's
environment from the current process, and the preview's spawn sites only wrapped
their child in `setpriv --inh-caps=-all --ambient-caps=-all` — a capability drop,
not an environment decision.

**The orchestrator's secrets reached the preview.** Every name in
`AgentIsolation`'s secret list — both database connection strings, `ILD_SECRET_KEY`,
the bootstrap credentials, and the API tokens ILD uses to reach itself and the
WorkItem Server — arrived in the child verbatim. This was observed, not inferred:
previewing this repository produced an `api.log` containing a
`SELECT … FROM public."__EFMigrationsHistory"` against the orchestrator's own
Postgres, because `ILD.Api` reads its connection string from the environment and
the preview's config never set one. A hostile agent needed only
`command: "env > leak.txt"` in `ild.config.json` and a `start_preview` call to
read back the exact values the uid split exists to withhold from it, with a
`"public": true` service on the wildcard preview host as a ready exfiltration
channel.

**The orchestrator's own runtime topology reached it too**, which is a different
category: `ILD_AGENT_USER`, `ILD_AGENT_GROUP`, `ILD_AGENT_HOME`,
`ILD_AGENT_SCRATCH_ROOT` and `ILD_ORCHESTRATOR_PRIVATE_ROOT` are not secret, they
are simply wrong for any child that is not this orchestrator — and wrong
silently. A nested ILD (previewing this repository inside itself) inherited
`ILD_AGENT_USER=agent`, concluded uid isolation was on, and routed its own
interactive provider terminal through `setpriv --reuid=agent` without holding
`CAP_SETUID`, dying with `setpriv: setresuid failed: Operation not permitted`.
Sharper still, it pointed its orchestrator-private root at the outer instance's,
where the git askpass helper lives — the script handed to git as `GIT_ASKPASS`
with a repository token in its environment. Both instances run as the same uid,
so the collision raised no permission error; the inner instance simply overwrote
the outer's helper.

**Cross-uid builds failed outright.** A preview building as `ild` in a worktree
the agent had already built in as `agent` hit `MSB3021` (the destination
`apphost` copy was created `0755` by the agent's build, clamping the inherited
`g:ild-agents:rwx` ACL to an effective `r-x`) and `MSB3374` (setting an explicit
mtime through `utimensat` requires being the file's owner or holding
`CAP_FOWNER`, so no mode or ACL scheme can grant it). Two uids building in one
tree is the cause; one uid removes the class.

## The decision

- **Both preview spawn sites cross to the agent uid** via `AgentIsolation.Route`,
  which drops uid and gid, loads the shared group, and clears the inheritable and
  ambient capability sets. With `ILD_AGENT_USER` unset it is a no-op and commands
  run inline as the current user — the single-uid escape hatch ADR-0014 documents,
  which local development and the unit-test suite depend on, is preserved exactly.
- **The child's environment is constructed, not inherited.** A named helper,
  `AgentIsolation.StripOrchestratorEnvironment`, removes the secrets and the five
  topology variables. It is deliberately **not** folded into
  `DropInheritedCapabilities`, which `ProcessRunner` (git, npm) and
  `AIProviderService.RunShellAsync` (Cmd nodes) also use and where a user's command
  may legitimately rely on the inherited environment; scrubbing there would change
  both silently.
- **Stripping happens before the resolved step's environment is applied**, never
  after. What is removed is therefore only ever what was _inherited_: a preview
  that legitimately needs one of these names sets it in `ild.config.json` or the
  repository's encrypted preview `.env`, and that value survives — pointed at the
  preview's own infrastructure rather than the orchestrator's. That is what makes
  a preview of an ILD-shaped app possible at all, and it is why the three
  per-service workarounds this repository carried (`ILD_AGENT_USER: ""`,
  `ILD_ORCHESTRATOR_PRIVATE_ROOT`, `ILD_AGENT_SCRATCH_ROOT`) are gone: the
  behaviour they bought is now the default for every repository, which is where it
  belongs.
- **Preview state moves from the orchestrator-private root to the shared scratch
  root.** Logs and the npm cache are now written by the agent and read by the
  orchestrator, which is exactly what that setgid, shared-group, default-ACL tree
  exists for.
- **The npm global prefix follows the agent's home**, so `npm install -g` in an
  install step writes where the uid running it can write and the agent-uid nodes
  that later exec the tool can reach it. "The agent's home" means whatever
  `AgentIsolation.ResolveChildHome` says the crossing does to `HOME`, which is the
  same answer `Route` applies — one rule, one owner, so the prefix cannot be
  derived from a different premise than the `HOME` the child actually gets. A
  crossing configured with a user but no home leaves `HOME` inherited and the
  prefix therefore in the orchestrator's own home, where ILD creates it as it
  always did; the container exports the two together, so this is a shape only a
  hand-built deployment reaches.

## Consequences

- **Preview state is agent-readable and reachable across runs.** The private
  root's stated threat — the agent pre-creating a path derived from its own
  worktree and planting content that steps then consume _while running as the
  orchestrator_ — evaporates once those steps are the agent. What replaces it is
  the ordinary shared-group reach ADR-0014 already accepts for `/worktrees` and
  `/data/repos`: one run's agent can read another run's preview state. That is the
  existing "all runs share the `ild-agents` group" residual, not a new one, and
  per-run groups remain the follow-up that narrows it. The code comment that
  asserted the old rationale was rewritten rather than left standing, because a
  future reader would otherwise restore the private root and re-break the preview.

- **The npm prefix on the orchestrator's `PATH` is now agent-writable.** ILD
  prepends `$AGENT_HOME/.local/bin` to its own process `PATH` so tools an install
  step provisioned are resolvable to the Cmd nodes and CLI adapters that run
  afterwards, and Cmd nodes run as the orchestrator. This is a change of
  directness, not of capability: the contents of that directory were always
  agent-controlled, because what writes them is an agent-authored install command.
  It is stated here rather than left implicit, and it is the reason the entrypoint
  provisions that directory as the agent up front — a prefix the orchestrator
  created would be owned by a uid the agent is not, and the agent's own
  `npm install -g` would fail on it.

- **A preview that needs a database must be given one.** Previously it silently
  attached to the orchestrator's, which is how a preview of this repository came
  up as a second ILD instance pointed at the live database — its
  `workitem-server.log` shows it sweeping real `WorkItems` for stale heartbeats. A
  preview now gets no connection string unless its repository's preview `.env` or
  its `ild.config.json` supplies one. Whether previewing ILD _should_ reach real
  infrastructure at all is a separate question; this makes the connection explicit
  and configurable rather than automatic.

- **`$HOME` is the agent's, but the credential store behind it is still shared —
  deliberately.** The agent home's dotdirs are symlinks into the
  `/home/ild/.agent-config` store, so a nested ILD preview's Claude session opens
  already logged in against the outer instance's credentials, and can write that
  store. A clean-room preview would mean scoping `HOME` to the preview's own state
  directory, at the cost of a separate login inside every preview and of the
  preview no longer exercising the code path the real deployment uses. We keep the
  shared store: the agent could already write it (ADR-0014 lists that as a
  residual), so scoping `HOME` here would buy a partial fix for one caller while
  making previews meaningfully harder to use. Closing it belongs with the
  credential-store residual as a whole, not with this change.

- **Stopping a preview and proxying it are unaffected.** The orchestrator retains
  `CAP_KILL` precisely so it can signal agent-uid processes, and the reverse proxy
  and health checks are HTTP over a port, which is uid-agnostic.

- **A worktree previewed before this change may still hold mixed-ownership
  `bin`/`obj` trees** and needs them cleared once. See
  [Troubleshooting](../troubleshooting.md).

## Considered and rejected

- **Grant the orchestrator `CAP_FOWNER`/`CAP_DAC_OVERRIDE`** so its preview builds
  could write the agent's files. Self-defeating in the safe form and dangerous in
  the useful one: the preview child has its capability sets cleared, so it would
  never receive them anyway, and letting it keep them would hand repo-authored
  shell commands an ownership-check bypass over the entire container — inverting
  the boundary ADR-0014 draws instead of completing it.

- **A per-uid MSBuild `ArtifactsPath`**, so the two uids never write the same
  `bin`/`obj`. It works, and it treats the symptom: the builds collide because two
  trust levels share one tree, and the collision is the visible edge of the
  privilege problem, not the problem. It is also .NET-specific, where the same
  mismatch would resurface for any other toolchain a previewed repository uses.
