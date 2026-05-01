## What to build

Several services use async/await and exception handling in ways that hide failures or break the error contract.

**A — `AIProviderService` returns errors as strings:** [AIProviderService.cs](ILD.Core/Services/Implementations/AIProviderService.cs#L42-L50) catches all exceptions and returns `$"[ai-error] {ex.Message}"` as a successful string result. Callers cannot distinguish AI output from a failure. Throw or return `Result<string>`.

**B — `RepositoryManager` mixes `ContinueWith` with async:** [RepositoryManager.cs](ILD.Core/Services/Implementations/RepositoryManager.cs#L100) uses `File.ReadAllTextAsync(full).ContinueWith(t => (string?)t.Result)`. `t.Result` rethrows wrapped exceptions and skips synchronization context. Replace with a plain `await File.ReadAllTextAsync(full)`.

**C — Bare `catch {}` on process kill:** [RepositoryManager.cs](ILD.Core/Services/Implementations/RepositoryManager.cs#L138-L142) silently swallows every exception when killing a git process. Catch `InvalidOperationException` only (process already exited) and log the rest.

## Acceptance criteria

- [x] `IAIProviderService.CompleteAsync` either throws or returns a typed result on failure; callers updated
- [x] `RepositoryManager.ReadFileAsync` (or equivalent) uses straight `await`
- [x] Process-kill catch narrows to expected exception type and logs unexpected failures
- [x] Tests cover at least one failure path per case (AI error throws, repo file read failure surfaces, kill of already-exited process is silent)

## Blocked by

None.
