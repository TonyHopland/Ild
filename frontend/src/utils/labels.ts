/**
 * What the UI calls each status, reason and review state. The enums carry the
 * server's names (HumanFeedback, WaitingHuman, CHANGES_REQUESTED); nothing on
 * screen shows those directly, it goes through these maps instead.
 */
import {
  LoopRunNodeStatus,
  LoopRunStatus,
  type RepositoryParallelismHold,
  WorkItemStatus,
} from "../types";

/** The tone a pill takes: the four status colours, the accent, or muted grey. */
export type Tone = "attention" | "danger" | "info" | "success" | "accent" | "muted";

const WORK_ITEM_STATUS: Record<WorkItemStatus, { label: string; tone: Tone }> = {
  [WorkItemStatus.Backlog]: { label: "Backlog", tone: "muted" },
  [WorkItemStatus.WorkQueue]: { label: "Work queue", tone: "muted" },
  [WorkItemStatus.Ready]: { label: "Ready", tone: "info" },
  [WorkItemStatus.Running]: { label: "Running", tone: "accent" },
  [WorkItemStatus.HumanFeedback]: { label: "Needs you", tone: "attention" },
  [WorkItemStatus.WaitingForIld]: { label: "Waiting for ILD", tone: "info" },
  [WorkItemStatus.Done]: { label: "Done", tone: "success" },
};

/** The statuses in the order a work item moves through them: the board's columns, left to right. */
export const WORK_ITEM_STATUS_ORDER: readonly WorkItemStatus[] = [
  WorkItemStatus.Backlog,
  WorkItemStatus.WorkQueue,
  WorkItemStatus.Ready,
  WorkItemStatus.Running,
  WorkItemStatus.HumanFeedback,
  WorkItemStatus.WaitingForIld,
  WorkItemStatus.Done,
];

export function workItemStatusLabel(status: WorkItemStatus | string): string {
  return WORK_ITEM_STATUS[status as WorkItemStatus]?.label ?? splitWords(status);
}

export function workItemStatusTone(status: WorkItemStatus | string): Tone {
  return WORK_ITEM_STATUS[status as WorkItemStatus]?.tone ?? "muted";
}

/**
 * Why an item waits in Needs you. The reasons are a fixed set the engine writes
 * (HumanFeedbackReasons); an unknown one is shown as written.
 */
const FEEDBACK_REASON: Record<string, { label: string; tone: Tone }> = {
  "Human Input Needed": { label: "Human input needed", tone: "attention" },
  "Node Failed": { label: "Node failed", tone: "danger" },
  "PR Awaiting Merge": { label: "PR awaiting merge", tone: "info" },
  "Rebase Conflict": { label: "Rebase conflict", tone: "attention" },
};

export function feedbackReasonLabel(reason: string): string {
  return FEEDBACK_REASON[reason]?.label ?? reason;
}

export function feedbackReasonTone(reason: string): Tone {
  return FEEDBACK_REASON[reason]?.tone ?? "attention";
}

const RUN_STATUS: Record<LoopRunStatus, { label: string; tone: Tone }> = {
  [LoopRunStatus.Running]: { label: "Running", tone: "accent" },
  [LoopRunStatus.Completed]: { label: "Completed", tone: "success" },
  [LoopRunStatus.Failed]: { label: "Failed", tone: "danger" },
  [LoopRunStatus.Cancelled]: { label: "Cancelled", tone: "muted" },
  [LoopRunStatus.WaitingHuman]: { label: "Waiting for you", tone: "attention" },
};

export function runStatusLabel(status: LoopRunStatus | string): string {
  return RUN_STATUS[status as LoopRunStatus]?.label ?? splitWords(status);
}

export function runStatusTone(status: LoopRunStatus | string): Tone {
  return RUN_STATUS[status as LoopRunStatus]?.tone ?? "muted";
}

const NODE_STATUS: Record<LoopRunNodeStatus, string> = {
  [LoopRunNodeStatus.Pending]: "Pending",
  [LoopRunNodeStatus.Running]: "Running",
  [LoopRunNodeStatus.Succeeded]: "Succeeded",
  [LoopRunNodeStatus.Failed]: "Failed",
  [LoopRunNodeStatus.Skipped]: "Skipped",
  [LoopRunNodeStatus.WaitingHuman]: "Waiting for you",
  [LoopRunNodeStatus.Interrupted]: "Interrupted",
};

export function nodeStatusLabel(status: LoopRunNodeStatus | string): string {
  return NODE_STATUS[status as LoopRunNodeStatus] ?? splitWords(status);
}

/**
 * A pull request review's state as the forge reports it (CHANGES_REQUESTED,
 * APPROVED, …), in words.
 */
export function reviewStateLabel(state: string): string {
  if (!state) return state;
  const words = state.toLowerCase().replace(/_/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Why a Ready item waits on its repository, on its card and in its dialog alike. */
export function repositoryHoldLabel(hold: RepositoryParallelismHold): string {
  return `Waiting: repository parallelism limit (${hold.inProgress}/${hold.limit} in progress)`;
}

/** The class a pill of that tone takes. */
export function pillClass(tone: Tone): string {
  return `pill pill-${tone}`;
}

/** "WaitingForIld" → "Waiting for ild"; a fallback for values the maps don't know yet. */
function splitWords(value: string): string {
  const words = value.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
