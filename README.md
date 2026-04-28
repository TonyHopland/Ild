# ILD — In-Loop Development

> A containerized AI-assisted development platform that closes the loop between work items, code, tests, reviews, and merged pull requests.

ILD turns the repetitive cycle of _plan → code → test → review → merge_ into a first-class, configurable workflow. You design loops as directed graphs of `Cmd`, `AI`, `Human`, `PR`, `Start`, and `Cleanup` nodes; ILD runs them autonomously inside per-work-item git worktrees, pauses for human review when needed, integrates with Forgejo / Gitea / GitHub / GitLab for pull requests, and persists everything to a single SQLite database.

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

- **Taskboard** — Backlog → Work Queue → Ready → Running → Human Feedback → Done. Work items track dependencies, priority, and the loop run history.
- **Loop templates** — Versioned directed graphs you author visually. Every save creates an immutable `LoopTemplateVersion`; in-flight runs keep their pinned version.
- **Loop engine** — Drives runs to completion with retries, `OnFailure` routing, `MaxTraversals` per edge, pause/resume, and crash recovery on startup.
- **Node executors**:
  - `Start` — creates / attaches a per-work-item git worktree and branch.
  - `Cmd` — runs a shell command inside the worktree with a timeout.
  - `AI` — renders a prompt template (with placeholders for work item, worktree files, previous output) and calls an OpenAI-compatible chat completions endpoint.
  - `Human` — pauses the run and parks the work item in `HumanFeedback`.
  - `PR` — creates a PR (or reuses existing), waits for webhook events, routes merge to success or rejection to failure.
  - `Cleanup` — destroys the worktree.
