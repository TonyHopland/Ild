## What to build

`ILoopEngine.GetRunStatusAsync(Guid runId)` currently returns a `LoopRunStatus` and uses `LoopRunStatus.Failed` as a sentinel for "run not found", which is ambiguous (a real failure looks identical to a missing run). Callers can't distinguish.

## Acceptance criteria

- [x] `GetRunStatusAsync` returns `LoopRunStatus?` (or throws `KeyNotFoundException`) for missing runs
- [x] `LoopRunsController.GetById` returns `404 NotFound` when the run does not exist
- [x] At least one `LoopEngineTests` test covers the missing-run path
- [x] Existing tests still pass

## Blocked by

None. Spun out of #019.
