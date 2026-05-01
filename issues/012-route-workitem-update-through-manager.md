## What to build

`WorkItemsController.Update` (PUT `/workitems/{id}`) directly manipulates `AppDbContext` instead of going through `IWorkItemManager`. This bypasses business logic, SignalR notifications, and event publishing that the manager handles.

Route the update through `IWorkItemManager.UpdateAsync`. Also route the fire-and-forget `Task.Run(() => le.RunAsync(...))` in Start and Transition endpoints through a proper background service or at minimum track the task with error handling.

## Acceptance criteria

- [x] `WorkItemsController.Update` delegates to `IWorkItemManager` instead of directly using `DbContext`
- [x] `WorkItemsController.Start` and `Transition` track the LoopRun task (not fire-and-forget)
- [x] Unhandled exceptions from loop runs are logged (not swallowed)
- [x] SignalR notifications fire on work item updates
- [x] All existing tests still pass

## Blocked by

- Blocked by #5 (Fix Store Update methods — the manager relies on correct store behavior)
