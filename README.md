# ILD — In-Loop Development

ILD is a containerized development orchestration system built around shared work items, loop templates, per-item git worktrees, and adapter-driven AI execution. A local ILD instance owns loop execution, repository operations, previews, and realtime UI; a standalone WorkItem Server owns the work-item source of truth so multiple ILD instances can coordinate safely.

The current product supports both autonomous polling from the shared server and explicit manual starts from the taskboard. AI nodes are adapter-based: some providers are plain OpenAI-compatible HTTP calls, while others run external agent CLIs such as OpenCode or Pi inside the worktree.

## What ILD Does

- Shared work-item coordination through a standalone WorkItem Server with atomic `Running` claims, heartbeat updates, stale reclaim, dependencies, tags, and conversation history.
- A taskboard UI covering `Backlog`, `WorkQueue`, `Ready`, `Running`, `HumanFeedback`, `WaitingForIld`, and `Done`.
- Visual, versioned loop-template editing with `Start`, `Cmd`, `AI`, `Human`, `PR`, and `Cleanup` nodes.
- Loop execution with retries, `OnFailure` routing, pause/resume, crash recovery, and startup reconciliation.
- Manual and automatic execution paths: users can start Ready items from the UI, and the background poller can claim Ready items from the shared server.
- Adapter-driven AI execution with currently implemented provider types for `openai`, `opencode`, and `pi`.
- QA preview orchestration for active worktrees, including start, stop, status, and public URL generation.
- Runtime configuration of repositories, remote providers, AI providers, and backend log level from the UI.
- Realtime updates over SignalR for run events, work-item changes, and node progress streaming.

## Architecture

```text
Browser (React 19 + Vite+)
  -> ILD.Api (/api/v1, /hubs/loop-run, /hubs/work-item)
       -> ILD.Core services
            - LoopEngine
            - WorkItemManager (remote-backed)
            - RepositoryManager
            - RecoveryManager
            - RemoteWorkItemPoller / Coordinator / StartupReconciler
            - Adapter registry + AI node executors
       -> ILD.Data (EF Core stores)
       -> PostgreSQL in the supported compose stack
       -> worktrees and local runtime files under /worktrees and /data

ILD.Api <-> ILD.WorkItemServer (HTTP + API key)
ILD.WorkItemServer -> PostgreSQL in the supported compose stack
ILD.McpServer -> local ILD API for agent-facing work-item tools
```

Key boundaries:

- `ILD.WorkItemServer` is authoritative for work-item state, dependencies, tags, conversations, repository association, and claim semantics.
- `ILD.Api` is the main host for auth, controllers, SignalR hubs, startup seeding, and recovery.
- `ILD.Core` owns loop execution, repository operations, polling orchestration, preview control, metrics generation, and AI adapter selection.
- `ILD.Data` stores loop runs, templates, repositories, providers, users, event logs, adapter session snapshots, and other ILD-local state.
- `frontend/` is the SPA surfaced at runtime from `wwwroot/` and proxied through Vite+ during development.

## Quickstart

### Docker Compose

The supported deployment path is the checked-in compose stack:

```bash
git clone <this repo> ild && cd ild
cp .env.example .env
# set ILD_PASSWORD before continuing
docker compose up --build
```

The compose stack starts three services:

- `postgres` on port `5432`
- `workitem-server` on port `8081`
- `ild` on port `8080`

Open <http://localhost:8080> and log in with `admin` and the `ILD_PASSWORD` value you supplied.

Named volumes used by default:

| Volume          | Purpose                                                |
| --------------- | ------------------------------------------------------ |
| `postgres-data` | PostgreSQL data for both ILD and the WorkItem Server   |
| `ild-data`      | ILD runtime files under `/data`                        |
| `ild-worktrees` | Per-work-item git worktrees                            |
| `workitem-data` | Additional WorkItem Server runtime files under `/data` |

Your host `~/.gitconfig` is mounted read-only into the ILD container so commits inherit your local name and email unless you override `GIT_CONFIG`.

### Local Development

Local development assumes the same split architecture: ILD API, WorkItem Server, and a PostgreSQL database. The easiest way to satisfy infrastructure locally is still `docker compose up postgres workitem-server` and then run the app/frontend from the host.

```bash
# backend
export ILD_PASSWORD=letmein
export ILD_DB_CONNECTION_STRING='Host=localhost;Port=5432;Database=IldCore;Username=ild_core;Password=ild_core_password'
dotnet run --project ILD.Api

# frontend
cd frontend
vp install
vp dev
```

For the standalone WorkItem Server outside compose:

```bash
export WORKITEM_DB_CONNECTION_STRING='Host=localhost;Port=5432;Database=IldWorkitems;Username=ild_workitems;Password=ild_workitems_password'
export WORKITEM_API_KEYS=test-api-key-123
dotnet run --project ILD.WorkItemServer
```

