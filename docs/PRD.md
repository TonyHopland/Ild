# ILD — Product Requirements Document

## Purpose

ILD is a local execution system for AI-assisted development loops that coordinates work through a shared WorkItem Server. The product goal is to let one or more developers run their own ILD instances while keeping work-item ownership, execution state, repository worktrees, review steps, and AI activity visible and controllable.

This PRD is aligned to the current implementation rather than the earlier single-container and SQLite-only concept. Where the product still has gaps relative to its desired end state, those gaps are called out explicitly.

## Product Shape

ILD is composed of:

- A local ILD host exposing the taskboard UI, loop-template editor, provider settings, loop-run views, preview controls, and agent-facing API tools.
- A standalone WorkItem Server that owns the shared work-item record and claim semantics.
- A PostgreSQL-backed persistence layer in the supported deployment path.
- Per-run local git worktrees plus ILD-local runtime state for loop runs, templates, event logs, providers, sessions, and previews.

## Primary Outcomes

The product should let a developer:

1. Create, categorize, and prioritize work items in a shared queue.
2. Resolve those work items through reusable loop templates made of `Start`, `Cmd`, `AI`, `Human`, `Prompt`, `PR`, and `Cleanup` nodes.
3. Run work either manually from the UI or automatically through background polling.
4. Use different AI execution backends through adapters instead of a single hard-coded LLM path.
5. Pause for review, feed back context, inspect run history, and recover safely after restarts.

## Current Requirements

### Shared Work-Item Coordination

- The WorkItem Server is authoritative for work-item state.
- Work items carry title, description, status, priority, tags, dependencies, conversation history, repository ID, optional creating loop-run ID, and optional human-feedback actions.
- `Transition(Running)` must remain the only validated claim operation on the shared server.
- Heartbeats must refresh active items so stale work can be reclaimed back to `Ready`.
- Human responses must move items through `WaitingForIld` so the owning ILD instance can resume execution cleanly.

### Local ILD Execution

- ILD owns loop templates, loop runs, worktrees, PR state, previews, and AI session snapshots.
- A Ready item can be started manually from the UI.
- A Ready item can also be claimed automatically by the remote poller when concurrency allows.
- Tag-based template resolution must happen before a remote claim is finalized; unresolved or ambiguous matches must escalate to human feedback.
- Startup reconciliation must re-check tracked remote items and either resume, re-track, or clean up local state.

### Loop Templates and Execution

- Templates are versioned on each save and runs pin the version they started with.
- Validation must enforce exactly one `Start` node, at least one `Cleanup` node, reachability from `Start`, and at least one path to cleanup.
- The loop engine remains sequential per run and acts as the sole state machine: node executors yield `NodeOutcome` values (`NodeStarting`, `Success(EdgeType)`, `Fail(EdgeType)`, `WaitingAction`, `WaitingIld`, `Terminal`) and the engine performs all persistence, routing, and status transitions.
- Failure routing is graph-driven: `Fail` outcomes carry an `EdgeType` — typically `OnFailure`. Human and PR nodes also fail via `OnFailure` when an external reject signal arrives. There are no automatic per-node retries — retry is modeled with `OnFailure` edges back to the same node and bounded by each edge's `MaxTraversals` (default `LoopEngine.DefaultMaxEdgeTraversals = 50` when null).
- Pause and cancellation must be cooperative so commands and long-running adapters can stop safely.

### AI Execution Model

- AI nodes resolve an `IAgentAdapter` from the configured `AiProvider.Type`.
- The currently supported provider types are `opencode`, `pi`, and `claude-code`.
- All three providers are CLI-backed: the adapter spawns the provider's CLI inside the worktree and reads structured output.
- The adapter, not the node executor, owns the provider-specific execution lifecycle.
- Prompt templates remain single-field per AI node and support ILD placeholder expansion.
- Session-aware adapters may persist and restore session snapshots across node visits.
- Agent-facing work-item tools are exposed through ILD's local MCP server integration.

### Human Review and PR Flow

- Human nodes must suspend execution and surface input controls in the taskboard.
- PR nodes create or reuse pull requests and suspend until merge or rejection is observed.
- Manual PR linking and manual merged-state confirmation remain available as operational fallbacks.
- Failed or cancelled work must support explicit cleanup to either `Done` or `Backlog`.

