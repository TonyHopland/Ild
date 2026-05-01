## What to build

`LoopEngine`, `WorkItemManager`, and `RecoveryManager` use the null-forgiving operator (`!`) in places where the nullability is reachable at runtime. Each `!` is a latent `NullReferenceException`.

Audit each file, classify each `!`:

1. Provably non-null at this point (EF tracking, just-set local) → leave a `// not-null because <reason>` comment.
2. Possibly null → replace with explicit `??`, throw, or early return.

## Acceptance criteria

- [x] Every `!` in the three files is either justified with a comment or removed
- [x] No new warnings under `<Nullable>enable</Nullable>`
- [x] All existing tests still pass

## Blocked by

None. Spun out of #024 part D.
