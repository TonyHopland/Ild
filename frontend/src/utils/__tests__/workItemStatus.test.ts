import { describe, expect, test } from "vite-plus/test";
import { normalizeWorkItemStatus } from "../workItemStatus";
import { WorkItemStatus } from "../../types";

describe("normalizeWorkItemStatus", () => {
  test("passes through string enum values from the REST API", () => {
    expect(normalizeWorkItemStatus("HumanFeedback")).toBe(WorkItemStatus.HumanFeedback);
    expect(normalizeWorkItemStatus("Running")).toBe(WorkItemStatus.Running);
  });

  test("maps numeric values from SignalR to the string enum", () => {
    expect(normalizeWorkItemStatus(0)).toBe(WorkItemStatus.Backlog);
    expect(normalizeWorkItemStatus(3)).toBe(WorkItemStatus.Running);
    expect(normalizeWorkItemStatus(4)).toBe(WorkItemStatus.HumanFeedback);
  });

  // Done (5) and WaitingForIld (6) are declared in a different order on the
  // server enum than in the TS enum, so the numeric mapping must follow the
  // server's values rather than the TS declaration order.
  test("maps the server-ordered Done/WaitingForIld values correctly", () => {
    expect(normalizeWorkItemStatus(5)).toBe(WorkItemStatus.Done);
    expect(normalizeWorkItemStatus(6)).toBe(WorkItemStatus.WaitingForIld);
  });

  test("falls back to Backlog for unknown or non-status values", () => {
    expect(normalizeWorkItemStatus(99)).toBe(WorkItemStatus.Backlog);
    expect(normalizeWorkItemStatus(null)).toBe(WorkItemStatus.Backlog);
    expect(normalizeWorkItemStatus(undefined)).toBe(WorkItemStatus.Backlog);
  });
});
