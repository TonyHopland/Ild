import { WorkItem, WorkItemStatus } from "../types";

// "Preview together" composes several work items into one integration worktree
// and previews them as a set. An item can only join that set if it has an
// *active* run whose branch can be merged — the backend resolves each member
// through its current run (Running / awaiting human), which excludes finished
// (Done) runs just as the single-item preview does. Backlog/queued items have
// produced no run at all.
//
// `branchName` is the authoritative signal (it is only populated from the
// current run), but the board list payload does not always include it, so we
// fall back to the statuses that imply a live, composable run.
export function isPreviewEligible(workItem: WorkItem): boolean {
  if (workItem.branchName) return true;
  return (
    workItem.status === WorkItemStatus.Running || workItem.status === WorkItemStatus.HumanFeedback
  );
}

// Short, human-readable reason shown on a dimmed card in selection mode so the
// eligibility rule teaches itself rather than failing silently. Returns null
// for eligible items.
export function ineligibleReason(workItem: WorkItem): string | null {
  if (isPreviewEligible(workItem)) return null;
  if (workItem.status === WorkItemStatus.Done)
    return "Run finished — no active worktree to preview";
  return "No run yet — nothing to preview";
}
