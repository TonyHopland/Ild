# ADR-0014: The coding agent runs under a separate OS user from the orchestrator

The orchestrator (`dotnet ILD.Api.dll`, PID 1) and the coding-agent CLI it spawns
(claude / opencode / pi / copilot) now run as **two distinct, non-root Linux
users** — `ild` (uid 10001) for the orchestrator and `agent` (uid 10002) for the
CLI — instead of sharing the single `ild` uid. Same-uid processes share a trust
boundary: with `ptrace` they can read each other's memory, and they can read each
other's files. Because the agent runs with `--permission-mode bypassPermissions`
(and equivalents) it is effectively an unsandboxed executor of untrusted model
output, so its only real containment is the OS. Sharing the orchestrator's uid
let it read the orchestrator's DB handles, run-scoped tokens and secrets, or hijack
the orchestrator via `ptrace`. Splitting the uid removes that direct access.

It does **not** make the agent fully contained — see _What this does not close_
below. The claim is narrower: the agent can no longer read the orchestrator's
memory or its private files by virtue of being the same user.

## How the boundary is enforced

- **Two users, one shared group.** `ild` owns `/app` and `/data` (the private data
  root: secrets, config, the repo store's parent). `agent` owns `/home/agent`. A
  shared group `ild-agents` (gid 10003), with both users as members, gates the
  paths both must touch. They come in two flavours:
  - **read/write** — `/worktrees` (agent writes its worktree, orchestrator reads
    results), `/data/repos` (git worktrees commit through the base repo's object
    store), `/data/chat-sessions`, and the credential store
    `/home/ild/.agent-config`: group-owned by `ild-agents`, `setgid` (new entries
    inherit the group), plus a default POSIX ACL granting `g:ild-agents:rwx` so a
    file created by either uid stays read/write for the other.
  - **read-only** — `/data/agents`, the npm-installed CLIs. The agent execs them
    but must not rewrite them, because the orchestrator runs those same binaries
    as `ild` (version checks, the provider terminal); a writable install would be
    a way straight back across the boundary. Mode `2755` with a `g:ild-agents:r-x`
    default ACL; the orchestrator still installs/updates them as the owner. These
    CLIs are installed onto `/data` at **runtime**, so the boot-time pass has to
    strip group-write that an install introduced (npm writes group-writable under
    the `umask 002` below) — its tripwire checks for excess permission here, not
    just missing permission as the read/write one does. During the install itself
    the version directory is closed to the agent outright (owner-only) and only
    reopened once the tree is complete and write has been stripped: npm builds
    `node_modules` as the orchestrator under `umask 002` with the shared group
    inherited from the setgid parent, so it is agent-writable while it is being
    assembled — and it is published before the `current` pointer flips to it, so
    the tree the pointer names is never one the agent could still edit.

  Reach is granted **through the group, never through the "other" bits**. `/data`
  is `0710` group `ild-agents`: `agent` can traverse it to reach the shared
  subtrees by exact path but cannot list it or read private sibling files — and a
  process outside the two uids gets nothing. It is deliberately not setgid, so
  state the orchestrator writes directly there keeps its own private group. The
  shared roots are likewise `2770`/`2750` rather than `2775`/`2755`: reaching
  anything inside requires traversing the root, so closing "other" there makes the
  whole subtree group-only whatever the modes on individual entries say.
  `/home/ild` is `0710` group `ild-agents` — the agent's dotdirs are symlinks into
  the store beneath it, so it must traverse (never list) that home, and `useradd`'s
  Debian default (`HOME_MODE 0750`, group `ild`) would break both the shared
  credentials and the git identity.

  The one deliberate exception is `/app`, which stays world-readable: the agent
  reads the MCP-server assemblies from there, and group-owning it would mean a
  recursive chown over the published output — an extra image layer shipping the
  build twice — to protect read-only code that is in the image anyway.

- **Cross-user spawn via retained capability, not setuid.** .NET has no native
  cross-user spawn on Linux, and an unprivileged process cannot switch uid. The
  entrypoint therefore drops the orchestrator to `ild` while retaining **ambient**
  `CAP_SETUID`/`CAP_SETGID`/`CAP_KILL` (via `capsh --keep=1 --user ... --addamb=...`),
  so the orchestrator — and only the orchestrator — can drop a child to `agent`.
  `CAP_KILL` is not optional: `setpriv` gives the agent a different real _and_
  saved uid, so without it `kill(2)` returns `EPERM` and both Halt and the
  per-node timeouts would leave an orphaned agent writing the worktree while the
  engine moved on to commit/cleanup. Each
  agent launch is wrapped as `setpriv --reuid=agent --regid=agent --init-groups
--inh-caps=-all --ambient-caps=-all -- <cmd>`, which switches uid/gid, loads the
  shared group, and clears the inheritable + ambient capability sets — enough that
  the agent binary's post-exec permitted set (`(inheritable & file-caps) |
ambient`) is empty (a non-root→non-root setuid does not auto-clear caps, so this
  is explicit; the bounding set is left alone because dropping it would need
  `CAP_SETPCAP`, which the orchestrator deliberately does not hold). This
  is chosen over a setuid-root `sudo`/`gosu` helper so that it will survive the
  `no_new_privs` / `ptrace_scope=2` hardening — a setuid bit or file capability is
  ignored under `no_new_privs`, whereas a capability the orchestrator already
  holds is not. **That hardening is not part of this change**: no `security_opt`
  or `sysctls` are set here, and `kernel.yama.ptrace_scope` is not namespaced so
  it cannot be set per-container anyway. It is separate (host/compose-level) work;
  this design is only built so as not to have to be redone when it lands.

- **One code seam.** Every agent launch goes through
  `CliAgentAdapterBase.StartAgentProcess`, which applies
  `AgentIsolation.Route(ProcessStartInfo)` and starts the process — so "a CLI
  launch crosses to the agent uid" is owned in one place rather than remembered at
  each of the adapters' call sites. Routing is a no-op unless `ILD_AGENT_USER` is
  set, so local development, unit tests and any single-uid deployment keep the
  pre-isolation behavior unchanged; the container image sets the variable.
  `ProcessRunner` (git, npm) is deliberately **not** routed — those are
  orchestrator operations and must keep running as `ild`.

- **The login terminal runs as the agent too, because file modes decide.** The
  agent CLIs' login state (`~/.claude`, `~/.opencode`, …) lives in the
  `/home/ild/.agent-config` volume, symlinked into **both** home directories. But
  the group and the default ACL are not enough on their own: these CLIs write
  their credentials owner-only (`~/.claude/.credentials.json` is `0600`,
  opencode's `auth.json` likewise), and a `0600` create clamps the ACL mask to
  nothing, so no group grant can widen it after the fact. The interactive provider
  terminal therefore runs its CLI as `agent` as well — the uid that later has to
  read those files creates them — instead of being a second, unrouted CLI launch
  as `ild`. Files the CLIs do _not_ explicitly restrict (e.g. the
  `.claude/projects` session transcripts the orchestrator snapshots) are created
  under the container's `umask 002`, so they stay group-readable.
  `agent` still cannot read `/data`'s private secrets or the orchestrator's memory.

- **git needs `safe.directory` set system-wide.** `git worktree add` runs as the
  orchestrator, so the worktree, its `.git` file and the gitdir under
  `/data/repos/<repo>/.git/worktrees/<name>` are owned by `ild` — and since 2.35.2
  git refuses to operate on a repository whose owner's uid differs from
  `geteuid()`, regardless of group and mode. Without `git config --system --add
safe.directory '*'` in the image, every git command the agent runs (the review
  prompts use `git log`/`diff`/`status`, and it commits its own work) dies with
  "detected dubious ownership". It has to be system-level — git ignores the
  setting from repository config, and the agent's `.gitconfig` is a symlink onto
  the read-only host mount. This gives nothing away: the check guards against
  picking up a repo owned by some _other_ user, whereas both uids are ours and the
  sharing is deliberate.

- **Orchestrator-private state lives under a root the entrypoint pre-creates.** A
  predictable path in world-writable `/tmp` is harmless while everything is one
  uid, and a privilege-escalation route once it is not: the agent can create the
  path first, and orchestrator code that guards on "does it already exist?" then
  trusts the agent's version. The git askpass helper is the sharp case — it is
  executed by the orchestrator with the repository token in its environment. The
  entrypoint therefore creates `ORCHESTRATOR_PRIVATE_DIR` owner-only (`0700`)
  before anything else runs and exports it as `ILD_ORCHESTRATOR_PRIVATE_ROOT`,
  exactly as it does for the shared scratch root. What closes the attack is that
  mode plus the root existing first — not the location and not path
  unpredictability — so this state stays on `/tmp`, where it was before and where
  it is discarded with the container rather than accumulating forever on the data
  volume. The app resolves the path to an absolute one unconditionally: it is
  handed to git as `GIT_ASKPASS`, and git runs with the worktree as its cwd, so a
  relative root would resolve inside the worktree and break every authenticated
  fetch and push.

- **Shared scratch is a setgid tree, not a per-directory grant.** The orchestrator
  regularly _seeds a file the agent then keeps writing_ — Pi's restored session
  transcript is written by the orchestrator and appended to by pi for the rest of
  the turn. Granting the directory cannot express that: create/unlink/rename are
  governed by the directory, but writing an existing file is governed by that
  file's own mode. So this scratch is rooted at `AGENT_SCRATCH_DIR`
  (`/tmp/ild-agent-scratch`, set up like the other shared trees), and the seeded
  file inherits the shared group and `umask 002` on its own. It is the same
  mechanism that already made the equivalent claude path work, whose transcripts
  live in the shared config store. It sits under `/tmp` so it is discarded with
  the container instead of growing on a volume.

## What this does not close

The uid split removes the agent's _direct_ read of orchestrator memory and private
files. It does not make the agent a contained tenant: several shared paths are
inputs to code the trusted side later executes as `ild`, so a hostile agent still
has indirect routes across the boundary. These are known and accepted for now
rather than overlooked:

- **`/data/repos` is writable by the agent** and has to be — a git worktree commits
  into the base repo's object store. That also puts each base repo's
  `.git/config` and `.git/hooks` in reach, and the orchestrator's own git runs
  against those as `ild` on every worktree add/fetch.
- **The preview service executes agent-authored commands as `ild`.** The command
  comes from the worktree's `ild.config.json`, which the agent writes, and the
  agent can trigger it itself through the ILD MCP tools. Capabilities are stripped
  from it (above), so the ceiling is `ild`, not root — but it remains a route.
  Moving the preview to the agent uid outright would close it.
- **The shared credential store is writable by the agent**, so it can write e.g.
  `.claude/settings.json` hooks, which then execute as `ild` when a human opens
  the provider login terminal.
- **All runs share the `ild-agents` group**, so one run's agent can reach another
  run's worktree and any repo in the store.
- **The agent inherits the orchestrator's environment**, including
  `ILD_DB_CONNECTION_STRING`. This is unchanged from the single-uid design (a
  same-uid child inherited it too), but the uid split does not fix it.

Narrowing these is follow-up work: per-run uids/groups, brokering secrets out of
the agent's environment, and treating the shared git/credential state as
attacker-controlled input on the orchestrator side.

## Consequences

- The orchestrator holds `CAP_SETUID`/`CAP_SETGID`/`CAP_KILL`. It is the trusted
  component; the untrusted agent holds **no** capabilities (the wrap strips them).
  This mirrors how service managers (nginx master, etc.) retain just enough
  privilege to fork and signal workers.
- **Ambient capabilities are stripped from every child that touches
  agent-authored input, not just from the agent.** Ambient capabilities are
  inherited by _all_ descendants, in the permitted and effective sets. That would
  be harmless for children whose input the orchestrator controls, but some
  orchestrator-side commands exist to execute agent-authored input — the preview
  service runs the worktree's `ild.config.json` command, and npm/git run against
  agent-writable `package.json` and `.git/config`/`hooks`. A process with
  effective `CAP_SETUID` can `setuid(0)`, and an exec with euid 0 is treated as if
  the file's capability sets were all ones, so its permitted set becomes the full
  bounding set: without this, hijacking one of those commands would have escalated
  from "runs as the orchestrator" to "runs as container root" — the uid split
  would have _raised_ the ceiling of a successful escape while lowering its
  everyday reach. Every orchestrator-side spawn that can reach agent-authored
  input therefore goes through `AgentIsolation.DropInheritedCapabilities` —
  `ProcessRunner` (git, npm), `AIProviderService.RunShellAsync`, and both
  `WorktreePreviewService` spawn sites — which wraps them in
  `setpriv --inh-caps=-all --ambient-caps=-all` (no uid change, needs no
  privilege).
- Splitting `$HOME` means any CLI state **not** listed in
  `AGENT_CONFIG_DIRS`/`AGENT_CONFIG_FILES` is no longer shared between the login
  terminal and the agent run — it would silently read as logged-out on the agent
  side. The list therefore has to name XDG locations explicitly (e.g. opencode's
  `.config/opencode` and `.local/share/opencode`, which is where it keeps auth),
  and adding an adapter means checking where its CLI stores credentials.
- The residual cross-boundary routes above (`/data/repos`, the credential store,
  the shared group, the inherited environment) are the follow-up backlog; changes
  there should keep the group/ACL scheme rather than re-hardcoding single-owner
  ownership.
- `WorkItemServer` keeps its original single-uid `gosu` drop — it spawns no agent
  and needs no split (the entrypoint's split path only activates when `AGENT_USER`
  is set).
