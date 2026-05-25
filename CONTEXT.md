# ILD (In-Loop Development)

A containerized AI-assisted development platform that runs configurable workflow loops over work items, integrating git worktrees, AI agents, and pull request lifecycle management.

## Language

### Core Concepts

**WorkItem**:
A unit of work — code change, planning session, or any task type. May or may not involve a repository, worktree, or PR.
_Avoid_: task, ticket, issue, story

**LoopTemplate**:
A named, versioned workflow definition expressed as a directed graph of nodes (Start, Cmd, AI, Human, Prompt, PR, Cleanup). Auto-versioned on every save.
_Avoid_: workflow, pipeline, template (without "loop")

**LoopTemplateVersion**:
An immutable snapshot of a LoopTemplate's graph at a point in time. Created on every save. A LoopRun pins a specific version (resolved as the template's latest at the moment the run starts); subsequent runs on the same WorkItem re-resolve and may pick a newer version.
_Avoid_: template revision, template snapshot

**LoopRun**:
A single execution of a pinned LoopTemplateVersion against a WorkItem. Tracks node states, edge traversals, and timing.
_Avoid_: execution, run, instance

**Worktree**:
A per-WorkItem git worktree created on first run, destroyed when Cleanup executes. Each new run after cleanup creates a fresh worktree.
_Avoid_: workspace, checkout

### Node Types

**Start Node**:
The entry point of a loop graph. Optionally creates a worktree and branch. If the base git repository is missing at `repo.WorktreesPath` (or fallback `{DataRoot}/repos/{repo-id}`), it is cloned on demand. If the base repository already exists, `git fetch origin` is run (best-effort — fetch failure is swallowed) followed by `git reset --hard origin/<defaultBranch>`. Reset failure **does** fail the node, enforcing the run-isolation invariant that every run must start from a clean origin base. After worktree creation, the branch is rebased onto `origin/<defaultBranch>` — rebase failure also fails the node to prevent stale worktrees.
_Avoid_: init, setup

**Cmd Node**:
Executes a shell command in the worktree. Succeeds on exit code 0. Bounded by run-scoped cancellation only — no per-node timeout.
_Avoid_: shell, command

**AI Node**:
Delegates execution to an `IAgentAdapter` resolved by `AiProvider.Type`. The adapter controls the full execution lifecycle including multi-turn loops, tool use, and internal state. Has a single `prompt` field regardless of whether the node starts fresh or resumes a bound session. If the graph needs different first-turn and follow-up prompts, model that explicitly with upstream `Prompt` nodes. Provider resolution: if `aiProviderId` parses as a GUID, the executor looks it up and fails if the row is missing (no fallback). If `aiProviderId` is unset or not a GUID, the executor falls back to `GetDefaultAiProviderAsync` (the provider flagged as default), failing only if no default is configured. Supports `rejectPattern` — a regex that, when matched against the AI output, causes the node to fail and route to the `on_failure` edge. Bounded by run-scoped cancellation only — no per-node timeout.
_Avoid_: LLM node, model node

**Human Node**:
A pause point that awaits human input. The input becomes `{{PreviousNode.Output}}` for downstream nodes. Can form conversational loops with AI nodes (e.g., grill-me planning).
_Avoid_: approval gate, review step

**Prompt Node**:
Renders a templated text payload using the prompt placeholder pipeline (`{{WorkItem.*}}`, `{{PreviousNode.Output}}`, `{{EventLog.*}}`, etc.) and emits the result as its Output. Always routes to `on_success`. Used to compose the prompt that a downstream AI node will consume — especially when first-turn vs. follow-up prompts need to differ (see the AI Node entry).
_Avoid_: template node, text node

**PR Node**:
Creates a pull request (or reuses existing one via `WorkItem.PrUrl`). Before PR creation, automatically commits any uncommitted changes, pushes the branch, fetches the remote, and verifies the branch has commits ahead of the target branch — if the branch has 0 commits ahead, the node fails (no-changes guard). Waits for webhook events: merge routes to `on_success`, rejection routes to `on_failure`. Supports `prDescriptionTemplate` config field with placeholder resolution (`{{WorkItem.Title}}`, `{{WorkItem.Description}}`, `{{PreviousNode.Output}}`); falls back to `WorkItem.Description` if unset. Makes the PR lifecycle explicit in the graph.
_Avoid_: repository node, git node

