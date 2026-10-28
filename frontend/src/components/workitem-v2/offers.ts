import { LoopRun, LoopRunStatus, WorkItem, WorkItemStatus } from "../../types";

// Whether the footer offers to start a Ready item early, and whether a pull
// request asks to be merged by hand. Plain rules the modal and the action
// thread share, kept out of the components.

/** Why an item waits on a person to merge its pull request. */
export const PR_AWAITING_MERGE = "PR Awaiting Merge";

/** The repository limit holding a Ready item back, or null when it is not held. */
export function repositoryHoldOf(
  workItem: Pick<WorkItem, "status" | "repositoryParallelismHold">,
): WorkItem["repositoryParallelismHold"] {
  return workItem.status === WorkItemStatus.Ready
    ? (workItem.repositoryParallelismHold ?? null)
    : null;
}

/**
 * The start the footer offers a Ready item: "anyway" past a repository limit
 * that holds it, "now" when only a paused scheduler does, otherwise none.
 */
export function startOfferOf(
  workItem: Pick<WorkItem, "status" | "repositoryParallelismHold">,
  schedulerPaused: boolean | undefined,
): "anyway" | "now" | null {
  if (repositoryHoldOf(workItem)) return "anyway";
  return workItem.status === WorkItemStatus.Ready && schedulerPaused === true ? "now" : null;
}

/**
 * ILD tried to merge the AutoMerge pull request and could not, so a person is
 * asked to. Only while the run still waits on that merge: a pull request closed
 * since, or a run that has moved on, needs no one to merge it.
 */
export function asksForManualMerge(
  run: Pick<LoopRun, "status" | "prAutoMerge" | "prSnapshot">,
): boolean {
  return (
    run.prAutoMerge === "IldFailed" &&
    run.status === LoopRunStatus.WaitingHuman &&
    run.prSnapshot?.state !== "closed"
  );
}
