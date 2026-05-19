# ILD (In-Loop Development)

A containerized AI-assisted development platform that runs configurable workflow loops over work items, integrating git worktrees, AI agents, and pull request lifecycle management.

## Language

### Core Concepts

**WorkItem**:
A unit of work — code change, planning session, or any task type. May or may not involve a repository, worktree, or PR.
_Avoid_: task, ticket, issue, story

**LoopTemplate**:
A named, versioned workflow definition expressed as a directed graph of nodes (Start, Cmd, AI, Human, PR, Cleanup). Auto-versioned on every save.
_Avoid_: workflow, pipeline, template (without "loop")

**LoopTemplateVersion**:
An immutable snapshot of a LoopTemplate's graph at a point in time. Created on every save. WorkItems pin a version at first run start.
_Avoid_: template revision, template snapshot

**LoopRun**:
A single execution of a pinned LoopTemplateVersion against a WorkItem. Tracks node states, edge traversals, and timing.
_Avoid_: execution, run, instance

**Worktree**:
A per-WorkItem git worktree created on first run, destroyed when Cleanup executes. Each new run after cleanup creates a fresh worktree.
_Avoid_: workspace, checkout

### Node Types

**Start Node**:
The entry point of a loop graph. Optionally creates a worktree and branch. If the base git repository is missing at `repo.WorktreesPath` (or fallback `data/repos/{repo-id}`), it is cloned on demand. If the base repository already exists, `git fetch origin` + `git reset --hard origin/<defaultBranch>` runs before worktree creation to ensure the latest state (best-effort; sync failure does not fail the node). After worktree creation, the branch is rebased onto `origin/<defaultBranch>` — rebase failure fails the node to prevent stale worktrees.
_Avoid_: init, setup

**Cmd Node**:
Executes a shell command in the worktree with a configurable timeout. Succeeds on exit code 0. Default timeout is 300 seconds.
_Avoid_: shell, command

**AI Node**:
Delegates execution to an `IAgentAdapter` resolved by `AiProvider.Type`. The adapter controls the full execution lifecycle including multi-turn loops, tool use, and internal state. Has a single `prompt` field regardless of whether the node starts fresh or resumes a bound session. If the graph needs different first-turn and follow-up prompts, model that explicitly with upstream `Prompt` nodes. If `provider` config specifies a provider that doesn't exist, the node fails (no fallback to default or first provider). Supports `rejectPattern` — a regex that, when matched against the AI output, causes the node to fail and route to the `on_failure` edge. Default timeout is 300 seconds.
_Avoid_: LLM node, model node

**Human Node**:
A pause point that awaits human input. The input becomes `{{PreviousNode.Output}}` for downstream nodes. Can form conversational loops with AI nodes (e.g., grill-me planning).
_Avoid_: approval gate, review step

**PR Node**:
Creates a pull request (or reuses existing one via `WorkItem.PrUrl`). Before PR creation, automatically commits any uncommitted changes, pushes the branch, fetches the remote, and verifies the branch has commits ahead of the target branch — if the branch has 0 commits ahead, the node fails (no-changes guard). Waits for webhook events: merge routes to `on_success`, rejection routes to `on_failure`. Supports `prDescriptionTemplate` config field with placeholder resolution (`{{WorkItem.Title}}`, `{{WorkItem.Description}}`, `{{PreviousNode.Output}}`); falls back to `WorkItem.Description` if unset. Makes the PR lifecycle explicit in the graph.
_Avoid_: repository node, git node

**Cleanup Node**:
A terminal (sink) node with only incoming edges. Destroys the worktree when executed, ending the LoopRun.
_Avoid_: teardown, end node

### Execution & Recovery

**Agent Adapter**:
A pluggable component implementing `IAgentAdapter` that handles full AI node execution. Each adapter declares `SupportedProviderTypes` and is auto-registered via DI. Adapter instance is scoped per AI node per `LoopRun` — sibling AI nodes in the same loop do not share state. Currently only the CLI-backed `opencode` and `pi` provider types are registered; there is no implicit default adapter type.
_Avoid_: LLM provider, model driver