**Cleanup Node**:
A terminal (sink) node with only incoming edges. Destroys the worktree when executed, ending the LoopRun.
_Avoid_: teardown, end node

### Execution & Recovery

**Agent Adapter**:
A pluggable component implementing `IAgentAdapter` that handles full AI node execution. Each adapter declares `SupportedProviderTypes` and is auto-registered via DI. Adapter instance is scoped per AI node per `LoopRun` — sibling AI nodes in the same loop do not share state. Currently the CLI-backed `opencode`, `pi`, and `claude-code` provider types are registered; there is no implicit default adapter type.
_Avoid_: LLM provider, model driver

**Tool Autonomy**:
Adapters have full autonomy over tool execution, but the engine now passes a per-node tool allowlist to the adapter runtime. Adapters still decide how to map those categories onto their own tool and permission systems.
_Avoid_: tool sandbox, tool permissions

**Event Log**:
Append-only audit stream per LoopRun. Serves as AI context and observability. Large payloads (>10KB) stored on disk; DB stores path reference. A background `EventLogRetentionSweeper` deletes entries older than `EventLogOptions.RetentionPeriod` (default 7 days) once per `RetentionSweepInterval` (default 24h), but **only** for runs whose linked WorkItem is in `Done` status — events for runs whose WorkItem is still Running/HumanFeedback/Backlog/etc. are preserved indefinitely.
_Avoid_: audit trail, log

**Edge Traversal Limits**:
The engine's runaway-graph safety net is **per edge**, not per node. Each edge's traversal count is tracked in-memory per run; if it exceeds the edge's `MaxTraversals` (or `LoopEngine.DefaultMaxEdgeTraversals = 50` when the edge leaves it null), the run is failed. There is no template-level execution cap.
_Avoid_: max node execs, run iteration limit

**Recovery Policy**:
Per-LoopTemplate setting controlling crash recovery behavior: AutoResume, NeedsReview, or Cancel. On recovery, stale `LoopRunNode` rows with `Status = Running` are transitioned to `Interrupted` and the engine re-enters the loop at `CurrentNodeId`; the node's `ExecuteAsync` is invoked fresh and re-checks its own preconditions. AutoResume does not resume runs at Human or PR nodes in `WaitingHuman` state — those require explicit human action.
_Avoid_: restart policy, failover

**Best-Effort Guarantees**:
Event log writes and SignalR notifications are best-effort — failures in these side effects never cause node execution to fail.

**Node Refresh**:
The engine refreshes the WorkItem from the store before each node execution, ensuring the latest state is used.

### Work Item Lifecycle

**Human Feedback**:
The WorkItem status for cases requiring **human** attention: PR awaiting merge, node failure exhaustion, rebase conflicts, Human node input. Distinguished by `HumanFeedbackReason` string. When a Human node receives input, the input is appended to the event log. When a Human node receives a reject, the current node is marked as Failed and the engine follows the `on_failure` edge (a Respond signal follows `on_respond`). Internal-resource waits (e.g. AI provider throttling) use `WaitingForIld` instead.
_Avoid_: paused, blocked, stalled

**WaitingForIld**:
WorkItem status used when a node cannot proceed because an internal resource (currently AI provider capacity) is unavailable. The LoopRun stays `Running` from the user's perspective; the scheduler (`RemoteWorkItemCoordinator`) automatically resumes the work item when the resource frees. Distinct from Human Feedback — no human action is required. Set via the `NodeOutcome.WaitingIld(reason)` outcome and paired with `HumanFeedbackReasons.AiProviderThrottled` on the WorkItem.
_Avoid_: throttled, queued, backpressure

**Ready**:
WorkItem status meaning all dependencies are Done and the work item can begin execution.
_Avoid_: available, queued

**Backlog**:
WorkItem status for items requiring human approval before entering the work queue. Whether new items land here or in Work Queue depends on a per-Repository setting.
_Avoid_: draft, planned

## Relationships

