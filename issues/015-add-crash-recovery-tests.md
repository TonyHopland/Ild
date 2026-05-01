## What to build

`RecoveryManager` has no dedicated unit tests. The PRD specifies crash recovery as a first-class concern with three RecoveryPolicy paths: AutoResume (re-execute in-flight node), NeedsReview (move to HumanFeedback), and Cancel (cancel the run).

Add tests that verify each policy path, worktree health validation on recovery, and preservation of Human node input across restarts.

## Acceptance criteria

- [x] Test: AutoResume policy re-executes the in-flight node
- [x] Test: NeedsReview policy transitions WorkItem to HumanFeedback
- [x] Test: Cancel policy cancels the LoopRun and transitions WorkItem appropriately
- [x] Test: Worktree health validation on recovery (corrupted worktree detected)
- [x] Test: Human node partial input preserved across recovery
- [x] Tests use mocked dependencies (no real DB or filesystem)

## Blocked by

- Blocked by #6 (Fix RecoveryManager fire-and-forget — test the corrected behavior)