### UI and Realtime Behavior

- The taskboard remains the main interaction surface.
- Work-item details should expose dependencies, run history, conversation history, preview controls, PR state, and human feedback actions.
- SignalR continues to push work-item changes, run-state changes, event logs, and node progress.
- Settings pages must remain available for backend log level, repositories, remote providers, and AI providers.

### Auth and Security

- User auth remains single-user username/password with PBKDF2 hashing and a persisted session token.
- Frontend sessions persist across reloads through stored auth state.
- WorkItem Server auth remains bearer API-key based.
- Secrets must not be committed in source-controlled config.

### Deployment

- The supported deployment is the repo's compose stack with PostgreSQL, ILD, and the WorkItem Server.
- ILD and the WorkItem Server both run EF Core migrations when connection strings are configured.
- The ILD runtime image may optionally include OpenCode, Pi, Node.js, the .NET SDK, Chrome, and custom CA certificates.

## Implementation Decisions

| Area                      | Decision                                                      |
| ------------------------- | ------------------------------------------------------------- |
| Work-item source of truth | Standalone WorkItem Server                                    |
| Local execution state     | Stored in ILD-local EF Core tables                            |
| Supported deployment      | Multi-service compose stack with PostgreSQL                   |
| Start model               | Manual start and autonomous polling both supported            |
| Loop execution            | Sequential per run                                            |
| Recovery                  | Per-run policy with startup reconciliation                    |
| AI architecture           | Adapter-driven, resolved by provider type                     |
| Tool execution            | Provider-owned: each CLI adapter maps the per-node allowlist  |
| Realtime                  | SignalR hubs for loop-run and work-item streams               |
| Auth                      | Session-token auth in ILD; bearer API keys in WorkItem Server |

## Module Boundaries

1. `ILD.Api` hosts controllers, SignalR hubs, auth middleware, template seeding, and startup recovery.
2. `ILD.Core` owns loop execution, repository management, polling coordination, preview services, recovery, metrics, and AI adapters.
3. `ILD.Data` owns ILD-local entities and store interfaces.
4. `ILD.WorkItemServer` owns remote work-item persistence and server-side claim semantics.
5. `ILD.McpServer` exposes local agent tools against the ILD API.
6. `frontend/` owns the SPA and taskboard surfaces.

## Observability Requirements

- Structured JSON logging remains the default.
- Runtime log-level changes are supported through the ILD API and settings UI.
- A Prometheus-style metrics endpoint remains exposed at `/metrics`.
- The ILD health endpoint currently checks database and disk status.
- Remote-provider connectivity should also be represented in health output, but that requirement is not fully implemented yet.

## Known Gaps

The following items are still desired but not fully delivered in the current codebase:

1. The ILD health endpoint reports database and disk health but does not perform a real remote-provider connectivity probe yet.
2. Documentation and product messaging should continue avoiding references to the removed single-container and SQLite-first architecture.

## Non-Goals

- Multi-tenant product behavior
- OAuth or enterprise identity providers
- Mobile-native clients
- Automatic merge approval
- Plugin marketplaces for custom node types
- General-purpose collaboration editing in the browser

## Validation Philosophy

- Tests should continue to verify external behavior rather than implementation details.
- Business logic should stay covered through unit and integration tests around loop execution, recovery, polling, auth, provider adapters, and repository behavior.
- Frontend tests should continue covering route-level and interaction-level behavior for core taskboard and settings flows.

## Further Notes

- The system is designed for a single developer running it in a container for personal AI-assisted development
- Forgejo is the primary git provider target, but the `IRemoteProvider` interface enables GitHub and GitLab support
- The event log is the source of truth for AI context — keeping it clean and well-structured is important
- Template versioning is critical: started workitems must not break if the template changes
- Recovery is a first-class concern: the system must survive container restarts gracefully
- The visual loop editor (React Flow) is a key UX surface — investing in good custom node rendering and edge UI is important
- Tool access should be conservative by default — read-only unless explicitly granted
- Worktree and branch are one per run, kept after the run finishes for inspection and reclaimed only when the run is deleted (retention sweep or manual delete) — each new run creates a fresh worktree
- Vite+ is alpha; keep frontend dependencies minimal and be prepared for potential breaking changes
