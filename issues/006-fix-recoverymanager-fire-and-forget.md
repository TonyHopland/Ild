## What to build

`RecoveryManager.RecoverAsync` starts recovered LoopRuns with a fire-and-forget `Task.Run()`. Unhandled exceptions are silently swallowed, and the recovered run cannot be cancelled through the normal `RunControl.Cts` mechanism since it's started with `CancellationToken.None`.

Track recovered run tasks properly. Either await them with proper error handling, or store the task in the `RunControl` so it can be observed and cancelled.

## Acceptance criteria

- [ ] Recovered loop runs are tracked and their exceptions are logged (not swallowed)
- [ ] Recovered runs use the `RunControl.Cts` token for cancellation
- [ ] `RunControl.Task` is set so callers can observe completion
- [ ] Add unit tests for RecoveryManager covering all three RecoveryPolicy paths (AutoResume, NeedsReview, Cancel)

## Blocked by

- Blocked by #2 (Fix LoopEngine memory leak — same `RunControl` lifecycle changes)