- A **WorkItem** has zero or more **LoopRun**s, but at most one active run at a time
- A **LoopRun** executes exactly one pinned **LoopTemplateVersion**. Status is `Running` until Cleanup executes, then `Completed`. PR merge/rejection is handled by a **PR Node** in the graph, not implicit.
- A **WorkItem** is _not_ directly linked to a **LoopTemplate**. At run start the engine resolves a template from `WorkItem.Tags` via `ILoopTemplateResolver`; tags must match exactly one template (zero or multiple matches → HumanFeedback). The matched template's latest version is then pinned on the new **LoopRun** row via `LoopTemplateVersionId`. Each subsequent run resolves again, so a template edited between runs takes effect on the next run.
- A **WorkItem** may have dependencies on other **WorkItem**s; it becomes ready when all dependencies reach `Done` status
- New **WorkItem**s land in either Backlog or Work Queue based on a per-**Repository** gating setting (Backlog = requires human approval to proceed, Work Queue = auto-flows)
- A **WorkItem** is optionally associated with a **Repository** (Plan-type items may not need one)
- A **Worktree** is created on first run, destroyed on Cleanup. Re-starting a WorkItem creates a fresh worktree
- `{{PreviousNode.Output}}` resolves to the source node of the incoming edge, not the chronologically previous execution
- `{{EventLog.LastN}}` returns the last 10 event-log summary entries (fixed — not parameterizable)
- AI node always uses its single `prompt`; graph structure controls prompt variation across turns
- `AiProvider.Config` is a free-form JSON blob; each adapter reads what it needs
- Rebase happens only at loop start, not before each node
- Failed/cancelled WorkItems: "Done" destroys worktree and discards; "Backlog" fully resets for re-planning
- Safety net (max edge traversals): when an edge is traversed more than its `MaxTraversals` (default 50 via `LoopEngine.DefaultMaxEdgeTraversals`), the engine fails the run with "Max traversals exceeded for edge …"

## Example Dialogue

> **Dev:** "When I create a work item, does it start running immediately?"
> **Domain expert:** "Maybe. It lands in Backlog or Work Queue depending on the repository's gating setting. If it has no dependencies and lands in Work Queue, it auto-transitions to Ready, and from there the scheduler will pick it up on its next poll and transition it to Running (subject to the global pause toggle and max-concurrent-runs cap)."

> **Dev:** "Can I change the loop template for a running work item?"
> **Domain expert:** "Not for the in-flight run — every LoopRun pins the template version it started with, so editing the template mid-run won't disturb it. But the next run on the same WorkItem will re-resolve from tags and pick the latest version, including any edits."

> **Dev:** "What happens if the container crashes mid-loop?"
> **Domain expert:** "Recovery policy determines the action. With AutoResume, the in-flight node re-executes. Human node input is preserved. The safety net bounds how many times nodes can re-run."

## Flagged ambiguities

- "WorkItem" covers both code changes and non-code work (planning) — confirmed intentional. `WorktreePath` and `BranchName` are nullable to support this.
- Dependency readiness trigger is `Done` status (not `IsPrMerged`) — works for all WorkItem types
- Template choice is resolved per LoopRun (not stored on the WorkItem) by matching `WorkItem.Tags` against templates; the matched template's latest version is pinned on the LoopRun at run start
- PR lifecycle is explicit in the graph via **PR Node**, not implicit engine behavior
- `WorkItem.Type` field is redundant — removed. Template assignment determines work type.
- Backlog vs Work Queue landing is per-Repository, not global or per-WorkItem

## API Versioning Policy

API routes use a manual prefix (`/api/v1/...`) hard-coded in `[Route]` attributes. There is **no** `Asp.Versioning` package wiring; breaking changes require introducing a new prefix (`/api/v2/...`) and keeping `/api/v1/...` working until clients migrate. List endpoints accept `skip`/`take` (default 100, max 500); event-log queries cap `limit` server-side at 500.

