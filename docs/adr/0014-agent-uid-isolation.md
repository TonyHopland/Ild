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
the orchestrator via `ptrace`. Splitting the uid closes that hole.

## How the boundary is enforced

- **Two users, one shared group.** `ild` owns `/app` and `/data` (the private data
  root: secrets, config, the repo store's parent). `agent` owns `/home/agent`. A
  shared group `ild-agents` (gid 10003), with both users as members, gates the
  paths both must touch: `/worktrees` (agent writes its worktree, orchestrator
  reads results), `/data/repos` (git worktrees share the base repo's object
  store), `/data/agents` (agent execs the npm-installed CLIs the orchestrator
  installs), `/data/chat-sessions`, and the credential store
  `/home/ild/.agent-config`. Those shared paths are group-owned by `ild-agents`,
  `setgid` (new files inherit the group), and carry a default POSIX ACL granting
  `g:ild-agents:rwx` so a file created by either uid stays read/write for the
  other. `/data` itself is mode `0711`: `agent` can traverse it to reach the shared
  subtrees by exact path but cannot list it or read private sibling files.

- **Cross-user spawn via retained capability, not setuid.** .NET has no native
  cross-user spawn on Linux, and an unprivileged process cannot switch uid. The
  entrypoint therefore drops the orchestrator to `ild` while retaining **ambient**
  `CAP_SETUID`/`CAP_SETGID` (via `capsh --keep=1 --user ... --addamb=...`), so the
  orchestrator — and only the orchestrator — can drop a child to `agent`. Each
  agent launch is wrapped as `setpriv --reuid=agent --regid=agent --init-groups
--inh-caps=-all --ambient-caps=-all -- <cmd>`, which switches uid/gid, loads the
  shared group, and clears the inheritable + ambient capability sets — enough that
  the agent binary's post-exec permitted set (`(inheritable & file-caps) |
ambient`) is empty (a non-root→non-root setuid does not auto-clear caps, so this
  is explicit; the bounding set is left alone because dropping it would need
  `CAP_SETPCAP`, which the orchestrator deliberately does not hold). This
  is chosen over a setuid-root `sudo`/`gosu` helper because it survives the
  `no_new_privs` hardening that lands alongside the `ptrace_scope=2` sysctl — a
  setuid bit / file capability would be ignored under `no_new_privs`, but a
  capability the orchestrator already holds is not.

- **One code seam.** All four adapters route their launch through
  `AgentUserLauncher.Route(ProcessStartInfo)`. It is a no-op unless `ILD_AGENT_USER`
  is set, so local development, unit tests and any single-uid deployment keep the
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

## Consequences

- The orchestrator holds `CAP_SETUID`/`CAP_SETGID`. It is the trusted component;
  the untrusted agent holds **no** capabilities (the wrap strips them). This mirrors
  how service managers (nginx master, etc.) retain just enough privilege to fork
  workers. Because the caps are _ambient_, the orchestrator's other trusted
  subprocesses (git, npm) inherit them too and simply never use them; the one path
  that strips them is the agent drop, which is the only untrusted child. Tightening
  that to strip caps from the other children as well is a cheap future hardening.
- Cross-repo isolation is not yet complete: every run's agent shares the
  `ild-agents` group, so it can reach any repo under `/data/repos` and any other
  run's worktree. Per-run isolation and secret-brokering are follow-up work and
  should tighten these grants without re-hardcoding single-owner ownership.
- `WorkItemServer` keeps its original single-uid `gosu` drop — it spawns no agent
  and needs no split (the entrypoint's split path only activates when `AGENT_USER`
  is set).
