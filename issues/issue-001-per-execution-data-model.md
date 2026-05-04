# Issue #1 — Backend: Per-Execution Data Model

## What to build

Change the `LoopRunNode` model from one-row-per-template-node to one-row-per-execution, add `RunNodeId` FK to `EventLog`, wire `LogEventAsync` to broadcast via `EventLogged` SignalR, and update the `LoopRunsController` API to return all execution rows.

Currently `LoopEngine.ExecuteNodeWithRetryAsync` calls `CreateOrLoadRunNodeAsync(run.Id, node.Id)` which creates or reuses a single `LoopRunNode` per template node. Each traversal of a node reuses that row, overwriting previous execution data. The new model creates a fresh `LoopRunNode` row for every traversal so the frontend can render one timeline entry per execution.

Additionally, `EventLog` rows currently reference only `NodeId` (template node ID). With multiple executions of the same template node, events from different executions are indistinguishable. Adding `RunNodeId` ties each event to a specific execution instance.

Finally, `LogEventAsync` writes to the event log store but does not broadcast via SignalR. The `EventLogged` SignalR event exists in the notifier and hub but is never called from the engine. Wiring this up enables real-time event delivery to the frontend.

## Acceptance criteria

- [ ] `LoopEngine.ExecuteNodeWithRetryAsync` creates a new `LoopRunNode` row for each traversal (not `CreateOrLoad`, but `CreateNew`)
- [ ] `EventLog` entity has a new nullable `Guid? RunNodeId` column with FK to `LoopRunNode`
- [ ] EF Core migration created and applies cleanly (`dotnet ef migrations add` + `dotnet ef database update`)
- [ ] `LogEventAsync` calls `_notifier.EventLoggedAsync(runId, data)` after writing to the event log store
- [ ] `EventLoggedPayload` extended with `eventType` and `nodeId` fields
- [ ] `LoopRunsController` GET `/api/v1/loopruns/{id}` returns all `LoopRunNode` execution rows (not aggregated per template node)
- [ ] Frontend `LoopRunNode` type updated to reflect the new shape (may need `executionIndex` or similar)
- [ ] Existing integration tests pass; tests that assert single-row-per-node behavior are updated

## Blocked by

None - can start immediately.
