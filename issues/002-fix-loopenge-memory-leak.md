## What to build

`LoopEngine._runs` is a `ConcurrentDictionary<Guid, RunControl>` that grows unbounded. Entries are added via `GetOrAdd` but never removed when a LoopRun completes, fails, or is cancelled. Each `RunControl` holds a `CancellationTokenSource` (unmanaged resource) and a `Task` reference, causing a memory leak over time.

Remove entries from `_runs` in `CompleteRunAsync`, `FailRunAsync`, and `CancelRunInternalAsync`. Implement `IDisposable` on `RunControl` or dispose `Cts` when the run ends.

## Acceptance criteria

- [ ] `RunControl` entries are removed from `_runs` when run reaches terminal state (Completed, Failed, Cancelled)
- [ ] `CancellationTokenSource` is disposed when run ends
- [ ] `RunControl` implements `IDisposable` or disposes `Cts` explicitly
- [ ] No memory growth observable after many loop runs complete
- [ ] Existing LoopEngine tests still pass

## Blocked by

None - can start immediately