The frontend dev server runs on `http://localhost:3000` and proxies `/api` and `/hubs` to `http://localhost:5000` by default.

### First Startup Behavior

On first successful ILD startup:

1. EF Core migrations are applied when a database connection string is configured.
2. The bootstrap admin user is created on first login from `ILD_PASSWORD`.
3. Seed loop templates are created: `Simple Code Change`, `AI-Assisted Feature`, and `Plan`.
4. A default remote provider is auto-seeded when `ILD_WORKITEM_SERVER_URL` and `ILD_WORKITEM_SERVER_API_KEY` are present and no providers exist yet.
5. Recoverable runs are inspected and recovery is attempted according to each run's policy.

## Configuration

Most important environment variables:

| Variable                        | Purpose                                                                                |
| ------------------------------- | -------------------------------------------------------------------------------------- |
| `ILD_PASSWORD`                  | Required bootstrap password for the `admin` user                                       |
| `ILD_USERNAME`                  | Optional bootstrap username override; current auth flow still seeds `admin` by default |
| `ILD_DB_CONNECTION_STRING`      | PostgreSQL connection string for ILD local state                                       |
| `WORKITEM_DB_CONNECTION_STRING` | PostgreSQL connection string for the WorkItem Server                                   |
| `ILD_DATA_PATH`                 | Base data directory for ILD runtime files                                              |
| `ILD_WORKTREES_PATH`            | Base directory for per-item worktrees                                                  |
| `ILD_LOG_LEVEL`                 | Initial Serilog level                                                                  |
| `ILD_WORKITEM_SERVER_URL`       | URL used for remote-provider auto-seeding                                              |
| `ILD_WORKITEM_SERVER_API_KEY`   | API key used for remote-provider auto-seeding                                          |
| `WORKITEM_API_KEYS`             | Accepted bearer keys for the WorkItem Server                                           |
| `ASPNETCORE_URLS`               | HTTP bind address for each .NET host                                                   |

Build-time container options from compose:

| Build arg         | Purpose                                                   |
| ----------------- | --------------------------------------------------------- |
| `WITH_OPENCODE`   | Install the OpenCode CLI in the ILD image                 |
| `WITH_PI`         | Install the Pi CLI in the ILD image                       |
| `WITH_NODE`       | Install Node.js tooling in the ILD image                  |
| `WITH_DOTNET_SDK` | Install the .NET SDK in the ILD image                     |
| `WITH_CHROME`     | Install Chrome in the ILD image                           |
| `WITH_CERTS`      | Import `.crt` or `.pem` files from `certs/` at build time |

Remote providers, repositories, AI providers, and runtime polling settings are managed from the UI and persisted in the ILD database.

## Domain Model

| Concept                 | Meaning                                                       |
| ----------------------- | ------------------------------------------------------------- |
| `WorkItem`              | Shared remote unit of work stored on the WorkItem Server      |
| `LoopTemplate`          | Named workflow definition with immutable saved versions       |
| `LoopRun`               | One local execution of a template version against a work item |
| `LoopRunNode`           | One visited node execution within a run                       |
| `RemoteProvider`        | Git provider + WorkItem Server settings + poll cadence        |
| `AiProvider`            | Adapter-resolved AI provider configuration                    |
| `RecoveryPolicy`        | `AutoResume`, `NeedsReview`, or `Cancel` after restart        |
| `ActiveWorkItemTracker` | Local set of items this ILD instance is actively heartbeating |

Important behavior:

- Work-item tags drive loop-template resolution.
- Ready items can be claimed automatically by the poller or started manually from the UI.
- Human feedback moves remote items through `HumanFeedback` and `WaitingForIld` before resuming execution.
- ILD-local run state stores engine-only data such as worktree path, branch, PR URL, and current run ID.
- The WorkItem Server also stores `RepositoryId`, `CreatedByLoopRunId`, and `HumanFeedbackActions` to round-trip execution context cleanly.

## Project Layout

```text
ild/
├── ILD.sln
├── README.md
├── PRD.md
├── docker-compose.yml
├── Dockerfile
├── Dockerfile.WorkItemServer
├── ild.config.json
├── ILD.Api/
├── ILD.Core/
├── ILD.Data/
├── ILD.McpServer/
├── ILD.WorkItemServer/
├── ILD.Tests/
└── frontend/
```

Notable source areas:

- `ILD.Api/Controllers` for the HTTP surface.
- `ILD.Core/Services/Implementations/Executors` for loop node execution.
- `ILD.Core/Services/Implementations/Adapters` for AI provider implementations.
- `ILD.Core/Services/Remote` for WorkItem Server polling and coordination.
- `frontend/src/pages` for taskboard, settings, providers, repositories, and loop-run UI.

