# Configuration

Remote providers, the WorkItem Server connection, repositories, AI providers, and runtime polling settings are managed from the UI and persisted in the ILD database. The WorkItem Server connection (URL, API key, poll/grace cadence) is a single app-wide setting edited from its own tab, no longer per remote provider. The settings below are environment- and build-time configuration.

## Environment variables

| Variable                                     | Purpose                                                                                                           |
| -------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `ILD_PASSWORD`                               | Required bootstrap password; sets the password for the bootstrap user on first login                              |
| `ILD_USERNAME`                               | Bootstrap username (defaults to `admin`); used to seed and authenticate the first user                            |
| `ILD_DB_CONNECTION_STRING`                   | PostgreSQL connection string for ILD local state                                                                  |
| `WORKITEM_DB_CONNECTION_STRING`              | PostgreSQL connection string for the WorkItem Server                                                              |
| `ILD_DATA_PATH`                              | Base data directory for ILD runtime files                                                                         |
| `ILD_WORKTREES_PATH`                         | Base directory for per-item worktrees (overrides the `DataRoot`/`worktrees` default)                              |
| `ILD_LOG_LEVEL`                              | Initial Serilog level (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`)                            |
| `ILD_SECRET_KEY`                             | Optional encryption-at-rest key for provider API keys and webhook secrets (see below)                             |
| `ILD_WORKITEM_SERVER_URL`                    | URL used to auto-seed the global WorkItem Server connection                                                       |
| `ILD_WORKITEM_SERVER_API_KEY`                | API key used to auto-seed the global WorkItem Server connection                                                   |
| `ILD_API_URL`                                | Base URL agents and the MCP server use to call back into the ILD API                                              |
| `ILD_ALLOWED_ORIGINS`                        | Comma-separated CORS origins allowed to call the ILD API                                                          |
| `ILD_PREVIEW_PROXY_BASE`                     | Origin worktree previews are served under, e.g. `http://ild.localhost:8080`. Unset ⇒ preview proxying is off      |
| `ILD_PREVIEW_PUBLIC_HOST`                    | Host used to build direct preview URLs when no proxy base is set (default `127.0.0.1`)                            |
| `ILD_SHUTDOWN_DRAIN_SECONDS`                 | Seconds the shutdown drain may spend parking in-flight runs (default `20`; see below)                             |
| `WORKITEM_API_KEYS`                          | Accepted bearer keys for the WorkItem Server (comma-separated)                                                    |
| `WORKITEM_DATA_PATH`                         | Base data directory for WorkItem Server runtime files                                                             |
| `WORKITEM_LOG_LEVEL`                         | Serilog level for the WorkItem Server (docker compose defaults it to `ILD_LOG_LEVEL`)                             |
| `GIT_CONFIG`                                 | Path to the host `.gitconfig` mounted into the ILD container (default `~/.gitconfig`) so commits inherit identity |
| `GIT_AUTHOR_NAME` / `GIT_AUTHOR_EMAIL`       | Override the git author identity for agent commits (defaults to the mounted host `.gitconfig`)                    |
| `GIT_COMMITTER_NAME` / `GIT_COMMITTER_EMAIL` | Override the git committer identity for agent commits                                                             |
| `ASPNETCORE_URLS`                            | HTTP bind address for each .NET host (standard ASP.NET Core variable)                                             |

The ILD API log level is also changeable at runtime through `PUT /api/v1/logging/level` without restarting; `ILD_LOG_LEVEL` only sets the starting level. The WorkItem Server has no runtime endpoint; its level is fixed at startup.

The ILD container additionally uses an `ILD_AGENT_TOKEN` for agent/MCP calls back into the local API. It is auto-generated at startup if unset, so you normally don't need to provide one.

## Session expiry

How long a sign-in lasts is a preference, not a secret, so it lives in the database and is edited under **Settings → Signed-in devices** — no restart, and it takes effect on the next request.

| Setting            | Default | Meaning                                      |
| ------------------ | ------- | -------------------------------------------- |
| `session.idleDays` | `30`    | Days a sign-in survives without being used   |
| `session.maxDays`  | `90`    | Days a sign-in survives however active it is |

Either accepts `0` to disable that limit, and both are capped at 3650. The defaults are deliberately generous: a single operator on their own machine should not be re-authenticating.

The two behave differently when changed. `session.idleDays` is re-evaluated on every request, so lowering it signs idle devices out at once. `session.maxDays` is stamped onto a session when it is created, so a change applies only to sign-ins made afterwards — existing devices keep the deadline they were given.

The credentials themselves stay environment variables (`ILD_PASSWORD`, `ILD_USERNAME`) because they are secrets.

## Graceful shutdown

When ILD is asked to stop — `docker compose stop`, a Kubernetes rollout, a
GitOps image bump — it parks the runs it is driving instead of dying with its
agent CLIs mid-step, and picks exactly those runs up again on the next start.
`ILD_SHUTDOWN_DRAIN_SECONDS` is how long it may spend doing so. Anything
malformed or non-positive falls back to the 20s default; `0` in particular would
silently restore the hard kill this replaces.

**The sequence.** `ApplicationStopping` fires once, before any hosted service
stops, and raises a single flag every component reads at the same moment. From
that instant no new run driver launches and the scheduler claims no new work
items, while heartbeats keep running so live runs hold the claims they already
have. The drain then, for each run this process is driving:

1. Parks it at its current node if that node is an **AI** node — `WaitingHuman`
   with the halt flag set, keeping `CurrentNodeId` and the captured agent
   session, and stamped as a shutdown halt rather than a human one. No work-item
   transition is made: leaving the item `Running` on the server is what lets the
   next start recognise the run as still ours.
2. Cancels the run, killing the agent process, park or no park. A run on a Cmd
   or Condition node is **not** parked — it is cheap to redo, so it is cancelled
   and left `Running` for ordinary crash recovery.
3. Waits for the driving loops to unwind, up to the drain timeout. This wait is
   the point rather than a courtesy: the park is written before the agent is
   killed, but each loop still has its interrupted-node bookkeeping to do on the
   way out. A timeout logs a warning and never fails the shutdown.

**Resume semantics.** On the next start a shutdown-parked run resumes through
the halt path, against the same agent session, with the prompt `"Continue where
you left off."` — the node is not re-run cold and its session context is not
discarded. Three paths look for these runs: startup recovery, the remote
work-item startup reconciler (the live path when `ILD_WORKITEM_SERVER_URL` is
set), and the stuck-run watchdog as a backstop for when the work-item server is
unreachable at startup — which is what happens when one deploy rolls both
containers.

A halt a **human** pressed is never auto-resumed by any of them. The two look
identical in the run row apart from the reason stamp, which is exactly why the
stamp exists; it is null for every row written before this feature, so those
read as human halts and are left alone.

**RecoveryPolicy still decides.** A run whose policy is `Cancel` or
`NeedsReview` gets that treatment on restart even though the shutdown was a tidy
one — the policy is an operator's explicit statement about restarts, and this is
a restart. Worktree health is checked before any resume, as it always was.

**Budgets must nest**, or the outer one cuts the inner one off mid-park:

```
ILD_SHUTDOWN_DRAIN_SECONDS (20s)
  < host shutdown timeout (drain + 5s, set automatically)
  < supervisor grace period (compose stop_grace_period / k8s terminationGracePeriodSeconds)
```

The host timeout is derived, not configured — the drain runs _inside_ the host
stop, so the host has to be willing to wait strictly longer than the drain.
Only the supervisor's grace period is outside ILD's reach; see
[deployment.md](./deployment.md#shutdown-and-run-draining) for the compose and
Kubernetes settings. If you raise the drain, raise the grace period with it.

## Secret encryption at rest

Provider API keys and webhook secrets are persisted in the ILD database. When `ILD_SECRET_KEY` is set, those columns are encrypted with AES-256-GCM before they are written; the key is derived from the variable via SHA-256, so any non-empty string is accepted (use a high-entropy value such as `openssl rand -hex 32`). The startup log reports whether encryption is enabled.

Behaviour is backwards-compatible so you can adopt it on an existing database without a data migration:

- When `ILD_SECRET_KEY` is unset, secrets are stored as plaintext (a warning is logged). Restrict access to the database volume accordingly.
- Existing plaintext rows remain readable after a key is added, and are re-written in encrypted form the next time that provider's secret is changed (a save that does not modify the secret leaves the stored value untouched). To encrypt existing secrets immediately, re-enter them on the affected providers.
- **Losing the key makes already-encrypted secrets unrecoverable.** Back it up, and treat rotating it as re-entering the affected provider secrets.

Other credentials follow their own paths: the bootstrap password is hashed (PBKDF2), and the WorkItem Server shared key is supplied at runtime via `WORKITEM_API_KEYS` / `ILD_WORKITEM_SERVER_API_KEY` rather than relying on database storage.

## ild.config.json

Place an `ild.config.json` file in the root of a repository to enable QA preview for that repo. ILD reads this file from the worktree whenever a preview is started.

```json
{
  "preview": {
    "defaultProfile": "app",
    "profiles": {
      "app": {
        "install": [...],
        "services": [...]
      }
    }
  }
}
```

### Top-level fields

| Field                    | Type   | Description                                                    |
| ------------------------ | ------ | -------------------------------------------------------------- |
| `preview.defaultProfile` | string | Profile used when `profileName` is omitted from the start call |
| `preview.profiles`       | object | Map of profile name → profile definition                       |

### Profile fields

| Field      | Type  | Description                                                                    |
| ---------- | ----- | ------------------------------------------------------------------------------ |
| `install`  | array | Ordered list of one-time setup commands run before services start (idempotent) |
| `services` | array | Ordered list of long-running services to start; each must become healthy       |

### Install step fields

| Field     | Type   | Description                                     |
| --------- | ------ | ----------------------------------------------- |
| `cwd`     | string | Working directory relative to the worktree root |
| `command` | string | Shell command to run                            |

### Service fields

| Field           | Type    | Description                                                                                                                                                                                                                                                                                                                                                                                              |
| --------------- | ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `name`          | string  | Unique service name within the profile; used to reference this service's port in other services                                                                                                                                                                                                                                                                                                          |
| `cwd`           | string  | Working directory relative to the worktree root                                                                                                                                                                                                                                                                                                                                                          |
| `command`       | string  | Shell command to start the service                                                                                                                                                                                                                                                                                                                                                                       |
| `port`          | string  | Logical port name assigned to this service (resolved to a free port at runtime)                                                                                                                                                                                                                                                                                                                          |
| `suggestedPort` | integer | Preferred port number; ILD uses it if free, otherwise picks another                                                                                                                                                                                                                                                                                                                                      |
| `env`           | object  | Environment variables injected into the service process (values may use token syntax — see below; the repository's preview `.env` overrides any name set here)                                                                                                                                                                                                                                           |
| `healthUrl`     | string  | URL polled after startup; the service is considered ready once it returns HTTP 2xx                                                                                                                                                                                                                                                                                                                       |
| `public`        | boolean | When `true`, this service's port is exposed as the primary preview URL in the UI                                                                                                                                                                                                                                                                                                                         |
| `publicUrl`     | string  | Overrides the advertised URL outright; may use `${PUBLIC_HOST}` and `${PORT}`                                                                                                                                                                                                                                                                                                                            |
| `rewriteHost`   | boolean | Default `true`. Whether the [preview proxy](./deployment.md#worktree-preview-proxy) replaces the `Host` header with the loopback address it forwards to. Leave it on for host-checking dev servers (Vite, webpack-dev-server, Rails, Django); set it to `false` only for a service that must see the browser-facing hostname, and allow the preview wildcard in that service's own configuration instead |

### Token syntax

String values in `command`, `cwd`, `env`, `healthUrl`, and `publicUrl` may contain tokens that ILD expands
at runtime. Everything expanded comes from `ild.config.json`: values supplied through the repository's
preview `.env` are secrets rather than templates and are never expanded — see
[Giving a preview its own configuration](#giving-a-preview-its-own-configuration).

| Token          | Expands to                                                                        |
| -------------- | --------------------------------------------------------------------------------- |
| `${HOST}`      | The bind host (loopback by default, overridable via `publicHost` on start)        |
| `${PORT}`      | The port allocated to this service                                                |
| `${PORT:name}` | The port allocated to the named service (for wiring services together)            |
| `${STATE_DIR}` | A per-preview state directory for data files that should not land in the worktree |

### Ports across restarts

Every port alias in the profile is allocated when the preview starts — including
aliases whose service you have not started yet, which is what lets `${PORT:name}`
resolve on a per-service start. Stopping the preview discards the allocations;
starting it again allocates afresh, so a service without a `suggestedPort` draws a
different ephemeral port on each run. Tokens are expanded per launch against the
current run's allocation, so a `${PORT:name}` cross-reference always carries the
port the referenced service is listening on **in the run that launched it**.

What a process cannot do is change its mind afterwards: an environment is fixed at
launch. Two rules follow from that, and both are enforced rather than left to you:

- **Restarting one service keeps its alias's allocation.** A service restarted on
  its own comes back on the port it already had, so every still-running service
  that references it stays correct. Only stopping the whole preview releases the
  alias.
- **A service is not started while a service it references is down.** Starting it
  would hand it a port nothing is listening on and then report it healthy — its
  own health check only asks about itself — so the start is refused with a message
  naming both services. Start the referenced service first, or start the whole
  profile, which launches everything in one run.

`suggestedPort` pins a service to a fixed port across restarts (ILD uses it when
it is free and falls back to an ephemeral port when it is not), which is worth
having for a stable browser URL. Cross-references do not need it.

### Example

The repository's own `ild.config.json` defines an `app` profile that boots three services — a WorkItem Server, the ILD API, and the Vite frontend — and wires them together via `${PORT:name}` references:

```json
{
  "preview": {
    "defaultProfile": "app",
    "profiles": {
      "app": {
        "install": [
          { "cwd": ".", "command": "command -v vp >/dev/null 2>&1 || npm install -g vite-plus" },
          { "cwd": "frontend", "command": "[ -d node_modules ] || vp install" }
        ],
        "services": [
          {
            "name": "workitem-server",
            "cwd": ".",
            "command": "dotnet run --project ILD.WorkItemServer --no-launch-profile",
            "port": "workitem-server",
            "suggestedPort": 5200,
            "env": {
              "WORKITEM_DATA_PATH": "${STATE_DIR}/workitem-data",
              "ASPNETCORE_URLS": "http://${HOST}:${PORT}"
            },
            "healthUrl": "http://127.0.0.1:${PORT}/health"
          },
          {
            "name": "api",
            "cwd": ".",
            "command": "dotnet run --project ILD.Api --no-launch-profile",
            "port": "backend",
            "suggestedPort": 5100,
            "env": {
              "ILD_PASSWORD": "letmein",
              "ILD_DATA_PATH": "${STATE_DIR}/data",
              "ILD_WORKTREES_PATH": "${STATE_DIR}/worktrees",
              "ILD_WORKITEM_SERVER_URL": "http://127.0.0.1:${PORT:workitem-server}",
              "ASPNETCORE_URLS": "http://${HOST}:${PORT}"
            },
            "healthUrl": "http://127.0.0.1:${PORT}/api/v1/health"
          },
          {
            "name": "app",
            "cwd": "frontend",
            "command": "vp dev --host ${HOST} --port ${PORT}",
            "port": "frontend",
            "suggestedPort": 3100,
            "env": {
              "ILD_API_PROXY_TARGET": "http://127.0.0.1:${PORT:backend}"
            },
            "healthUrl": "http://127.0.0.1:${PORT}/",
            "public": true,
            "rewriteHost": true
          }
        ]
      }
    }
  }
}
```

`app` runs Vite, which rejects requests whose `Host` it does not recognise, so it keeps the default `"rewriteHost": true` and sees the request as if it arrived on its own loopback port. The value is spelled out here only to show the field; omitting it means the same thing.

### What a preview process runs as

Your preview is a foreign application, and ILD treats it as one. Under
[agent uid isolation](#agent-uid-isolation) a preview's install steps and
services run as the **`agent`** user — the same user the coding agent runs as, and
not the one ILD itself runs as — with an environment ILD builds rather than one
they inherit wholesale ([ADR-0016](adr/0016-preview-runs-as-the-agent.md)). With
`AGENT_USER` empty the container is single-uid and everything runs as the runtime
user, exactly as before.

Worth keeping in view throughout this section: `ild.config.json` lives in the
worktree, so the coding agent working on that work item can edit the commands a
preview runs, and can start a preview itself through the ILD MCP tools. Whatever a
preview process is given, an agent-authored command can read.

Three things follow that are worth knowing when you write a profile:

- **Files your preview creates are owned by `agent`**, the same user that owns
  everything the coding agent wrote in the worktree. This is what lets a preview
  build in a worktree the agent has already built in — a build that spans two
  users fails on ownership-gated operations no permission scheme can fix (see
  [Troubleshooting](troubleshooting.md)).

- **Your commands do not see ILD's own secrets.** ILD removes them from the
  environment before your command runs: both of its database connection strings,
  `ILD_SECRET_KEY`, `ILD_PASSWORD`, `ILD_USERNAME`, the API tokens it uses to
  reach itself and the WorkItem Server, and anything you have named in
  `ILD_AGENT_ENV_DENYLIST`. It also removes the five variables describing its own
  uid topology (`ILD_AGENT_USER`, `ILD_AGENT_GROUP`, `ILD_AGENT_HOME`,
  `ILD_AGENT_SCRATCH_ROOT`, `ILD_ORCHESTRATOR_PRIVATE_ROOT`), which describe the
  ILD process and are wrong for anything else. Everything else is inherited as
  before. This matters even if your app reads none of those names, because your
  _shell command_ still ran with them in `env` — one debug `printenv`, a crash
  reporter that captures the environment, or a script that forwards it somewhere
  was enough to carry them out.

- **`${STATE_DIR}` lives under the shared scratch root** (`/tmp/ild-agent-scratch`
  by default), because your preview writes there and now does so as `agent`. It is
  still per-worktree and still discarded with the container. Your service's **log
  file is not** in it: ILD opens, appends to and serves those itself, so they stay
  in a directory only ILD can reach. Read them through the Preview tab or
  `get_preview_logs` rather than by path — if your service wants a log file of its
  own to manage, put that under `${STATE_DIR}`.

### Giving a preview its own configuration

Nothing is inherited on your behalf, so anything your app needs — a database URL,
an API key, a bucket name — you supply, in the repository's encrypted preview
`.env` (Repositories page, or a work item's **Preview** tab) or in a service's
`env` block. The `.env` is the better of the two for a credential: it is stored
encrypted, never written into the worktree, and never returned through the agent
API, so it does not end up committed to your repository or readable straight out
of ILD.

**When both set the same name, the `.env` wins.** The order a preview process's
environment is built in is: ILD's base defaults, then the service's `env` block,
then the `.env` last. `ild.config.json` lives in the worktree and is what the
profile's author committed; the `.env` is what you typed for this ILD, so it is
the one that gets the final say — otherwise a value you can see in the UI would
be silently losing to a file only a commit can change.

Two consequences of that order are worth knowing:

- **A leftover `.env` line overrides a computed value.** A service `env` entry
  built from `${PORT:name}` or `${STATE_DIR}` is only used if the `.env` does not
  also set that name. If you once pinned `ILD_API_PROXY_TARGET` to a fixed port
  and left the line in, every later run gets the stale port rather than the one
  the referenced service is actually listening on — with no warning, because a
  deliberate override is exactly what it looks like. When a service reaches the
  wrong port, read the `.env` first.
- **`.env` values are never expanded.** They are secrets, not templates, so they
  arrive at the process byte-for-byte: a `${` in a password stays a `${`. [Token
  syntax](#token-syntax) applies to what `ild.config.json` declares, not to what
  you type here.

**It is not a vault, though, and the difference matters.** ILD injects the `.env`
into every preview process, which is the whole point of it — and the commands
those processes run come from `ild.config.json`, which the coding agent writes. An
install step of `env > leak.txt` hands the values back, exactly the way it would
have handed back ILD's own secrets before this release. So the rule is the same
one that governs the credentials on your laptop: **scope what you put here to the
preview's own infrastructure.** A throwaway database, a sandbox API key, a bucket
you would not mind someone emptying. Not production, and not a credential that
also unlocks something else.

Removing a name from what is _inherited_ does not stop you setting it
deliberately. The strip runs before your values are applied, so anything you set
wins — including one of ILD's own names, if your app genuinely uses it. Point it
at your own infrastructure rather than expecting ILD's.

This is the only migration step for an existing profile: if a service used to come
up without configuration you never wrote down, it was reading ILD's, and it now
needs its own.

**Previewing an ILD-shaped app.** If the application you are previewing reads the
same `ILD_*` variables ILD does — in practice, ILD itself; this repository's own
profile boots one — it is now cleanly separated from the outer instance rather
than quietly sharing its identity, private directories and database. Give it its
own `ILD_DB_CONNECTION_STRING` and `WORKITEM_DB_CONNECTION_STRING`; a second ILD
runs its background services for real and would otherwise act on the outer
instance's work items. `HOME` remains the shared agent home, so an agent CLI
inside the preview is already logged in against the same credential store —
[ADR-0016](adr/0016-preview-runs-as-the-agent.md) records why that is deliberate.

## Build-time container options

Set as build args (e.g. in `.env` consumed by `docker compose build`):

| Build arg         | Purpose                                                                       |
| ----------------- | ----------------------------------------------------------------------------- |
| `WITH_NODE`       | Install Node.js tooling in the ILD image                                      |
| `WITH_DOTNET_SDK` | Base the ILD image on the .NET SDK image instead of the ASP.NET runtime image |
| `WITH_CHROME`     | Install Chrome in the ILD image                                               |
| `WITH_CERTS`      | Import `.crt` or `.pem` files from `certs/` at build time                     |
| `AGENT_UID`       | uid of the lower-trust `agent` user the coding agent runs as (default 10002)  |
| `AGENT_GID`       | gid of that user (default 10002)                                              |
| `SHARED_GID`      | gid of the `ild-agents` group shared by both users (default 10003)            |

### Agent uid isolation

The orchestrator and the coding-agent CLI run as **two different users** — `ild`
and `agent` — so the agent cannot read the orchestrator's memory or its private
files. See [ADR-0014](adr/0014-agent-uid-isolation.md) for the design. The
`AGENT_UID`/`AGENT_GID`/`SHARED_GID` build args only matter if those ids collide
with something else on your host for a bind-mounted volume.

The split is controlled at runtime by `AGENT_USER`. Setting it to an empty string
turns isolation off entirely — the container comes up single-uid, exactly as it
did before the split:

```yaml
services:
  ild:
    environment:
      - AGENT_USER= # disable uid isolation
```

That is the escape hatch the container's `FATAL:` startup messages point at. It
is one switch on purpose: the app-side variables are derived from it at startup,
so isolation is never half-on. The container refuses to start rather than
degrade silently if isolation is requested but `capsh`/`setpriv`, the ambient
capabilities, or the shared group are missing.

Under isolation the orchestrator's own secrets — the DB connection strings,
`ILD_SECRET_KEY`, `ILD_PASSWORD`, and the API tokens/keys it uses to reach itself
and the WorkItem server — are stripped from the agent's environment so the
lower-trust agent uid never sees them. If you introduce additional secret
environment variables that the orchestrator reads but the agent must not, list
their names (comma-separated) in `ILD_AGENT_ENV_DENYLIST` and they are stripped
too. The agent's git commit identity (`GIT_AUTHOR_*`/`GIT_COMMITTER_*`) and any
provider API key an adapter passes to the CLI are kept. The same denylist governs
what a Worktree Preview's processes inherit — see
[What a preview process runs as](#what-a-preview-process-runs-as).

The coding agents (Pi, OpenCode, Claude Code, GitHub Copilot) are **not** baked into the image.
They install on demand onto the persistent `/data` volume and are updated there
without rebuilding the image. `WITH_NODE` must be on, since those installs and
version checks use Node/npm. The agent a configured AI provider needs is
**installed automatically** — at startup for any provider that already exists,
and when a provider using a not-yet-installed agent is added — so a fresh or
upgraded deployment doesn't fail its first AI run on a missing CLI. Installs run
in the background; the **AI Provider** page shows each agent's current/latest
version, lets you trigger an install or update manually, and reports failures
(e.g. the npm registry being unreachable).

Toolchain versions are also configurable: `NODE_VERSION`, `DOTNET_VERSION`, and
`NODE_RUNTIME_VERSION`. With `WITH_DOTNET_SDK=1` the image is based on
`mcr.microsoft.com/dotnet/sdk:$DOTNET_VERSION`, so the SDK available to agents
tracks `DOTNET_VERSION` rather than a separate channel.

## AI provider configuration

Each AI provider stores a free-form JSON config blob (`AiProvider.Config`) that
tunes the adapter it runs. Fields are schema-driven — every adapter advertises
its own set — and apply to **every repository** that provider runs in, because
the config lives on the provider, not in a target repo.

### Custom MCP servers (JSON)

The **OpenCode** and **Claude Code** adapters expose a `customMcpServersJson`
field that attaches arbitrary [MCP](https://modelcontextprotocol.io) servers to
the agent, on top of the built-in `ild` server. This lets you create provider
variants that differ only by the tools they carry — e.g. a plain **OpenCode**
provider and an **OpenCode w/chrome** provider whose agents can drive a headless
browser for debugging and screenshots.

Set it on the **AI Providers** page: create or edit an OpenCode or Claude Code
provider and fill in the **Custom MCP servers (JSON)** field. The value is stored
on the provider (`AiProvider.Config`) and applies to every run that uses it. It
is _not_ a per-loop-node setting — the Loop Editor's node settings do not carry
provider config.

The value is a JSON object mapping a server name to its definition:

```json
{
  "chrome-devtools": {
    "command": [
      "npx",
      "-y",
      "chrome-devtools-mcp@latest",
      "--headless",
      "--isolated",
      "--no-sandbox"
    ]
  }
}
```

- `command` may be a single string or an array of argv tokens.
- `args` (optional array) is appended after `command`.
- `env` (optional object) becomes the server process's environment.

Each adapter translates this into its own native shape (OpenCode's
`{ "type": "local", "command": [...], "environment": {...} }` and Claude Code's
`{ "command": ..., "args": [...], "env": {...} }`) and merges it alongside the
`ild` entry. The name `ild` is reserved and any custom server using it is
ignored, so it can never clobber the built-in server. For Claude Code the custom
servers are injected even when the `ild` tool is disabled for the node.

Invalid or partially-malformed JSON is ignored and **never fails an AI node
run** — the parser fails open, keeping whatever well-formed servers it can and
skipping the rest.

The **Pi** adapter has no MCP support by design and does not expose this field.
**GitHub Copilot** is not currently wired for MCP in ILD, so it does not expose
it either.

> The `chrome-devtools` example above requires Chrome in the ILD image
> (`WITH_CHROME`) and Node/npm (`WITH_NODE`). `--no-sandbox` is required because
> agents run as a non-root user.
