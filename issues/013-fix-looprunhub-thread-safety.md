## What to build

`LoopRunHub._runGroups` is a `ConcurrentDictionary<Guid, HashSet<string>>`. The outer dictionary is thread-safe, but the inner `HashSet<string>` is not. Concurrent `Add()` and `Remove()` calls from multiple hub connections can cause `InvalidOperationException` or data corruption.

Replace inner `HashSet<string>` with a thread-safe alternative (e.g., `ConcurrentBag<string>` or a `lock`-protected `HashSet`), or use SignalR's built-in group management (`Groups.AddToGroupAsync` / `RemoveFromGroupAsync`) instead of manual tracking.

## Acceptance criteria

- [x] Connection tracking per run group is thread-safe under concurrent access
- [x] Prefer SignalR's built-in `Groups.AddToGroupAsync` / `RemoveFromGroupAsync` over manual dictionary tracking
- [x] No `InvalidOperationException` under load with many concurrent connections
- [x] Existing hub functionality (joining/leaving run groups) works correctly

## Blocked by

None - can start immediately