## Development Workflow

```bash
vp install
dotnet build ILD.sln
dotnet test ILD.Tests/ILD.Tests.csproj

cd frontend
vp check
vp test --run
```

Vite+ notes:

- Use `vp install`, `vp dev`, `vp check`, and `vp test`.
- Do not use raw `pnpm` or `npm` installs in this repo.

### QA Preview

If a repository defines `preview.profiles` in `ild.config.json`, ILD can start and manage long-running QA services inside a worktree. The work item modal exposes preview controls, and the same API surface is available to AI tools.

`POST /api/v1/workitems/{id}/preview/start` accepts optional `profileName`, `skipInstall`, `publicHost`, `portOverrides`, and `timeoutSeconds` values.

## Testing

Primary validation commands:

- `dotnet test ILD.Tests/ILD.Tests.csproj`
- `cd frontend && vp test --run`
- `cd frontend && vp check`

The test suite covers loop execution, recovery, polling, repository management, auth, provider adapters, metrics, schema validation, and frontend page/component behavior.

## API Surface

All ILD API routes are under `/api/v1/...`.

Selected ILD endpoints:

| Method | Route                                  | Purpose                               |
| ------ | -------------------------------------- | ------------------------------------- |
| `POST` | `/api/v1/auth/login`                   | Create a session token                |
| `POST` | `/api/v1/auth/logout`                  | Revoke the current session            |
| `GET`  | `/api/v1/auth/me`                      | Return the authenticated user         |
| `GET`  | `/api/v1/health`                       | Database and disk health summary      |
| `PUT`  | `/api/v1/logging/level`                | Change backend log level at runtime   |
| `GET`  | `/metrics`                             | Prometheus-style metrics snapshot     |
| `GET`  | `/api/v1/workitems`                    | List work items                       |
| `POST` | `/api/v1/workitems/{id}/start`         | Start a Ready work item manually      |
| `POST` | `/api/v1/workitems/{id}/preview/start` | Start a QA preview                    |
| `GET`  | `/api/v1/loopruns`                     | List loop runs                        |
| `GET`  | `/api/v1/loopruns/{id}/events`         | Read run event logs                   |
| `GET`  | `/api/v1/remoteproviders`              | Manage git + WorkItem Server settings |
| `GET`  | `/api/v1/agent/workitems`              | Agent-facing work-item listing        |

SignalR hubs:

- `/hubs/loop-run`
- `/hubs/work-item`

Standalone WorkItem Server endpoints:

| Method | Route                        | Purpose                                           |
| ------ | ---------------------------- | ------------------------------------------------- |
| `GET`  | `/health`                    | Basic service liveness                            |
| `GET`  | `/workitems`                 | List work items                                   |
| `GET`  | `/workitems/poll`            | Heartbeat + ready-item polling                    |
| `POST` | `/workitems/{id}/transition` | Atomic claim or permissive state change           |
| `POST` | `/workitems/{id}/feedback`   | Append human feedback and move to `WaitingForIld` |

The WorkItem Server expects bearer API keys via `Authorization: Bearer <key>`.

## Deployment

The supported deployment is the checked-in compose stack with PostgreSQL plus the two .NET services. ILD and the WorkItem Server both run migrations against PostgreSQL when connection strings are configured.

The main `Dockerfile` builds the frontend, publishes the .NET host, and optionally installs additional runtime tooling used by work-item execution. `Dockerfile.WorkItemServer` builds the separate WorkItem Server image.

For host bind mounts instead of named volumes:

```yaml
volumes:
  - ./.local/ild-data:/data
  - ./.local/ild-worktrees:/worktrees
  - ./.local/workitem-data:/data
```

## Troubleshooting

**Login returns 401 even with the configured password**

`ILD_PASSWORD` is only used when the bootstrap user is first created. After that, auth uses the stored PBKDF2 hash and a persisted session token.

**The poller is not claiming work**

Confirm at least one remote provider has `WorkItemServerUrl` configured, plus a valid WorkItem API key and poll settings. The poller remains effectively disabled until that configuration exists.

**A work item is stuck in `Running` remotely**

The WorkItem Server reclaims stale running items when heartbeats stop arriving. Check ILD is still tracking the item and that the poller is reaching the remote server.

**Webhook updates are not reaching ILD**

The webhook route is not an anonymous bypass. Configure the expected bearer auth and HMAC settings together; a missing or mismatched secret causes rejection.

**Preview URLs are not reachable from the host**

Only ports published by compose are reachable from the host browser. An internal preview may still be valid for AI-driven checks even when it is not externally reachable.

## License

See the repository for license details.
