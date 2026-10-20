import { LoopRun, LoopRunNodeStatus, LoopRunStatus, NodeType } from "../../types";

/**
 * When a run can be sent back to one of its nodes. One rule for the Runs tab
 * and the failed-node card on the Action tab, so they never disagree.
 */

/** A run still alive, running or parked: one that offers a retry at all. An ended run is not restarted. */
export function runOffersRetry(run: Pick<LoopRun, "status">): boolean {
  return run.status === LoopRunStatus.Running || run.status === LoopRunStatus.WaitingHuman;
}

/** A run that takes a retry now: parked, or running but paused, never mid-node. */
export function runTakesRetryNow(run: Pick<LoopRun, "status" | "isPaused">): boolean {
  return (
    run.status === LoopRunStatus.WaitingHuman ||
    (run.status === LoopRunStatus.Running && run.isPaused)
  );
}

/**
 * A node a run can go back to: not the one running now, and not Start or
 * Cleanup, which cannot meaningfully run again.
 */
export function nodeTakesRetry(
  status: LoopRunNodeStatus,
  nodeType: NodeType | string | null | undefined,
): boolean {
  return (
    status !== LoopRunNodeStatus.Running &&
    nodeType !== NodeType.Start &&
    nodeType !== NodeType.Cleanup
  );
}
