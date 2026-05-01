## What to build

Two related bugs in the retry logic in `LoopEngine.ExecuteNodeWithRetryAsync`:

**Bug A — `HasFailureEdge` check inside retry loop:** The `if (hasFailureEdge)` check is at the bottom of the retry `while(true)` loop. This means on the first failure it returns immediately without ever reaching `attempt > maxRetries`. The semantics are correct per the PRD ("error edge → follow immediately on failure; no error edge → auto-retry N times"), but the code structure is wrong: the check should be **before** the retry loop, not inside it.

**Bug B — Duplicate LoopRunNode records per retry:** Each retry creates a new `LoopRunNode` with a new GUID instead of updating the existing record. A node with `MaxRetries = 3` that fails all attempts produces 4 conflicting `LoopRunNode` rows.

**Decision logged:** "Error edge → immediate follow, no retry" is the intended design. `MaxRetries` only applies to nodes without a failure edge.

For Bug A: Move `HasFailureEdge` check **before** the retry loop. If a failure edge exists, skip retry logic entirely.
For Bug B: Update the existing `LoopRunNode` record on retry rather than creating a new one, incrementing `RetryCount` in place.

## Acceptance criteria

- [x] `HasFailureEdge` check moved **before** retry loop — nodes with failure edge skip retry logic entirely
- [x] Each node execution updates a single `LoopRunNode` record in place, incrementing `RetryCount`
- [x] No duplicate `LoopRunNode` rows for the same node in a LoopRun
- [x] LoopEngine tests cover both paths (failure edge immediate route, auto-retry exhaustion)

## Blocked by

None - can start immediately
