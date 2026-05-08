# ILD — In-Loop Development

> A containerized AI-assisted development platform that closes the loop between work items, code, tests, reviews, and merged pull requests.

ILD turns the repetitive cycle of _plan → code → test → review → merge_ into a first-class, configurable workflow. You design loops as directed graphs of `Cmd`, `AI`, `Human`, `PR`, `Start`, and `Cleanup` nodes; ILD runs them autonomously inside per-work-item git worktrees, pauses for human review when needed, integrates with Forgejo / Gitea / GitHub / GitLab for pull requests, and persists execution state to a local SQLite database.

Work items are managed by a standalone **WorkItem Server** that multiple ILD instances can share. Each ILD polls the server for ready work, claims it atomically, executes it locally, and returns feedback — enabling multi-developer coordination with a single source of truth.

---

## Table of Contents

- [What ILD Does](#what-ild-does)
- [Architecture](#architecture)
- [Quickstart](#quickstart)
- [Configuration](#configuration)
- [Domain Concepts](#domain-concepts)
- [Project Layout](#project-layout)
- [Development Workflow](#development-workflow)
- [Testing](#testing)
- [API Surface](#api-surface)
- [Deployment](#deployment)
- [Troubleshooting](#troubleshooting)

---

## What ILD Does

- **WorkItem Server** — Standalone REST service that is the authoritative source for work items. Multiple ILD instances connect to the same server to coordinate work, claim items atomically, and resolve human feedback.
- **Taskboard** — Backlog → Work Queue → Ready → Running → Human Feedback → Waiting For Ild → Done. Work items carry tags, dependencies, priority, and a full conversation history.
- **Autonomous polling** — Each ILD instance polls the remote server on a configurable schedule. Ready items are claimed via `Transition(Running)` — first ILD to claim wins. No manual "start" button needed.
- **Loop templates** — Versioned directed graphs you author visually. Every save creates an immutable `LoopTemplateVersion`; in-flight runs keep their pinned version.
- **Loop engine** — Drives runs to completion with retries, `OnFailure` routing, `MaxTraversals` per edge, pause/resume, and crash recovery on startup.
- **Node executors**:
  - `Start` — creates / attaches a per-work-item git worktree and branch.
  - `Cmd` — runs a shell command inside the worktree with a timeout.
  - `AI` — renders a prompt template (with placeholders for work item, worktree files, previous output) and calls an OpenAI-compatible chat completions endpoint.
  - `Human` — pauses the run and parks the work item in `HumanFeedback`.
  - `PR` — creates a PR (or reuses existing), waits for webhook events, routes merge to success or rejection to failure.
  - `Cleanup` — destroys the worktree.
- **Tag-to-loop matching** — A work item's tags determine which loop template executes it. Tag name must match a loop template name on the ILD instance. No match or ambiguous match escalates to `HumanFeedback`.
- **Grace period polling** — When a work item is in `HumanFeedback`, ILD polls at 5-second intervals until the user responds (transitions to `WaitingForIld`) or the configurable grace period expires.
- **Startup reconciliation** — On restart, ILD queries the remote server for each locally-tracked work item's status, resumes running items, and cleans up completed ones.
- **Repository integration** — git worktrees, branch creation, optional Forgejo / Gitea pull requests, webhook-driven merge sync.
- **Real-time UI** — SignalR pushes node state changes, event log entries, and run state transitions to the React taskboard.
- **Single-binary deploy** — One container, SQLite for local execution state, worktrees at `/worktrees`, basic auth via env vars.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Browser (React)                           │
│  Taskboard · WorkItemModal · LoopEditor · RemoteProviders        │
│  ConversationView · EventLog (SignalR)                           │
└──────────────────────────────┬──────────────────────────────────┘
                               │ /api/v1 + /hubs/* (SignalR)
┌──────────────────────────────┴──────────────────────────────────┐
│                         ILD.Api (ASP.NET Core 10)               │
│  Controllers · AuthMiddleware · LoopRunHub · WorkItemHub        │
└──────────────────────────────┬──────────────────────────────────┘
                               │ DI
┌──────────────────────────────┴──────────────────────────────────┐
│                         ILD.Core (services)                      │
│  LoopEngine · NodeExecutors · WorkItemManager (remote-backed)    │
│  RemoteWorkItemPoller · RemoteWorkItemCoordinator                │
│  RemoteWorkItemStartupReconciler · ActiveWorkItemTracker         │
│  RepositoryManager · AIProviderService · RemoteProvider          │
│  PrSyncService · RecoveryManager · EventLogService               │
└───┬────────────────────────────────────┬────────────────────────┘
    │ IWorkItemServerClient (HTTP)       │ Store interfaces
┌───┴──────────────────────────┐  ┌─────┴────────────────────────┐
│   ILD.WorkItemServer         │  │     ILD.Data (EF Core)        │
│   (standalone REST service)  │  │  AppDbContext · Entities      │
│                              │  │  DTOs · Enums · Stores        │
│   Work items (authoritative) │  └──────────┬───────────────────┘
│   Tags · Dependencies        │             │ EF Core
│   Conversation · Status      │  ┌──────────┴───────────────────┐
│   Atomic claim on Running    │  │  SQLite (ild.db) + /worktrees │
│   Heartbeat / stale reclaim  │  │  (local sidecar + worktrees)  │
└──────────────────────────────┘  └───────────────────────────────┘
```

- **`ILD.WorkItemServer`** is the standalone WorkItem server. It owns the authoritative work item domain: Title, Description, Status, Priority, Tags, Dependencies, Conversation, CreatedBy. It exposes a REST API and enforces atomic claim on `Transition(Running)`.
- **`ILD.Core`** owns all business services. The `WorkItemManager` is now remote-backed — every write hits the server first, then mirrors to a local sidecar row. The loop engine, poller, and reconciler are singletons.
- **`ILD.Data`** owns the EF Core data layer. ILD keeps a local sidecar row that mirrors the server view and stores engine-only fields (worktree, branch, PR, current loop run).
- **`ILD.Api`** is the ASP.NET Core host: controllers, SignalR hubs, auth middleware, DI composition, and startup template seeding + run recovery.
- **`ILD.McpServer`** is the MCP server for AI agents. It proxies work item operations through the local ILD API.
- **`ILD.Tests`** is the xUnit suite covering EventLogService, LoopTemplateValidator, LoopTemplateManager, WorkItemManager, LoopEngine, AuthService, RepositoryManager, and AIProviderService.
- **`frontend/`** is a React 18 + Vite+ SPA proxied to the .NET API at dev time and served from `wwwroot/` in production.

`LoopEngine` and the node executors are registered as singletons. The engine injects `IServiceProvider` and creates scoped DB access per operation, allowing long-running runs to share a single engine instance while still using transient DB contexts. SignalR notifications flow through `IRunNotifier` → `SignalRRunNotifier` → `LoopRunHub` group `runId.ToString()`.

---

## Quickstart

### Option A — Docker (recommended)

Requires Docker + Docker Compose.

```bash
git clone <this repo> ild && cd ild
ILD_PASSWORD=letmein docker compose up --build
```

Then open <http://localhost:8080> and log in with `admin` / `letmein`.

The container persists state in two named volumes:

| Volume          | Mounted at   | Purpose                               |
| --------------- | ------------ | ------------------------------------- |
| `ild-data`      | `/data`      | SQLite DB at `/data/ild.db` (sidecar) |
| `ild-worktrees` | `/worktrees` | Per-work-item git worktrees           |

Your host `~/.gitconfig` is mounted read-only so commits inherit your name/email. Override with `GIT_CONFIG=/path/to/config docker compose up`.

### Option B — Local development (no container)

Requires .NET 10 SDK, Node 22+, and [Vite+](https://vite.plus) (`vp`). Vite+ wraps the package manager — **don't run `pnpm`/`npm` directly** for installs.

```bash
# Backend
dotnet build ILD.sln
ILD_PASSWORD=letmein dotnet run --project ILD.Api

# Frontend (separate terminal — proxies /api → :5000)
cd frontend
vp install
vp dev
```

Backend listens on `http://localhost:5000` (configurable via `ASPNETCORE_URLS` or `ILD.Api/Properties/launchSettings.json`); frontend dev server on `http://localhost:3000` and proxies `/api` and `/hubs` to the backend.

### First login & seed data

On first startup the API:

1. Creates `data/ild.db` with the EF schema.
2. Bootstraps user `admin` whose password is the value of `ILD_PASSWORD` (PBKDF2-SHA256, 100k iterations).
3. Seeds three loop templates: **Simple Code Change**, **AI-Assisted Feature**, **Plan**.
4. Best-effort recovers any `LoopRun` left in `Running` from a previous process.
5. Runs startup reconciliation against the remote WorkItem server (if configured).

To smoke-test the API directly:

```bash
TOKEN=$(curl -s -X POST http://localhost:5000/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"letmein"}' \
  | jq -r .token)

curl -s http://localhost:5000/api/v1/looptemplates \
  -H "Authorization: Bearer $TOKEN" | jq '.[].name'
```

---

## Configuration

All configuration is via environment variables.

| Variable             | Default                    | Description                                                                                     |
| -------------------- | -------------------------- | ----------------------------------------------------------------------------------------------- |
| `ILD_USERNAME`       | `admin`                    | Bootstrap username.                                                                             |
| `ILD_PASSWORD`       | _(required)_               | Plaintext password used the **first time** the user is created. After that, it's stored hashed. |
| `ILD_DATA_PATH`      | `data` (`/data` in Docker) | Where `ild.db` lives.                                                                           |
| `ILD_WORKTREES_PATH` | `worktrees` (`/worktrees`) | Default base path for per-work-item worktrees.                                                  |
| `ILD_LOG_LEVEL`      | `Information`              | Serilog minimum level.                                                                          |
| `ASPNETCORE_URLS`    | `http://+:8080`            | Listening URLs.                                                                                 |

AI providers, remote git providers, and repositories are configured at runtime through the API / UI rather than environment variables.

The WorkItem server connection, polling schedule, grace period, and concurrency cap are configured through the **Remote Providers** settings page.

> **Secrets policy.** No real credentials live in `appsettings.*.json` or any committed file. Auth is bootstrapped via `ILD_USERNAME` / `ILD_PASSWORD` at first run and then stored hashed. AI provider API keys live in `AiProvider.Config` (a JSON column on the `AiProvider` row) and are entered through the Settings UI; they must never be committed to source control. WorkItem server API keys are stored per-remote-provider and are never logged.

---

## Domain Concepts

| Concept                   | Summary                                                                                                        |
| ------------------------- | -------------------------------------------------------------------------------------------------------------- |
| **WorkItem**              | A unit of work on the remote server. Owns status, priority, tags, dependencies, and conversation history.      |
| **WorkItemStatus**        | `Backlog` → `WorkQueue` → `Ready` → `Running` → `HumanFeedback` → `WaitingForIld` → `Done`.                    |
| **Conversation**          | Array of `{ role, content, timestamp }` entries appended on transitions to response states.                    |
| **Tags**                  | String labels on work items. Tag name must match a loop template name for execution.                           |
| **LoopTemplate**          | Named workflow. Has many immutable `LoopTemplateVersion`s. Versioned on every save.                            |
| **LoopNode / Edge**       | Graph payload owned by a `LoopTemplateVersion`. Edges carry `EdgeType` (`OnSuccess` / `OnFailure`).            |
| **LoopRun**               | An execution of a template version against a work item. Tracks status, current node, traversals, recovery.     |
| **LoopRunNode**           | One node execution in a run. Retries update the same record. Stores output and timing.                         |
| **EventLog**              | Append-only audit stream per run (node started/completed, human feedback, etc).                                |
| **RemoteProvider**        | Git provider connection + optional WorkItem server URL, API key, poll schedule, grace period, max concurrency. |
| **AiProvider**            | OpenAI-compatible chat completions endpoint + API key.                                                         |
| **RecoveryPolicy**        | What to do with `Running` runs after a restart: `AutoResume`, `NeedsReview`, or `Cancel`.                      |
| **ActiveWorkItemTracker** | Local in-memory tracker of work items this ILD instance currently considers active.                            |

### Polling & claim flow

1. Poller sends active work item IDs as heartbeat to the remote server.
2. Server returns status updates for active items + list of Ready items.
3. Items in `WaitingForIld` are transitioned to `Running` (resume).
4. Ready items are claimed via `Transition(Running)` — first ILD wins.
5. Tag-to-template resolution happens before claim. No match or ambiguous match escalates to `HumanFeedback`.
6. Claimed items are tracked locally and a `LoopRun` is created.

### Grace period

When at least one active work item is in `HumanFeedback`, the poller switches to a faster cadence (default 5s) so human responses propagate quickly. After the configurable grace period expires, the poller reverts to the normal schedule. If below max concurrency during grace, new Ready items are still claimed.

### Startup reconciliation

On startup, ILD queries the remote server for each locally-tracked work item's current status:

- **Running** → resume execution via `LoopEngine`
- **HumanFeedback / WaitingForIld** → add to active tracker (no resume, human needs to respond)
- **Done / not found** → clean up locally

### Loop validation rules

A `LoopTemplateGraph` is rejected on save unless:

- Exactly one `Start` node exists.
- At least one `Cleanup` node exists.
- All nodes are reachable from `Start`.
- At least one path leads to a `Cleanup` node.
- Every edge references existing nodes.
- Prompt templates only reference known placeholders (`{{WorkItem.Title}}`, `{{WorkItem.Description}}`, `{{PreviousNodeOutput}}`, `{{WorkTree.File:relative/path}}`, …).

---

## Project Layout

```
ild/
├── ILD.sln                       # .NET solution
├── docker-compose.yml            # Single-container deployment
├── Dockerfile                    # Multi-stage: frontend → backend → runtime
├── PRD.md                        # Product requirements
├── WorkitemServer-PRD.md         # WorkItem server architecture spec
├── package.json                  # Vite+ workspace root
├── pnpm-workspace.yaml
├── vite.config.ts                # Vite+ root config (lint/fmt/check)
├── ILD.Data/                     # EF Core data layer
│   ├── AppDbContext.cs           # DbContext + model configuration
│   ├── Entities/                 # EF entity models (WorkItem, LoopRun, etc.)
│   ├── DTOs/                     # Wire types and request/response DTOs
│   ├── Enums/                    # Status / type enums
│   ├── Stores/                   # IWorkItemStore, ILoopRunStore, etc.
│   └── ServiceCollectionExtensions.cs
├── ILD.Core/                     # Business services (no EF Core dep)
│   └── Services/
│       ├── Interfaces/
│       ├── Implementations/
│       │   ├── Executors/        # Start/Cmd/AI/Human/PR/Cleanup node executors
│       │   ├── LoopEngine.cs
│       │   ├── WorkItemManager.cs  (remote-backed)
│       │   ├── LoopTemplateManager.cs
│       │   ├── RecoveryManager.cs
│       │   └── ...
│       └── Remote/               # Remote WorkItem server client & poller
│           ├── WorkItemServerClient.cs
│           ├── RemoteWorkItemPoller.cs
│           ├── RemoteWorkItemCoordinator.cs
│           ├── RemoteWorkItemStartupReconciler.cs
│           ├── IActiveWorkItemTracker.cs
│           └── ILoopTemplateResolver.cs
├── ILD.Api/                      # ASP.NET Core host
│   ├── Program.cs
│   ├── Configuration/
│   │   ├── ServiceCollectionExtensions.cs
│   │   ├── SignalRRunNotifier.cs
│   │   └── TemplateSeeder.cs
│   ├── Controllers/              # Auth, WorkItems, LoopTemplates,
│   │   │                         # LoopRuns, Repositories, RemoteProviders,
│   │   │                         # AiProviders, Webhooks, Health, Agent
│   ├── Hubs/                     # LoopRunHub, WorkItemHub
│   └── Middleware/               # AuthMiddleware
├── ILD.McpServer/                # MCP server for AI agents
│   ├── IldClient.cs              # HTTP client to local ILD API
│   └── Tools/                    # create_workitem, list_workitems, etc.
├── ILD.WorkItemServer/           # Standalone WorkItem server
│   ├── Domain/                   # WorkItem, WorkItemStatus, ConversationMessage
│   ├── Controllers/              # REST API endpoints
│   ├── Services/                 # WorkItemService, WorkItemMapper
│   ├── Auth/                     # API key middleware
│   ├── Hosting/                  # StaleWorkItemReclaimer
│   └── Data/                     # EF Core context
├── ILD.Tests/                    # xUnit + FluentAssertions + Moq
└── frontend/
    ├── package.json
    ├── vite.config.ts            # Vite+ + jsdom for Vitest
    └── src/
        ├── components/           # Taskboard, WorkItemCard, WorkItemModal, …
        ├── hooks/
        ├── pages/                # Taskboard, LoopEditor, RemoteProviders, Settings
        ├── services/             # api.ts, auth.ts, signalr client
        ├── types/
        └── utils/
```

---

## Development Workflow

```bash
# Backend
dotnet build ILD.sln                                  # build
dotnet test  ILD.Tests/ILD.Tests.csproj --no-build    # run xUnit suite
dotnet run   --project ILD.Api                        # start API

# Frontend (run inside frontend/)
vp install      # restore deps via the wrapped package manager
vp dev          # dev server with /api proxy
vp test --run   # vitest in CI mode
vp check        # format + lint + typecheck
vp build        # production build → frontend/dist
```

Vite+ rules of thumb (see [AGENTS.md](AGENTS.md)):

- Use `vp add` / `vp remove`, **never** `pnpm add` directly.
- `vp test` runs the bundled Vitest. Don't install `vitest` as a dep.
- `vp dev` / `vp build` run Vite, not your npm scripts.

---

## Testing

| Suite           | Command                                             | Count   |
| --------------- | --------------------------------------------------- | ------- |
| Backend xUnit   | `dotnet test ILD.Tests/ILD.Tests.csproj --no-build` | **318** |
| Frontend Vitest | `cd frontend && vp test --run`                      | **127** |

Backend tests use:

- `Microsoft.EntityFrameworkCore.Sqlite` with a real in-memory SQLite database (`TestDb`), exercising the same store layer as production.
- `FluentAssertions` for readable assertions.
- `Moq` for collaborator stubs.
- `Microsoft.AspNetCore.Mvc.Testing` for any HTTP-level integration test.

The `LoopEngine` tests in particular cover the trickiest behaviors: happy path, retries within `MaxRetries`, `OnFailure` routing, `MaxTraversals` cycle protection, cancellation, and human-pause handling.

---

## API Surface

All routes are prefixed with `/api/v1`. Auth: `Authorization: Bearer <token>` (token from `POST /auth/login`) or `?token=` query for SignalR.

| Method | Route                                               | Purpose                                |
| ------ | --------------------------------------------------- | -------------------------------------- |
| POST   | `/auth/login`                                       | Exchange username+password for a token |
| POST   | `/auth/logout`                                      | Revoke the current session             |
| GET    | `/health`                                           | Liveness + DB + disk checks            |
| GET    | `/workitems`                                        | List work items                        |
| POST   | `/workitems`                                        | Create work item                       |
| GET    | `/workitems/{id}`                                   | Get work item with runs                |
| PUT    | `/workitems/{id}`                                   | Update                                 |
| POST   | `/workitems/{id}/start`                             | Trigger a loop run                     |
| POST   | `/workitems/{id}/transition`                        | Manual status transition               |
| GET    | `/looptemplates`                                    | List templates                         |
| POST   | `/looptemplates`                                    | Create (validates graph)               |
| PUT    | `/looptemplates/{id}`                               | Save (creates a new version)           |
| POST   | `/looptemplates/validate`                           | Pre-flight validation                  |
| POST   | `/looptemplates/{id}/clone?newName=…`               | Clone                                  |
| GET    | `/looptemplates/{id}/versions`                      | Version history                        |
| GET    | `/looprun/{id}`                                     | Run detail                             |
| POST   | `/looprun/{id}/cancel`                              | Cancel a run                           |
| GET    | `/repositories`, `/aiproviders`, `/remoteproviders` | CRUD over each resource                |
| POST   | `/webhooks/forgejo`                                 | Forgejo / Gitea PR webhook ingress     |
| GET    | `/agent/workitems`                                  | MCP agent: list work items             |
| POST   | `/agent/workitems`                                  | MCP agent: create work item            |

SignalR hubs:

- `/hubs/looprun` — group `runId` receives `NodeStateChanged`, `EventLogged`, `LoopRunStateChanged`, `Paused`, `Resumed`.
- `/hubs/workitem` — taskboard updates.

### WorkItem Server REST API

The standalone WorkItem server exposes its own REST surface (separate from the ILD API):

| Method | Route                                  | Purpose                           |
| ------ | -------------------------------------- | --------------------------------- |
| POST   | `/workitems`                           | Create work item                  |
| GET    | `/workitems`                           | List (filter by status, tags)     |
| GET    | `/workitems/{id}`                      | Get single                        |
| PUT    | `/workitems/{id}`                      | Update title/description/tags     |
| POST   | `/workitems/{id}/transition`           | Transition status (claim, resume) |
| POST   | `/workitems/{id}/dependencies`         | Add dependency                    |
| DELETE | `/workitems/{id}/dependencies/{depId}` | Remove dependency                 |
| POST   | `/workitems/{id}/feedback`             | Submit human feedback             |
| DELETE | `/workitems/{id}`                      | Delete                            |
| GET    | `/workitems/poll?activeIds=…`          | Poll + heartbeat                  |

Auth: `Authorization: Bearer <api-key>` or `X-Api-Key` header.

---

## Deployment

`docker compose up --build` is the supported path for the ILD instance. The `ILD.WorkItemServer` deploys as a separate container with its own SQLite database.

The `Dockerfile` is multi-stage:

1. **frontend-build** — `node:22-alpine`, `npm ci && npm run build` → static files.
2. **build** — `mcr.microsoft.com/dotnet/sdk:10.0`, `dotnet publish -c Release`.
3. **final** — `mcr.microsoft.com/dotnet/aspnet:10.0` + `git`. Frontend assets land in `wwwroot/`.

Bind-mount alternatives (instead of named volumes):

```yaml
volumes:
  - ./.local/data:/data
  - ./.local/worktrees:/worktrees
```

For multi-tenant or HTTPS deployments, terminate TLS at a reverse proxy (Caddy, Traefik, nginx). ILD itself only speaks HTTP and assumes a single trusted user. The WorkItem server should be deployed behind the same reverse proxy with its own API key authentication.

---

## Troubleshooting

**`Cannot resolve scoped service '…' from root provider`**
A singleton (engine / executor) tried to inject a scoped service directly. Inject `IServiceProvider` and create a scope with `sp.CreateScope()`. The `LoopEngine` follows this pattern, resolving store interfaces from scoped services.

**`A possible object cycle was detected`**
EF navigation properties make cycles. JSON output uses `ReferenceHandler.IgnoreCycles` — keep it that way when adding new endpoints.

**Login returns 401 but I'm sure the password is right**
The bootstrap password is _only_ used the first time the user is created. To reset, stop the container, delete `data/ild.db` (or the matching row), restart with the new `ILD_PASSWORD`.

**Loop run stuck in `Running` after a crash**
On startup, `RecoveryManager` reads each run's `RecoveryPolicy`:

- `AutoResume` — resumes via `LoopEngine.RunAsync`.
- `NeedsReview` — moves the work item to `HumanFeedback`.
- `Cancel` — cancels the run.

The `RemoteWorkItemStartupReconciler` also queries the remote server for each tracked work item's status and resumes, tracks, or cleans up accordingly.

Set the policy you want on the **template**, not the run.

**Webhook doesn't update PR status**
Ensure the Forgejo / Gitea webhook posts to `POST /api/v1/webhooks/forgejo` with auth disabled for that path (it's in the `AuthOptions.ExcludedPaths` list). The work item is matched on `WorkItem.PrUrl`.

**Work item stuck in `Running` on the remote server**
The server's `StaleWorkItemReclaimer` detects items whose heartbeat hasn't been refreshed within the timeout (default 15 minutes) and auto-transitions them back to `Ready`. Check the poller is running and the remote provider is configured correctly.

**Poller not picking up work**
Verify the Remote Provider settings page has a valid WorkItem server URL and API key. Check the log for `Remote poll:` messages. The poller stays disabled until a RemoteProvider with a `WorkItemServerUrl` exists.

---

## License

See repository for license details.
