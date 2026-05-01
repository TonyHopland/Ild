## What to build

Several data model issues need fixing:

**A — `RecoveryPolicy` stored as `string` not `enum`:** `LoopTemplate.RecoveryPolicy` and `LoopRun.RecoveryPolicy` are `string` fields despite a `RecoveryPolicy` enum existing. Change to strongly-typed enum.

**B — `NodeType` enum has non-sequential values:** `PR = 5` and `Cleanup = 4`. Fix to `PR = 4, Cleanup = 10`. Cleanup uses 10 (not 5) to leave room for new node types between PR and Cleanup without shifting the terminal value.

**C — `CleanupNodeExecutor` doesn't clear `WorktreePath`:** After destroying the worktree, `WorkItem.WorktreePath` is not set to null. A restarted WorkItem sees a stale path that no longer exists on disk.

**D — `UpdatedAt` properties never auto-set:** Multiple entities have `UpdatedAt` that is never populated. Add a `SaveChanges` override in `AppDbContext` to auto-set `UpdatedAt` on modified entities.

**E — `ProviderStore` handles `LoopTemplate` CRUD:** Violates single responsibility. Move `LoopTemplate` operations to `ILoopTemplateStore` / `LoopTemplateStore`.

## Acceptance criteria

- [x] `LoopTemplate.RecoveryPolicy` and `LoopRun.RecoveryPolicy` are typed as `RecoveryPolicy` enum
- [x] `NodeType` enum values are sequential (Start=0, Cmd=1, AI=2, Human=3, PR=4, Cleanup=10)
- [x] `CleanupNodeExecutor` sets `WorkItem.WorktreePath = null` after destroying worktree
- [x] `UpdatedAt` is auto-set on save via `AppDbContext.SaveChanges` override
- [x] `LoopTemplate` CRUD moved from `ProviderStore` to `LoopTemplateStore`
- [x] EF migration generated and applied for enum type change
- [x] All existing tests still pass

## Blocked by

None - can start immediately
