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

| Field           | Type    | Description                                                                                       |
| --------------- | ------- | ------------------------------------------------------------------------------------------------- |
| `name`          | string  | Unique service name within the profile; used to reference this service's port in other services   |
| `cwd`           | string  | Working directory relative to the worktree root                                                   |
| `command`       | string  | Shell command to start the service                                                                |
| `port`          | string  | Logical port name assigned to this service (resolved to a free port at runtime)                   |
| `suggestedPort` | integer | Preferred port number; ILD uses it if free, otherwise picks another                               |
| `env`           | object  | Environment variables injected into the service process (values may use token syntax — see below) |
| `healthUrl`     | string  | URL polled after startup; the service is considered ready once it returns HTTP 2xx                |
| `public`        | boolean | When `true`, this service's port is exposed as the primary preview URL in the UI                  |

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
              "WORKITEM_API_KEYS": "preview-api-key",
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
              "ILD_WORKITEM_SERVER_API_KEY": "preview-api-key",
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
            "public": true
          }
        ]
      }
    }
  }
}
```

## Build-time container options

Set as build args (e.g. in `.env` consumed by `docker compose build`):

| Build arg         | Purpose                                                   |
| ----------------- | --------------------------------------------------------- |
| `WITH_NODE`       | Install Node.js tooling in the ILD image                  |
| `WITH_DOTNET_SDK` | Install the .NET SDK in the ILD image                     |
| `WITH_CHROME`     | Install Chrome in the ILD image                           |
| `WITH_CERTS`      | Import `.crt` or `.pem` files from `certs/` at build time |

The coding agents (Pi, OpenCode, Claude Code) are **not** baked into the image.
They install on demand onto the persistent `/data` volume from the **AI Provider**
page and are updated there without rebuilding the image. `WITH_NODE` must be on,
since those installs and version checks use Node/npm. On a fresh deployment, open
the AI Provider page and use each agent's **Update** button before running AI
nodes.

Toolchain versions are also configurable: `NODE_VERSION`, `DOTNET_VERSION`, `NODE_RUNTIME_VERSION`, and `DOTNET_SDK_CHANNEL`.
