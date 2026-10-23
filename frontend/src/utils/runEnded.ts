import { LoopRunStatus } from "../types";

/**
 * Whether a run has ended: Completed or Cancelled, as the server counts it. A
 * Failed run has not — it can still be retried — so it keeps its worktree.
 */
export const runHasEnded = (status: LoopRunStatus) =>
  status === LoopRunStatus.Completed || status === LoopRunStatus.Cancelled;
