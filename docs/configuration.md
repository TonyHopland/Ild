# Configuration

Remote providers, the WorkItem Server connection, repositories, AI providers, and runtime polling settings are managed from the UI and persisted in the ILD database. The WorkItem Server connection (URL, API key, poll/grace cadence) is a single app-wide setting edited from its own tab, no longer per remote provider. The settings below are environment- and build-time configuration.

## Environment variables

| Variable                        | Purpose                                                                                  |
| ------------------------------- | ---------------------------------------------------------------------------------------- |
| `ILD_PASSWORD`                  | Required bootstrap password; sets the password for the bootstrap user on first login     |
| `ILD_USERNAME`                  | Bootstrap username (defaults to `admin`); used to seed and authenticate the first user   |
| `ILD_DB_CONNECTION_STRING`      | PostgreSQL connection string for ILD local state                                         |
| `WORKITEM_DB_CONNECTION_STRING` | PostgreSQL connection string for the WorkItem Server                                     |
| `ILD_DATA_PATH`                 | Base data directory for ILD runtime files                                                |
| `ILD_WORKTREES_PATH`            | Base directory for per-item worktrees (overrides the `DataRoot`/`worktrees` default)     |
| `ILD_LOG_LEVEL`                 | Initial Serilog level (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`)   |
| `ILD_SECRET_KEY`                | Optional encryption-at-rest key for provider API keys and webhook secrets (see below)    |
| `ILD_WORKITEM_SERVER_URL`       | URL used to auto-seed the global WorkItem Server connection                              |
| `ILD_WORKITEM_SERVER_API_KEY`   | API key used to auto-seed the global WorkItem Server connection                          |
| `ILD_ALLOWED_ORIGINS`           | Comma-separated CORS origins allowed to call the ILD API                                 |
| `WORKITEM_API_KEYS`             | Accepted bearer keys for the WorkItem Server (comma-separated)                           |
| `WORKITEM_DATA_PATH`            | Base data directory for WorkItem Server runtime files                                    |
| `GIT_CONFIG`                    | Path to the host `.gitconfig` mounted into the ILD container so commits inherit identity |
| `ASPNETCORE_URLS`               | HTTP bind address for each .NET host (standard ASP.NET Core variable)                    |

The log level is also changeable at runtime through `PUT /api/v1/logging/level` without restarting; `ILD_LOG_LEVEL` only sets the starting level.

The ILD container additionally uses an `ILD_AGENT_TOKEN` for agent/MCP calls back into the local API. It is auto-generated at startup if unset, so you normally don't need to provide one.

## Secret encryption at rest

Provider API keys and webhook secrets are persisted in the ILD database. When `ILD_SECRET_KEY` is set, those columns are encrypted with AES-256-GCM before they are written; the key is derived from the variable via SHA-256, so any non-empty string is accepted (use a high-entropy value such as `openssl rand -hex 32`). The startup log reports whether encryption is enabled.

Behaviour is backwards-compatible so you can adopt it on an existing database without a data migration:

- When `ILD_SECRET_KEY` is unset, secrets are stored as plaintext (a warning is logged). Restrict access to the database volume accordingly.
- Existing plaintext rows remain readable after a key is added, and are re-written in encrypted form the next time that provider's secret is changed (a save that does not modify the secret leaves the stored value untouched). To encrypt existing secrets immediately, re-enter them on the affected providers.
- **Losing the key makes already-encrypted secrets unrecoverable.** Back it up, and treat rotating it as re-entering the affected provider secrets.

Other credentials follow their own paths: the bootstrap password is hashed (PBKDF2), and the WorkItem Server shared key is supplied at runtime via `WORKITEM_API_KEYS` / `ILD_WORKITEM_SERVER_API_KEY` rather than relying on database storage.

## Build-time container options

Set as build args (e.g. in `.env` consumed by `docker compose build`):

| Build arg          | Purpose                                                    |
| ------------------ | ---------------------------------------------------------- |
| `WITH_OPENCODE`    | Install the OpenCode CLI in the ILD image                  |
| `WITH_PI`          | Install the Pi CLI in the ILD image (requires `WITH_NODE`) |
| `WITH_CLAUDE_CODE` | Install the Claude Code CLI in the ILD image               |
| `WITH_NODE`        | Install Node.js tooling in the ILD image                   |
| `WITH_DOTNET_SDK`  | Install the .NET SDK in the ILD image                      |
| `WITH_CHROME`      | Install Chrome in the ILD image                            |
| `WITH_CERTS`       | Import `.crt` or `.pem` files from `certs/` at build time  |

Toolchain versions are also configurable: `NODE_VERSION`, `DOTNET_VERSION`, `NODE_RUNTIME_VERSION`, and `DOTNET_SDK_CHANNEL`.
