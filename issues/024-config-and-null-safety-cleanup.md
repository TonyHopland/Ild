## What to build

Configuration and null-handling are inconsistent across the backend.

**A — Hardcoded paths:** [Program.cs](ILD.Api/Program.cs#L26-L27) hardcodes `"data"`, the worktrees subdir, and the SQLite filename. Move these to `IConfiguration` (`Storage:DataRoot`, `Storage:WorktreesSubdir`, `Storage:DatabaseFile`) with the current values as defaults.

**B — `HttpClient` registration:** Verify `AIProviderService` (and any other consumer) is registered via `IHttpClientFactory` / `AddHttpClient<>`, not as a singleton holding a long-lived `HttpClient` (DNS staleness, socket exhaustion). Check [ServiceCollectionExtensions.cs](ILD.Api/Configuration/ServiceCollectionExtensions.cs).

**C — Logging level switch is wired end-to-end:** Confirm `LoggingController` actually mutates the Serilog `LoggingLevelSwitch` registered in `Program.cs` and that the change is observable in subsequent log lines. Add a test or remove the controller if it's dead code.

**D — Null-forgiving (`!`) audit:** Multiple services use `!` to suppress nullable warnings (e.g. on `FindAsync` results). Replace with explicit null checks that throw a meaningful exception or return early. Focus on `LoopEngine`, `WorkItemManager`, and `RecoveryManager`.

## Acceptance criteria

- [x] No hardcoded `data/...` paths in `Program.cs`; configuration drives storage locations
- [x] All `HttpClient` consumers use `IHttpClientFactory`
- [x] `LoggingController` has a test or is deleted
- [x] Audit pass over `LoopEngine` / `WorkItemManager` / `RecoveryManager` removes gratuitous `!` operators — _done in #034_

## Blocked by

None.
