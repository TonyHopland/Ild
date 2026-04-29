## What to build

Several EF Core configuration gaps in `AppDbContext`:

**A — Missing cascade delete configuration:** Only 3 of 12+ relationships have explicit `OnDelete` behavior. Configure cascade delete for: `LoopRun` → `WorkItem`, `LoopRunNode` → `LoopRun`, `LoopRunEdgeTraversal` → `LoopRun`, `EventLog` → `LoopRun`, `Repository` → `RemoteProvider`.

**B — Duplicate `ForeignKey` attributes:** Every entity relationship has `[ForeignKey]` on both the FK scalar property AND the navigation property. Remove the redundant attributes.

**C — `LoopRunStore.GetByWorkItemAsync` returns only first run:** Should have a method to get all runs for a WorkItem (for run history in the modal) and a method to get the current run.

**D — `WorkItemStore.GetByRepositoryAsync` returns only one item:** Should return `IReadOnlyList<WorkItem>` for all work items in a repository.

**E — Unused `using ILD.Data.Enums` imports:** Remove dead imports from store interface and implementation files.

## Acceptance criteria

- [ ] Cascade delete configured for parent→child relationships (LoopRun, LoopRunNode, EventLog, etc.)
- [ ] Duplicate `ForeignKey` attributes removed (keep one per relationship)
- [ ] `ILoopRunStore` has `GetAllByWorkItemAsync` and `GetCurrentByWorkItemAsync` methods
- [ ] `IWorkItemStore.GetByRepositoryAsync` returns `IReadOnlyList<WorkItem>`
- [ ] Dead `using` imports removed
- [ ] All existing tests still pass

## Blocked by

None - can start immediately
