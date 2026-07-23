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
    default ACL; the orchestrator still installs/updates them as the owner.

  `/data` itself is mode `0711`: `agent` can traverse it to reach the shared
  subtrees by exact path but cannot list it or read private sibling files.
  `/home/ild` is explicitly `0755` — the agent's dotdirs are symlinks into the
  store beneath it, and `useradd`'s Debian default (`HOME_MODE 0750`) would break
  both the shared credentials and the git identity.

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
  `AgentUserLauncher.Route(ProcessStartInfo)` and starts the process — so "a CLI
  launch crosses to the agent uid" is owned in one place rather than remembered at
  each of the adapters' call sites. Routing is a no-op unless `ILD_AGENT_USER` is
  set, so local development, unit tests and any single-uid deployment keep the
  pre-isolation behavior unchanged; the container image sets the variable.
  `ProcessRunner` (git, npm) is deliberately **not** routed — those are
  orchestrator operations and must keep running as `ild`.

- **Credentials are shared, secrets are not.** The agent CLIs' login state
  (`~/.claude`, `~/.opencode`, …) lives in the `/home/ild/.agent-config` volume and
  is symlinked into **both** home directories, so the interactive login terminal
  (which still runs as `ild`) and the agent run (as `agent`) see the same store.
  The store is group-shared, so the orchestrator can read the agent's credentials —
  that direction is fine (the orchestrator is the trusted side). The property that
  matters is the reverse: `agent` cannot read `/data`'s private secrets or the
  orchestrator's process memory.

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
  privilege to fork and signal workers. Because the caps are _ambient_, the
  orchestrator's other trusted subprocesses (git, npm) inherit them too and simply
  never use them; the one path that strips them is the agent drop, which is the
  only untrusted child. Tightening that to strip caps from the other children as
  well is a cheap future hardening.
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
