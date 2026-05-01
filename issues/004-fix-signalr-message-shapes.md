## What to build

The backend SignalR hubs send events with raw argument shapes that don't match what the frontend expects:

- `LoopRunHub.NotifyLoopRunStateChanged` sends `(runId, oldStatus, newStatus)` but frontend casts `message.payload` to `LoopRun`
- `LoopRunHub.NotifyNodeStateChanged` sends `(runId, nodeId, status, output)` but frontend expects a structured payload
- `WorkItemHub.NotifyHumanFeedbackRequired` sends `(workItemId, reason)` but frontend destructures `message.payload.workItemId`
- `WorkItemHub` methods are defined as public hub methods (client-invoked) rather than server-initiated broadcasts via `IHubContext`

**Decision logged:** Use a single typed DTO payload per event. Backend sends `SendAsync("LoopRunStateChanged", { runId, oldStatus, newStatus })`. Hub `Notify*` methods remain client-invokable for convenience, but all actual broadcasts go through `IHubContext` from the service layer (`SignalRRunNotifier` / `SignalRWorkItemNotifier`).

Align the event contracts end-to-end. Create a `SignalRWorkItemNotifier` (mirroring `SignalRRunNotifier`) so the WorkItemManager can broadcast state changes through `IHubContext<WorkItemHub>`.

## Acceptance criteria

- [x] Define shared SignalR event DTOs (one per event type: NodeStateChanged, EventLogged, LoopRunStateChanged, WorkItemStateChanged, HumanFeedbackRequired, etc.)
- [x] Backend wraps each event in a single DTO payload (not positional args)
- [x] `LoopRunHub` broadcasts use `IHubContext` from the service layer (already done via `SignalRRunNotifier`)
- [x] Create `SignalRWorkItemNotifier` with `IHubContext<WorkItemHub>` for server-initiated broadcasts
- [x] Frontend SignalR handlers accept typed DTO arguments (no `message.payload` mis-casts)
- [x] Remove all `as any` casts from `on()` calls in Taskboard and LoopRunMonitor
- [x] Taskboard receives and displays work item state changes in real time
- [x] LoopRunMonitor receives and displays node state changes in real time

## Blocked by

None - can start immediately
