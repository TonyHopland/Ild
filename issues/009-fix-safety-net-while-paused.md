## What to build

Two related issues in the LoopEngine pause/safety-net logic:

**A — Safety net doesn't fire while paused:** The wall-clock deadline check (`DateTime.UtcNow > deadline`) is inside the main execution loop body, which is skipped while `control.IsPaused` is true. A run paused indefinitely will never hit the safety net.

**B — Pause loop ignores cancellation token:** `Task.Delay(50, CancellationToken.None)` cannot be cancelled. If cancellation is requested during the 50ms delay, the system waits the full delay before noticing.

Add a wall-clock check inside the pause loop. Pass `ct` to `Task.Delay` so cancellation is responsive.

## Acceptance criteria

- [x] Wall-clock deadline is checked inside the pause loop (not just the main loop)
- [x] `Task.Delay` in pause loop uses the run's `CancellationToken` (not `CancellationToken.None`)
- [x] A paused run that exceeds the wall-clock deadline is cancelled and fails gracefully
- [x] Cancellation during pause is responsive (no 50ms delay blocking shutdown)

## Blocked by

None - can start immediately
