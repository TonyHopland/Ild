## What to build

Several services issue redundant or N+1 database queries that should collapse to a single query.

**A — `WorkItemManager.GetDependencies` / `GetDependents` N+1:** [WorkItemManager.cs](ILD.Core/Services/Implementations/WorkItemManager.cs#L108-L133) iterates every `WorkItemStatus` enum value and calls `GetByStatusAsync()` per status, then filters in memory. Should be one `Where(w => ids.Contains(w.Id))` query.

**B — `WorkItemManager.IsReadyAsync` N+1:** [WorkItemManager.cs](ILD.Core/Services/Implementations/WorkItemManager.cs#L135-L149) has the same per-status loop for a simple "are all deps Done?" check. Replace with a single query over the dependency id list.

**C — Redundant template load in `LoopEngine.RunAsync`:** [LoopEngine.cs](ILD.Core/Services/Implementations/LoopEngine.cs#L212-L225) calls `GetVersionByIdAsync` then `GetLoopTemplateByVersionIdAsync` then loads nodes/edges separately. Add a single store method (e.g. `GetTemplateGraphByVersionIdAsync`) that uses `.Include(t => t.LoopNodes).ThenInclude(n => n.OutgoingEdges)` so the engine starts a run with one round-trip.

## Acceptance criteria

- [x] `GetDependencies` / `GetDependents` issue at most one DB query each
- [x] `IsReadyAsync` issues at most one DB query
- [x] `LoopEngine.RunAsync` startup path loads template + version + nodes + edges in a single eager-loaded query
- [x] Existing tests in `WorkItemManagerTests`, `LoopEngineTests` still pass

## Blocked by

None.