**Tool Autonomy**:
Adapters have full autonomy over tool execution, but the engine now passes a per-node tool allowlist to the adapter runtime. Adapters still decide how to map those categories onto their own tool and permission systems.
_Avoid_: tool sandbox, tool permissions

**Event Log**:
Append-only audit stream per LoopRun. Serves as AI context and observability. Large payloads (>10KB) stored on disk; DB stores path reference. Retained for 7 days after successful completion; failed runs preserved until manually closed. Edge traversal counts are persisted to `LoopRunEdgeTraversal` rows.
_Avoid_: audit trail, log

**Recovery Policy**:
Per-LoopTemplate setting controlling crash recovery behavior: AutoResume, NeedsReview, or Cancel. On recovery, in-flight nodes are re-executed. AutoResume does not resume runs at Human or PR nodes in `WaitingHuman` state — those require explicit human action.
_Avoid_: restart policy, failover

**Best-Effort Guarantees**:
Event log writes and SignalR notifications are best-effort — failures in these side effects never cause node execution to fail.

**Node Refresh**:
The engine refreshes the WorkItem from the store before each node execution, ensuring the latest state is used.

### Work Item Lifecycle

**Human Feedback**:
A single WorkItem status for all cases requiring human attention: PR awaiting merge, node failure exhaustion, rebase conflicts, Human node input. Distinguished by `HumanFeedbackReason` string. When a Human node receives input, the input is appended to the event log. When a Human node receives a reject, the current node is marked as Failed.
_Avoid_: paused, blocked, stalled

**Ready**:
WorkItem status meaning all dependencies are Done and the work item can begin execution.
_Avoid_: available, queued

**Backlog**:
WorkItem status for items requiring human approval before entering the work queue. Whether new items land here or in Work Queue depends on a per-Repository setting.
_Avoid_: draft, planned

## Relationships

- A **WorkItem** has zero or more **LoopRun**s, but at most one active run at a time
- A **LoopRun** executes exactly one pinned **LoopTemplateVersion**. Status is `Running` until Cleanup executes, then `Completed`. PR merge/rejection is handled by a **PR Node** in the graph, not implicit.
- A **WorkItem** references a **LoopTemplate** directly. `LoopTemplateVersionId` is null ("Latest") until the first run starts, then pinned forever
- A **WorkItem** may have dependencies on other **WorkItem**s; it becomes ready when all dependencies reach `Done` status
- New **WorkItem**s land in either Backlog or Work Queue based on a per-**Repository** gating setting (Backlog = requires human approval to proceed, Work Queue = auto-flows)
- A **WorkItem** is optionally associated with a **Repository** (Plan-type items may not need one)
- A **Worktree** is created on first run, destroyed on Cleanup. Re-starting a WorkItem creates a fresh worktree
- `{{PreviousNode.Output}}` resolves to the source node of the incoming edge, not the chronologically previous execution
- `{{EventLog.LastN}}` defaults to N=10, capped at 50
- AI node always uses its single `prompt`; graph structure controls prompt variation across turns
- `AiProvider.Config` is a free-form JSON blob; each adapter reads what it needs
- Rebase happens only at loop start, not before each node
- Failed/cancelled WorkItems: "Done" destroys worktree and discards; "Backlog" fully resets for re-planning
- Safety net (max node execs + wall clock): finishes current node, then cancels the run

## Example Dialogue

> **Dev:** "When I create a work item, does it start running immediately?"
> **Domain expert:** "No — it lands in Backlog or Work Queue depending on the repository's gating setting. If it has no dependencies and lands in Work Queue, it auto-transitions to Ready. You still need to click Start."

> **Dev:** "Can I change the loop template for a running work item?"
> **Domain expert:** "No — the LoopTemplateVersion is pinned at first run start. Unstarted work items float to the latest version. Only after the first run does the version stay fixed."