- **Repository integration** — git worktrees, branch creation, optional Forgejo / Gitea pull requests, webhook-driven merge sync.
- **Real-time UI** — SignalR pushes node state changes, event log entries, and run state transitions to the React taskboard.
- **Single-binary deploy** — One container, SQLite at `/data/ild.db`, worktrees at `/worktrees`, basic auth via env vars.

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│                        Browser (React)                         │
│  Taskboard · WorkItemModal · LoopEditor · EventLog (SignalR)   │
└──────────────────────────────┬─────────────────────────────────┘
                               │ /api/v1 + /hubs/* (SignalR)
┌──────────────────────────────┴─────────────────────────────────┐
│                          ILD.Api (ASP.NET Core 10)             │
│  Controllers · AuthMiddleware · LoopRunHub · WorkItemHub       │
└──────────────────────────────┬─────────────────────────────────┘
                               │ DI
┌──────────────────────────────┴─────────────────────────────────┐
│                          ILD.Core (services)                   │
│  LoopEngine · NodeExecutors · WorkItemManager · AuthService    │
│  RepositoryManager · AIProviderService · RemoteProvider        │
│  PrSyncService · RecoveryManager · EventLogService             │
└──────────────────────────────┬─────────────────────────────────┘
                               │ EF Core
┌──────────────────────────────┴─────────────────────────────────┐
│                  SQLite ( /data/ild.db )  +  /worktrees         │
└────────────────────────────────────────────────────────────────┘
```

- **`ILD.Core`** owns the domain model (15 entities), DTOs, enums, and all business services.
- **`ILD.Api`** is the ASP.NET Core host: 8 controllers, 2 SignalR hubs, auth middleware, DI composition, and startup template seeding + run recovery.
- **`ILD.Tests`** is the xUnit suite — 40 tests covering `EventLogService`, `LoopTemplateValidator`, `LoopTemplateManager`, `WorkItemManager`, `LoopEngine`, `AuthService`, `RepositoryManager`, and `AIProviderService`.
- **`frontend/`** is a React 18 + Vite+ SPA proxied to the .NET API at dev time and served from `wwwroot/` in production.

`LoopEngine` and the node executors are registered as singletons. They use a `Func<AppDbContext>` factory to create a per-operation EF scope, which allows long-running runs to share a single engine instance while still using transient DB contexts. SignalR notifications flow through `IRunNotifier` → `SignalRRunNotifier` → `LoopRunHub` group `runId.ToString()`.

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

| Volume          | Mounted at   | Purpose                     |
| --------------- | ------------ | --------------------------- |
| `ild-data`      | `/data`      | SQLite DB at `/data/ild.db` |
| `ild-worktrees` | `/worktrees` | Per-work-item git worktrees |

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

---

## Domain Concepts

| Concept             | Summary                                                                                                           |
| ------------------- | ----------------------------------------------------------------------------------------------------------------- |
| **WorkItem**        | A unit of work. Owns status, priority, repository, dependencies, current run, branch and worktree path.           |
| **WorkItemStatus**  | `Backlog` → `WorkQueue` → `Ready` → `Running` → (`HumanFeedback`) → `Done`.                                       |
| **LoopTemplate**    | Named workflow. Has many immutable `LoopTemplateVersion`s. Versioned on every save.                               |
| **LoopNode / Edge** | Graph payload owned by a `LoopTemplateVersion`. Edges carry `EdgeType` (`OnSuccess` / `OnFailure`).               |
| **LoopRun**         | An execution of a template version against a work item. Tracks status, current node, traversals, recovery policy. |
| **LoopRunNode**     | One node execution in a run. Retries update the same record (incrementing retry count). Stores output and timing. |
| **EventLog**        | Append-only audit stream per run (node started/completed, human feedback, etc).                                   |
| **RemoteProvider**  | Forgejo / Gitea / GitHub-style API connection used to open / merge PRs.                                           |
| **AiProvider**      | OpenAI-compatible chat completions endpoint + API key.                                                            |
| **RecoveryPolicy**  | What to do with `Running` runs after a restart: `AutoResume`, `NeedsReview`, or `Cancel`.                         |

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
├── ILD.sln                       # .NET solution (Core + Api + Tests)
├── ild.slnx                      # New-style solution file
├── docker-compose.yml            # Single-container deployment
├── Dockerfile                    # Multi-stage: frontend → backend → runtime
├── PRD.md                        # Product requirements (95 user stories)
├── package.json                  # Vite+ workspace root
├── pnpm-workspace.yaml
├── vite.config.ts                # Vite+ root config (lint/fmt/check)
├── ILD.Core/                     # Domain + services (no ASP.NET deps)
│   ├── DTOs/                     # Wire types
│   ├── Enums/                    # Status / type enums
│   ├── Models/                   # EF entities + AppDbContext
│   └── Services/
│       ├── Interfaces/
│       └── Implementations/
│           ├── Executors/        # Start/Cmd/AI/Human/PR/Cleanup node executors
│           ├── LoopEngine.cs
│           ├── WorkItemManager.cs
│           ├── LoopTemplateManager.cs
│           ├── LoopTemplateValidator.cs
│           ├── AuthService.cs
│           ├── RepositoryManager.cs
│           ├── AIProviderService.cs
│           ├── RemoteProvider.cs
│           ├── PrSyncService.cs
│           ├── RecoveryManager.cs
│           └── EventLogService.cs
├── ILD.Api/                      # ASP.NET Core host
│   ├── Program.cs
│   ├── Configuration/
│   │   ├── ServiceCollectionExtensions.cs
│   │   ├── SignalRRunNotifier.cs
│   │   └── TemplateSeeder.cs
│   ├── Controllers/              # Auth, WorkItems, LoopTemplates,
│   │   │                         # LoopRuns, Repositories, RemoteProviders,
│   │   │                         # AiProviders, Webhooks, Health
│   ├── Hubs/                     # LoopRunHub, WorkItemHub
│   └── Middleware/               # AuthMiddleware
├── ILD.Tests/                    # xUnit + FluentAssertions + Moq + EF Sqlite InMemory
└── frontend/
    ├── package.json
    ├── vite.config.ts            # Vite+ + jsdom for Vitest
    └── src/
        ├── components/           # Taskboard, TaskboardColumn, WorkItemCard, WorkItemModal, …
        ├── hooks/
        ├── pages/
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

| Suite           | Command                                             | Count  |
| --------------- | --------------------------------------------------- | ------ |
| Backend xUnit   | `dotnet test ILD.Tests/ILD.Tests.csproj --no-build` | **40** |
| Frontend Vitest | `cd frontend && vp test --run`                      | **2**  |

Backend tests use:

- `Microsoft.EntityFrameworkCore.Sqlite` with an in-memory shared-cache connection (`TestDb`).
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

SignalR hubs:

- `/hubs/looprun` — group `runId` receives `NodeStateChanged`, `EventLogged`, `LoopRunStateChanged`, `Paused`, `Resumed`.
- `/hubs/workitem` — taskboard updates.

---

## Deployment

`docker compose up --build` is the supported path. The `Dockerfile` is multi-stage:

1. **frontend-build** — `node:22-alpine`, `npm ci && npm run build` → static files.
2. **build** — `mcr.microsoft.com/dotnet/sdk:10.0`, `dotnet publish -c Release`.
3. **final** — `mcr.microsoft.com/dotnet/aspnet:10.0` + `git`. Frontend assets land in `wwwroot/`.

Bind-mount alternatives (instead of named volumes):

```yaml
volumes:
  - ./.local/data:/data
  - ./.local/worktrees:/worktrees
```

For multi-tenant or HTTPS deployments, terminate TLS at a reverse proxy (Caddy, Traefik, nginx). ILD itself only speaks HTTP and assumes a single trusted user.

---

## Troubleshooting

**`Cannot resolve scoped service '…' from root provider`**
A singleton (engine / executor) tried to inject a scoped service directly. Inject `IServiceProvider` and create a scope, or inject `Func<AppDbContext>` (already done for the engine).

**`A possible object cycle was detected`**
EF navigation properties make cycles. JSON output uses `ReferenceHandler.IgnoreCycles` — keep it that way when adding new endpoints.

**Login returns 401 but I'm sure the password is right**
The bootstrap password is _only_ used the first time the user is created. To reset, stop the container, delete `data/ild.db` (or the matching row), restart with the new `ILD_PASSWORD`.

**Loop run stuck in `Running` after a crash**
On startup, `RecoveryManager` reads each run's `RecoveryPolicy`:

- `AutoResume` — resumes via `LoopEngine.RunAsync`.
- `NeedsReview` — moves the work item to `HumanFeedback`.
- `Cancel` — cancels the run.

Set the policy you want on the **template**, not the run.

**Webhook doesn't update PR status**
Ensure the Forgejo / Gitea webhook posts to `POST /api/v1/webhooks/forgejo` with auth disabled for that path (it's in the `AuthOptions.ExcludedPaths` list). The work item is matched on `WorkItem.PrUrl`.

---

## License

See repository for license details.
