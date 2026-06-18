import { WorkItem, WorkItemStatus } from "../types";

// "Preview together" composes several work items into one integration worktree
// and previews them as a set. An item can only join that set if it already has
// a branch/worktree to merge — items still in the backlog or queued have
// produced no run yet, so there is nothing to compose.
//
// `branchName` is the authoritative signal, but the board list payload does not
// always include it, so we fall back to the statuses that can only be reached
// once a run (and therefore a branch) exists.
export function isPreviewEligible(workItem: WorkItem): boolean {
  if (workItem.branchName) return true;
  return (
    workItem.status === WorkItemStatus.Running ||
    workItem.status === WorkItemStatus.HumanFeedback ||
    workItem.status === WorkItemStatus.Done
  );
}

// Short, human-readable reason shown on a dimmed card in selection mode so the
// eligibility rule teaches itself rather than failing silently. Returns null
// for eligible items.
export function ineligibleReason(workItem: WorkItem): string | null {
  if (isPreviewEligible(workItem)) return null;
  return "No run yet — nothing to preview";
}
