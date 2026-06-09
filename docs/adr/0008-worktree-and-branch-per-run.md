# ADR-0008: One worktree and branch per run, reclaimed by retention

Each run gets its own git branch (`ild/wi-<workItemId>-run-<runId>`) and therefore its own worktree, instead of a branch keyed by work item. We did this because a shared per-work-item branch let a prior run's commits leak into the next run (the Start node rebases the branch onto `origin/<default>`, replaying any commits still on it) — and because git forbids two worktrees checked out on the same branch, keeping a finished run's worktree around for inspection is only possible if its branch is unique. A finished run's worktree and branch are deliberately **kept** (the Cleanup node no longer deletes them) so an old run stays inspectable; disk is reclaimed later by the `WorktreeRetentionSweeper`. This reinforces the clean-base invariant of [ADR-0006](./0006-run-isolation-clean-origin-base.md) by making cross-run isolation structural rather than dependent on reset/rebase succeeding.

## Consequences

- **One PR per run.** Because the branch is per-run, re-running a work item pushes a new branch and opens a new PR rather than updating an existing one.
- **Retention deletes the whole run.** Once a run has been terminal (Completed/Failed/Cancelled) longer than the configurable `run.retentionDays` window (default 30; `0` disables), the sweeper deletes its worktree, local branch, and run row (nodes, edge traversals, and event logs cascade). The window is read from settings on every pass, so changes take effect without a restart.
- **Pinning.** A run marked `Retain` is never reclaimed until the mark is cleared — an escape hatch for runs worth keeping indefinitely.
- **Safety rails.** The sweeper never reclaims the run a still-active work item points at (kept until the work item is `Done` or a newer run supersedes it), and never touches remote branches or PRs — only local state is reclaimed.
- **Worktrees must live on persistent storage** (`App:WorktreesPath`, under the data root) rather than `/tmp`, or "inspect an old run" breaks across reboots.
