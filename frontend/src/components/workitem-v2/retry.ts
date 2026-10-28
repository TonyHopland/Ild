import {
  LoopRun,
  LoopRunNodeStatus,
  LoopRunStatus,
  NodeType,
  WorkItem,
  WorkItemStatus,
} from "../../types";

// When a run can be sent back to one of its nodes. One place for the rules the
// Runs tab and the failed-node card on the Action tab share, so they never
// disagree, and for the one only the card applies: a run that ended on a failed
// node, which the retry reopens.

/** Why a run waits on a person after a node failed with nowhere to go. */
export const NODE_FAILED = "Node Failed";

/** A run still alive, running or parked: one that offers a retry at all. An ended run is not restarted. */
export function runOffersRetry(run: Pick<LoopRun, "status">): boolean {
  return run.status === LoopRunStatus.Running || run.status === LoopRunStatus.WaitingHuman;
}

/**
 * A run a retry reopens, as the server decides: one that ended Failed on a
 * failed node ("Node Failed") before such a failure parked the run instead,
 * while it is still its work item's current run and the item waits on a
 * person. The run's reason is read off the item, which carries its current
 * run's.
 */
export function runReopensOnRetry(
  run: Pick<LoopRun, "id" | "status">,
  workItem: Pick<WorkItem, "status" | "humanFeedbackReason" | "currentLoopRunId">,
): boolean {
  return (
    run.status === LoopRunStatus.Failed &&
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason === NODE_FAILED &&
    workItem.currentLoopRunId === run.id
  );
}

/** A run that takes a retry now: parked, or running but paused, never mid-node. */
export function runTakesRetryNow(run: Pick<LoopRun, "status" | "isPaused">): boolean {
  return (
    run.status === LoopRunStatus.WaitingHuman ||
    (run.status === LoopRunStatus.Running && run.isPaused)
  );
}

/**
 * A node a run can go back to: not the one running now, and not a Start or
 * Cleanup that did not fail, which cannot meaningfully run again. A failed
 * node of any type can, once its cause is fixed.
 */
export function nodeTakesRetry(
  status: LoopRunNodeStatus,
  nodeType: NodeType | string | null | undefined,
): boolean {
  return (
    status !== LoopRunNodeStatus.Running &&
    (status === LoopRunNodeStatus.Failed ||
      (nodeType !== NodeType.Start && nodeType !== NodeType.Cleanup))
  );
}