> **Dev:** "What happens if the container crashes mid-loop?"
> **Domain expert:** "Recovery policy determines the action. With AutoResume, the in-flight node re-executes. Human node input is preserved. The safety net bounds how many times nodes can re-run."

## Flagged ambiguities

- "WorkItem" covers both code changes and non-code work (planning) — confirmed intentional. `WorktreePath` and `BranchName` are nullable to support this.
- Dependency readiness trigger is `Done` status (not `IsPrMerged`) — works for all WorkItem types
- "Latest" template version is resolved at first run start, not at WorkItem creation
- PR lifecycle is explicit in the graph via **PR Node**, not implicit engine behavior
- `WorkItem.Type` field is redundant — removed. Template assignment determines work type.
- Backlog vs Work Queue landing is per-Repository, not global or per-WorkItem

## API Versioning Policy

API routes use a manual prefix (`/api/v1/...`) hard-coded in `[Route]` attributes. There is **no** `Asp.Versioning` package wiring; breaking changes require introducing a new prefix (`/api/v2/...`) and keeping `/api/v1/...` working until clients migrate. List endpoints accept `skip`/`take` (default 100, max 500); event-log queries cap `limit` server-side at 500.

See the [Architecture](#architecture) section below for how the API layer composes with storage, auth, and realtime channels.

## Architecture

### API surface

- All controllers live under `ILD.Api/Controllers` and inherit `[Route("api/v1/...")]`. There is no implicit versioning middleware — see [API Versioning Policy](#api-versioning-policy).
- Auth is enforced by `ILD.Api/Middleware/AuthMiddleware.cs` via bearer-token session cookies/headers. Excluded paths: `/api/v1/auth/login`, `/api/v1/health`, `/api/v1/logging`, `/metrics`. The webhook endpoint (`/api/v1/webhooks/forgejo`) is **not** excluded; external callers must additionally pass HMAC verification (`WebhookSignatureVerifier`) on top of bearer auth.
- `AuthService.LoginAsync` auto-seeds the `admin` user the first time it sees a login attempt with the username `admin` and a non-empty `ILD_PASSWORD` env var.
- Webhook routes verify HMAC against `RemoteProvider.WebhookSecret` values; if no provider has a secret configured, all webhook calls are rejected with 401.
- `EventLog` query routes live on `LoopRunsController` (`GET /api/v1/loopruns/{id}/events`, `GET /api/v1/loopruns/{id}/events/{seq}`) — there is no separate `EventLogController`.
- `HttpClient` instances for AI providers and remote providers are obtained from `IHttpClientFactory` with named clients (`"openai"`, `"forgejo"`); failures surface as `AiProviderException` with cause-preserving inner exceptions.

### Storage layout

Storage paths come from configuration in this precedence order (highest first):

1. `ILD_DATA_PATH` env var (overrides everything; integration tests clear it explicitly)
2. `Storage:DataRoot` config value (default `./data`)
3. Falls back to `Path.GetFullPath("data")` if neither is set

Within `DataRoot`:

- `Storage:DatabaseFile` (default `ild.db`) — SQLite file. EF Core migrations run on startup via `dbContext.Database.Migrate()`.
- `Storage:WorktreesSubdir` (default `worktrees`) — root for per-WorkItem git worktrees. Each `Worktree` row's `Path` is rooted here.
- Event log payloads >10KB spill to `events/{looprrun-id}/{seq}.json`; DB rows store the relative path.

Tests must not share `DataRoot`. Integration tests use `ILD.Tests/Integration/ApiFactory.cs`, which generates a per-instance temp directory and an in-memory SQLite connection. Unit tests use `ILD.Tests/TestDb.cs`, whose XML doc comment describes the per-test isolation guarantees.

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

Routes in `frontend/src/App.tsx` are registered with `React.lazy(() => import(...))` and wrapped in a top-level `ErrorBoundary` so a render failure in one route can't take down the shell. Page-level errors surface through the shared `ErrorBanner` component (issue #036).
