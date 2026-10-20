import { describe, expect, it } from "vite-plus/test";
import { LoopRunNodeStatus, LoopRunStatus, WorkItemStatus } from "../../types";
import {
  feedbackReasonLabel,
  feedbackReasonTone,
  nodeStatusLabel,
  reviewStateLabel,
  runStatusLabel,
  workItemStatusLabel,
  workItemStatusTone,
} from "../labels";

describe("labels", () => {
  it("names every work item status in words, never the enum name", () => {
    for (const status of Object.values(WorkItemStatus)) {
      expect(workItemStatusLabel(status)).not.toMatch(/[a-z][A-Z]/);
    }
    expect(workItemStatusLabel(WorkItemStatus.HumanFeedback)).toBe("Needs you");
    expect(workItemStatusLabel(WorkItemStatus.WaitingForIld)).toBe("Waiting for ILD");
    expect(workItemStatusTone(WorkItemStatus.HumanFeedback)).toBe("attention");
  });

  it("names run and node statuses in words", () => {
    expect(runStatusLabel(LoopRunStatus.WaitingHuman)).toBe("Waiting for you");
    expect(nodeStatusLabel(LoopRunNodeStatus.WaitingHuman)).toBe("Waiting for you");
    for (const status of Object.values(LoopRunStatus)) {
      expect(runStatusLabel(status)).not.toMatch(/[a-z][A-Z]/);
    }
  });

  it("gives each feedback reason its words and tone, and shows an unknown one as written", () => {
    expect(feedbackReasonLabel("Node Failed")).toBe("Node failed");
    expect(feedbackReasonTone("Node Failed")).toBe("danger");
    expect(feedbackReasonTone("PR Awaiting Merge")).toBe("info");
    expect(feedbackReasonLabel("Something New")).toBe("Something New");
  });

  it("turns a forge review state into words", () => {
    expect(reviewStateLabel("CHANGES_REQUESTED")).toBe("Changes requested");
    expect(reviewStateLabel("APPROVED")).toBe("Approved");
  });
});
