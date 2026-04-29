## What to build

`RecoveryManager` has no dedicated unit tests. The PRD specifies crash recovery as a first-class concern with three RecoveryPolicy paths: AutoResume (re-execute in-flight node), NeedsReview (move to HumanFeedback), and Cancel (cancel the run).

Add tests that verify each policy path, worktree health validation on recovery, and preservation of Human node input across restarts.

## Acceptance criteria

- [ ] Test: AutoResume policy re-executes the in-flight node
- [ ] Test: NeedsReview policy transitions WorkItem to HumanFeedback
- [ ] Test: Cancel policy cancels the LoopRun and transitions WorkItem appropriately
- [ ] Test: Worktree health validation on recovery (corrupted worktree detected)
- [ ] Test: Human node partial input preserved across recovery
- [ ] Tests use mocked dependencies (no real DB or filesystem)

## Blocked by

- Blocked by #6 (Fix RecoveryManager fire-and-forget — test the corrected behavior)
