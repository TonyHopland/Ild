# Architecture

ILD is split into a **local ILD instance** that owns loop execution, repository operations, previews, and the realtime UI, and a **standalone WorkItem Server** that owns the work-item source of truth so multiple ILD instances can coordinate safely.

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

## Module boundaries

- **`ILD.WorkItemServer`** is authoritative for work-item state, dependencies, tags, conversations, repository association, and claim semantics.
- **`ILD.Api`** is the main host for auth, controllers, SignalR hubs, startup seeding, and recovery.
- **`ILD.Core`** owns loop execution, repository operations, polling orchestration, preview control, metrics generation, and AI adapter selection.
- **`ILD.Data`** stores loop runs, templates, repositories, providers, users, event logs, adapter session snapshots, and other ILD-local state.
- **`ILD.McpServer`** exposes local agent tools against the ILD API.
- **`frontend/`** is the React SPA surfaced at runtime from `wwwroot/` and proxied through Vite+ during development.

## Realtime channel

Two SignalR hubs are mapped:

- `/hubs/loop-run` — run-level events
- `/hubs/work-item` — work-item-level events

Both emit messages of shape `{ type, payload, timestamp }`. Event payload types are statically modelled in `frontend/src/types/signalr.ts`. Known events include `NodeStateChanged`, `LoopRunStateChanged`, `WorkItemStateChanged`, `HumanFeedbackRequired`, `EventLogged`, `RunPaused`, `RunResumed`, `DependencyResolved`, and `NodeProgress` (real-time progress lines consumed by the live-stream view).

## Deeper internals

For the full engineering reference — node execution semantics, recovery policies, edge-traversal limits, storage layout, and the auth/webhook enforcement model — see [CONTEXT.md](../CONTEXT.md). For product scope and requirements, see [PRD.md](./PRD.md).
