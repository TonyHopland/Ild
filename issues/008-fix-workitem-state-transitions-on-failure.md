## What to build

When `LoopEngine.RunAsync` fails early (e.g., no Start node found at line 126, or safety net exceeded), it calls `FailRunAsync` which marks the LoopRun as failed but does **not** transition the associated WorkItem out of `Running` status. The WorkItem remains stuck in `Running` indefinitely.

Ensure all failure paths in the engine call `TransitionWorkItemAsync` to move the WorkItem to `HumanFeedback` (or `Done` for terminal failures) before marking the LoopRun as failed.

Also fix `TransitionWorkItemAsync` silent no-op on null WorkItem — log a warning instead of silently returning.

## Acceptance criteria

- [ ] "No Start node" failure path transitions WorkItem out of Running
- [ ] Safety net exhaustion failure path transitions WorkItem out of Running
- [ ] Cancellation failure path transitions WorkItem out of Running
- [ ] `TransitionWorkItemAsync` logs a warning when WorkItem is not found (instead of silent return)
- [ ] Add LoopEngine test for early failure WorkItem state transition

## Blocked by

- Blocked by #2 (Fix LoopEngine memory leak — same file, `FailRunAsync` refactored)
