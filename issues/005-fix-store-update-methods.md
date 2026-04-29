## What to build

All `Update*Async` methods in the store implementations call `_db.SaveChangesAsync()` without first attaching the entity or marking it as `Modified`. This only works if the entity is already tracked by the current `DbContext`. In scoped DI (each request gets a new context), updates silently do nothing.

Affected stores: `AuthStore`, `LoopRunStore`, `LoopTemplateStore`, `ProviderStore`, `WorkItemStore`, `EventLogStore`.

Fix by calling `_db.Entry(entity).State = EntityState.Modified` (or `_db.Set<T>().Update(entity)`) before `SaveChangesAsync()`.

## Acceptance criteria

- [ ] Every `Update*Async` method attaches the entity or marks it as `Modified` before saving
- [ ] Updates work correctly when entity was loaded in a different `DbContext` instance
- [ ] Add tests that verify cross-context updates (load in one scope, update in another)
- [ ] All existing tests still pass

## Blocked by

None - can start immediately
