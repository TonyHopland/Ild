import { WorkItemStatus } from "../types";

// SignalR uses the default JSON protocol, which serializes the status enum as
// its numeric value rather than the PascalCase string the REST API returns.
// The number on the wire is the RemoteWorkItemStatus integer — the notifier
// casts it through `(WorkItemStatus)(int)` (SignalRWorkItemNotifier), which
// preserves the integer rather than remapping by name. Mirror that enum's
// ordering (WaitingForIld = 5, Done = 6) so rendering code can safely call
// helpers like `.toLowerCase()` on the status.
const NUMERIC_STATUS: Record<number, WorkItemStatus> = {
  0: WorkItemStatus.Backlog,
  1: WorkItemStatus.WorkQueue,
  2: WorkItemStatus.Ready,
  3: WorkItemStatus.Running,
  4: WorkItemStatus.HumanFeedback,
  5: WorkItemStatus.WaitingForIld,
  6: WorkItemStatus.Done,
};

export function normalizeWorkItemStatus(value: unknown): WorkItemStatus {
  if (typeof value === "string") return value as WorkItemStatus;
  if (typeof value === "number") return NUMERIC_STATUS[value] ?? WorkItemStatus.Backlog;
  return WorkItemStatus.Backlog;
}
