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

  // The wire value is the RemoteWorkItemStatus integer (WaitingForIld = 5,
  // Done = 6), not the TS enum declaration order, so the numeric mapping must
  // follow that ordering — an approve driving the item to Done emits 6.
  test("maps the wire-ordered WaitingForIld/Done values correctly", () => {
    expect(normalizeWorkItemStatus(5)).toBe(WorkItemStatus.WaitingForIld);
    expect(normalizeWorkItemStatus(6)).toBe(WorkItemStatus.Done);
  });

  test("falls back to Backlog for unknown or non-status values", () => {
    expect(normalizeWorkItemStatus(99)).toBe(WorkItemStatus.Backlog);
    expect(normalizeWorkItemStatus(null)).toBe(WorkItemStatus.Backlog);
    expect(normalizeWorkItemStatus(undefined)).toBe(WorkItemStatus.Backlog);
  });
});
