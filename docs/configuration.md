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
| `WORKITEM_API_KEYS`                          | Accepted bearer keys for the WorkItem Server (comma-separated)                                                    |
| `WORKITEM_DATA_PATH`                         | Base data directory for WorkItem Server runtime files                                                             |
| `WORKITEM_LOG_LEVEL`                         | Serilog level for the WorkItem Server (docker compose defaults it to `ILD_LOG_LEVEL`)                             |
| `GIT_CONFIG`                                 | Path to the host `.gitconfig` mounted into the ILD container (default `~/.gitconfig`) so commits inherit identity |
| `GIT_AUTHOR_NAME` / `GIT_AUTHOR_EMAIL`       | Override the git author identity for agent commits (defaults to the mounted host `.gitconfig`)                    |
| `GIT_COMMITTER_NAME` / `GIT_COMMITTER_EMAIL` | Override the git committer identity for agent commits                                                             |
| `ASPNETCORE_URLS`                            | HTTP bind address for each .NET host (standard ASP.NET Core variable)                                             |

The ILD API log level is also changeable at runtime through `PUT /api/v1/logging/level` without restarting; `ILD_LOG_LEVEL` only sets the starting level. The WorkItem Server has no runtime endpoint; its level is fixed at startup.

The ILD container additionally uses an `ILD_AGENT_TOKEN` for agent/MCP calls back into the local API. It is auto-generated at startup if unset, so you normally don't need to provide one.

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
| `env`           | object  | Environment variables injected into the service process (values may use token syntax — see below)                                                                                                                                                                                                                                                                                                        |
| `healthUrl`     | string  | URL polled after startup; the service is considered ready once it returns HTTP 2xx                                                                                                                                                                                                                                                                                                                       |
| `public`        | boolean | When `true`, this service's port is exposed as the primary preview URL in the UI                                                                                                                                                                                                                                                                                                                         |
| `publicUrl`     | string  | Overrides the advertised URL outright; may use `${PUBLIC_HOST}` and `${PORT}`                                                                                                                                                                                                                                                                                                                            |
| `rewriteHost`   | boolean | Default `true`. Whether the [preview proxy](./deployment.md#worktree-preview-proxy) replaces the `Host` header with the loopback address it forwards to. Leave it on for host-checking dev servers (Vite, webpack-dev-server, Rails, Django); set it to `false` only for a service that must see the browser-facing hostname, and allow the preview wildcard in that service's own configuration instead |

### Token syntax

String values in `command`, `env`, and `healthUrl` may contain tokens that ILD expands at runtime:

| Token          | Expands to                                                                        |
| -------------- | --------------------------------------------------------------------------------- |
| `${HOST}`      | The bind host (loopback by default, overridable via `publicHost` on start)        |
| `${PORT}`      | The port allocated to this service                                                |
| `${PORT:name}` | The port allocated to the named service (for wiring services together)            |
| `${STATE_DIR}` | A per-preview state directory for data files that should not land in the worktree |

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

Note what this profile does **not** contain: any connection string, API key, or
password beyond the throwaway `letmein` bootstrap. Those come from the
repository's encrypted preview `.env`, and they have to come from somewhere — see
below.

### What a preview process runs as, and what it inherits

Under [agent uid isolation](#agent-uid-isolation) a preview's install steps and
services run as the **`agent`** user, not as the orchestrator. The commands come
from `ild.config.json`, a file the coding agent writes, so they get exactly the
privileges the agent already has and nothing more
([ADR-0016](adr/0016-preview-runs-as-the-agent.md)). With `AGENT_USER` empty the
container is single-uid and they run as the runtime user, exactly as before.

Three consequences worth knowing when you write a profile:

- **The environment is constructed, not inherited.** ILD removes its own secrets
  (both DB connection strings, `ILD_SECRET_KEY`, `ILD_PASSWORD`, `ILD_USERNAME`,
  and the API tokens it uses to reach itself and the WorkItem Server, plus
  anything named in `ILD_AGENT_ENV_DENYLIST`) and the five variables describing
  its own uid topology (`ILD_AGENT_USER`, `ILD_AGENT_GROUP`, `ILD_AGENT_HOME`,
  `ILD_AGENT_SCRATCH_ROOT`, `ILD_ORCHESTRATOR_PRIVATE_ROOT`). Everything else is
  inherited as before.

- **A preview that needs a database must be given its own connection string**, in
  the repository's preview `.env` or the service's `env` block. Removing a name
  from what is _inherited_ does not stop you setting it deliberately — the strip
  runs before your values are applied, so anything you set wins. Before this,
  a previewed app that read `ILD_DB_CONNECTION_STRING` from the environment
  silently attached to ILD's own database.

- **`${STATE_DIR}` lives under the shared scratch root** (`/tmp/ild-agent-scratch`
  by default), not under the orchestrator-private root, because both uids now
  touch it: the preview writes there and ILD reads the service logs back for the
  Preview tab and `get_preview_logs`. It is still per-worktree and still
  discarded with the container.

### Previewing ILD inside ILD

This repository's own profile boots a second ILD. That works with no per-repo
workaround: the nested instance inherits none of the outer one's identity, so it
comes up single-uid, and its interactive provider terminal opens instead of
failing with `setpriv: setresuid failed`. It does need its own
`ILD_DB_CONNECTION_STRING` and `WORKITEM_DB_CONNECTION_STRING` in the
repository's preview `.env` — pointed at a database of its own, since a nested
ILD runs its background services for real and would otherwise sweep the outer
instance's work items. `HOME` is still the shared agent home, so the nested
instance's Claude session is already logged in against the same credential store;
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
[What a preview process runs as](#what-a-preview-process-runs-as-and-what-it-inherits).

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
