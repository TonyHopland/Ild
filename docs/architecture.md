# Architecture

ILD is split into a **local ILD instance** that owns loop execution, repository operations, previews, and the realtime UI, and a **standalone WorkItem Server** that owns the work-item source of truth so multiple ILD instances can coordinate safely.

```text
Browser (React 19 + Vite+)
  -> ILD.Api (/api/v1, /hubs/loop-run, /hubs/work-item, /hubs/chat)
       -> ILD.Core services
            - LoopEngine
            - WorkItemManager (remote-backed)
            - RepositoryManager
            - RecoveryManager
            - RemoteWorkItemPoller / Coordinator / StartupReconciler
            - Adapter registry + AI node executors
       -> ILD.Data (EF Core stores)
       -> PostgreSQL in the supported compose stack
       -> runs in containers or pods, each with a volume of its own; ILD's runtime files under /data, and preview volumes and repository clones under /worktrees

ILD.Api <-> ILD.WorkItemServer (HTTP + API key)
ILD.WorkItemServer -> PostgreSQL in the supported compose stack
ILD.McpServer -> local ILD API for agent-facing work-item tools
```

## Module boundaries

- **`ILD.WorkItemServer`** is authoritative for work-item state, dependencies, tags, repository association, and claim semantics. Its API is a contract that may be implemented over an external tracker, so Ild-specific data about a work item belongs in `ILD.Data`, not on it ([ADR-0025](./adr/0025-workitem-server-contract-is-tracker-mappable.md)). Repository association is one of the existing fields that ADR leaves for a later audit.
- **`ILD.Api`** is the main host for auth, controllers, SignalR hubs, startup seeding, and recovery.
- **`ILD.Core`** owns loop execution, repository operations, polling orchestration, preview control, metrics generation, and AI adapter selection.
- **`ILD.Data`** stores loop runs, templates, repositories, providers, users, event logs, adapter session snapshots, and other ILD-local state.
- **`ILD.McpServer`** exposes local agent tools against the ILD API.
- **`frontend/`** is the React SPA surfaced at runtime from `wwwroot/` and proxied through Vite+ during development.

## Realtime channel

Three SignalR hubs are mapped:

- `/hubs/loop-run` — run-level events
- `/hubs/work-item` — work-item-level events, and the instance-wide settings and log events
- `/hubs/chat` — chat events. A client subscribes to each chat it shows (`SubscribeToChat`) and to a per-user inbox (`SubscribeToChatInbox`) for the events that concern all of a user's chats, such as unread counts, so it can follow several chats at once

All emit messages of shape `{ type, payload, timestamp }`. Event payload types are statically modelled in `frontend/src/types/signalr.ts`, which is the complete catalogue; the list below is a partial one. Known events include `NodeStateChanged`, `LoopRunStateChanged`, `WorkItemStateChanged`, `HumanFeedbackRequired`, `EventLogged` (sent for every event stored on a run, with the event's id and timestamp, so the Action tab reads the run's conversation on from the last event it has), `RunPaused`, `RunResumed`, `DependencyResolved`, and `NodeProgress` (real-time progress lines consumed by the live-stream view); the work-item hub also carries the instance-wide settings events `SchedulerStateChanged`, `NetworkPolicyChanged`, `NetworkLogAppended` and `NetworkLogCleared`. Further events cover run halts (`RunHalted`), pull requests (`PrSnapshotChanged`, `PrQueueChanged`), previews (`PreviewStateChanged`), the board (`WorkItemRunProgressed`, `WorkItemPrStatusChanged`, `WorkItemBoardReordered`, `WorkItemDeleted`), the log (`LogEntryAppended`), the agents' proposals (`…ProposalsChanged`) and, on the chat hub, `ChatMessageAppended`, `ChatTurnStarted`, `ChatTurnProgress`, `ChatTurnCompleted`, `ChatLoopUpdate`, `ChatTitleChanged`, `ChatActivityChanged`, `ChatUnreadChanged` and `ChatSchedulesChanged`.

## Why it's shaped this way

The decisions behind this architecture — the WorkItem Server split, manual API versioning, the global AI-traversal cap, the explicit PR node, tag-based template resolution, the clean-origin run isolation invariant, and adapter-owned AI execution — are recorded as [ADRs](./adr/).

## Deeper internals

For the full engineering reference — node execution semantics, recovery policies, the AI traversal cap ([ADR-0018](./adr/0018-global-ai-traversal-cap.md)), storage layout, and the auth/webhook enforcement model — see [CONTEXT.md](../CONTEXT.md). For the API design and conventions, see [api.md](./api.md). For product scope and requirements, see [PRD.md](./PRD.md).
