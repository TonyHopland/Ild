import { describe, expect, test } from "vite-plus/test";
import { isPreviewEligible, ineligibleReason } from "./previewSelection";
import { WorkItem, WorkItemStatus } from "../types";

function workItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "1",
    title: "Item",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: "Medium",
    tags: [],
    repositoryId: "repo",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2026-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  } as WorkItem;
}

describe("isPreviewEligible", () => {
  test("is true when the item has a branch, regardless of status", () => {
    expect(isPreviewEligible(workItem({ branchName: "ild/wi-1-run-abc" }))).toBe(true);
  });

  test("is true for active run-bearing statuses without an explicit branch", () => {
    for (const status of [WorkItemStatus.Running, WorkItemStatus.HumanFeedback]) {
      expect(isPreviewEligible(workItem({ status }))).toBe(true);
    }
  });

  test("is false for Done without a branch — its run is no longer composable", () => {
    // A finished run is excluded by the backend's current-run resolution, so the
    // UI must not advertise it as previewable.
    expect(isPreviewEligible(workItem({ status: WorkItemStatus.Done }))).toBe(false);
  });

  test("is false for pre-run statuses with no branch", () => {
    for (const status of [
      WorkItemStatus.Backlog,
      WorkItemStatus.WorkQueue,
      WorkItemStatus.Ready,
      WorkItemStatus.WaitingForIld,
    ]) {
      expect(isPreviewEligible(workItem({ status }))).toBe(false);
    }
  });
});

describe("ineligibleReason", () => {
  test("is null for an eligible item", () => {
    expect(ineligibleReason(workItem({ status: WorkItemStatus.Running }))).toBeNull();
  });

  test("explains why a pre-run item cannot be previewed", () => {
    expect(ineligibleReason(workItem({ status: WorkItemStatus.Ready }))).toMatch(/no run yet/i);
  });

  test("explains that a Done item's run has finished", () => {
    expect(ineligibleReason(workItem({ status: WorkItemStatus.Done }))).toMatch(/finished/i);
  });
});
