import { WorkItemStatus } from "../types";

// SignalR uses the default JSON protocol, which serializes the server-side
// WorkItemStatus enum as its numeric value rather than the PascalCase string
// the REST API returns. Normalize both shapes to the string enum so rendering
// code can safely call helpers like `.toLowerCase()` on the status.
const NUMERIC_STATUS: Record<number, WorkItemStatus> = {
  0: WorkItemStatus.Backlog,
  1: WorkItemStatus.WorkQueue,
  2: WorkItemStatus.Ready,
  3: WorkItemStatus.Running,
  4: WorkItemStatus.HumanFeedback,
  5: WorkItemStatus.Done,
  6: WorkItemStatus.WaitingForIld,
};

export function normalizeWorkItemStatus(value: unknown): WorkItemStatus {
  if (typeof value === "string") return value as WorkItemStatus;
  if (typeof value === "number") return NUMERIC_STATUS[value] ?? WorkItemStatus.Backlog;
  return WorkItemStatus.Backlog;
}