See the [Architecture](#architecture) section below for how the API layer composes with storage, auth, and realtime channels.

## Architecture

### API surface

- All controllers live under `ILD.Api/Controllers` and inherit `[Route("api/v1/...")]`. There is no implicit versioning middleware — see [API Versioning Policy](#api-versioning-policy).
- Auth is enforced by `ILD.Api/Middleware/AuthMiddleware.cs` via bearer-token session cookies/headers. The middleware only **enforces** auth on `/api/*`, `/hubs/*`, and `/metrics`; everything else (SPA routes, static assets when `AuthOptions.AllowStaticFiles` is on — the default) is allowed through so the SPA shell can serve. Within the enforced scope, `AuthOptions.ExcludedPaths` adds explicit exemptions: `/api/v1/auth/login`, `/api/v1/health`, `/api/v1/logging`, `/metrics`. The webhook endpoints (`/api/v1/webhooks/forgejo` and `/api/v1/webhooks/github`) are **not** excluded; external callers must additionally pass HMAC verification via `IRemoteGitProviderAdapter.VerifyWebhookSignature` (the relevant adapter owns the check) on top of bearer auth.
- `AuthService.LoginAsync` auto-seeds the `admin` user the first time it sees a login attempt with the username `admin` and a non-empty `ILD_PASSWORD` env var.
- Webhook routes verify HMAC against `RemoteProvider.WebhookSecret` values; if no provider has a secret configured, all webhook calls are rejected with 401.
- `EventLog` query routes live on `LoopRunsController` (`GET /api/v1/loopruns/{id}/events?cursor=&limit=` for the cursor-paginated list, `GET /api/v1/loopruns/{id}/events/payload?sequence=N` for the spilled large-payload fetch) — there is no separate `EventLogController`.
- `HttpClient` instances for AI providers and the work-item server are registered as **typed clients** via `AddHttpClient<TInterface, TImpl>` (no named clients). Failures from AI calls surface as `AiProviderException` with cause-preserving inner exceptions.

### Storage layout

**Database (production):** PostgreSQL via Npgsql. The connection string is read from the `ILD_DB_CONNECTION_STRING` env var (or the same-named configuration key as a fallback). When a connection string is configured, `dbContext.Database.Migrate()` runs on startup. When unset (e.g. integration tests) the host skips data-layer registration and the test factory substitutes its own `DbContext` + stores, calling `EnsureCreated()` instead.

**Filesystem paths.** The data root is resolved in this precedence order (highest first):

1. `ILD_DATA_PATH` env var (overrides everything; integration tests clear it explicitly)
2. `Storage:DataRoot` configuration value
3. The literal default `"data"` (relative to the host's working directory)

`Storage:WorktreesSubdir` (default `"worktrees"`) is appended to `DataRoot` to compose the worktrees root, **unless** `ILD_WORKTREES_PATH` is set, which overrides the worktrees path outright. Each `Worktree` row's `Path` is rooted under the resolved worktrees path.

Event log payloads larger than `EventLogOptions.LargePayloadThresholdBytes` (default 10 KB) spill to `EventLogOptions.PayloadDirectory` (default `data/payloads`) at `{run-id}/{sequence}.json`; the DB row stores the relative path.

**Database (tests).** Integration tests use `ILD.Tests/Integration/ApiFactory.cs`, which generates a per-instance temp directory and an in-memory SQLite connection. Unit tests use `ILD.Tests/TestDb.cs`, whose XML doc comment describes the per-test isolation guarantees. SQLite is **only** used in tests; production deployments must point at Postgres.

### SignalR realtime channel

Two hubs are mapped:

- `/hubs/loop-run` — broadcasts run-level events
- `/hubs/work-item` — broadcasts work-item-level events

Both emit messages of shape `{ type: string; payload: T; timestamp: string }`. As of issue #035 the payload `T` is statically typed in the frontend by `frontend/src/types/signalr.ts`'s `SignalREventPayloads` map. The known event names are:

- `NodeStateChanged`
- `LoopRunStateChanged`
- `WorkItemStateChanged`
- `HumanFeedbackRequired`
- `EventLogged`
- `RunPaused`
- `RunResumed`
- `DependencyResolved`
- `NodeProgress` — real-time progress lines during node execution, consumed by the LiveStream component

The frontend hook `useSignalR.on<E>(eventType, handler)` resolves the payload type from the map; unknown event names fall through to `unknown` so the call site is forced to narrow before use.

### Frontend route loading

Routes in `frontend/src/App.tsx` are wrapped in a top-level `ErrorBoundary` so a render failure in one route can't take down the shell. The heavier editor and live-monitor pages (`LoopEditor`, `LoopRunMonitor`) are code-split via `React.lazy(() => import(...))` and wrapped in a `Suspense` boundary; the remaining pages (Login, Taskboard, EventLogViewer, Settings, Repositories, RemoteProviders, AiProviders) are imported eagerly because their footprint doesn't justify a separate chunk. Page-level errors surface through the shared `ErrorBanner` component (issue #036).
