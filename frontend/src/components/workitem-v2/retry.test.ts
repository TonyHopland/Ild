import { describe, expect, test } from "vite-plus/test";
import { LoopRunNodeStatus, LoopRunStatus, NodeType, WorkItemStatus } from "../../types";
import {
  nodeRetryIsPrimary,
  nodeTakesRetry,
  runOffersRetry,
  runReopensOnRetry,
  runTakesRetryNow,
} from "./retry";

describe("when a run can go back to a node", () => {
  test.each([
    [LoopRunStatus.WaitingHuman, false, true, true],
    [LoopRunStatus.Running, true, true, true],
    [LoopRunStatus.Running, false, true, false],
    [LoopRunStatus.Completed, false, false, false],
    [LoopRunStatus.Failed, false, false, false],
  ])(
    "a %s run (paused: %s) offers a retry: %s, takes one now: %s",
    (status, isPaused, offers, now) => {
      expect(runOffersRetry({ status })).toBe(offers);
      expect(runTakesRetryNow({ status, isPaused })).toBe(now);
    },
  );

  const waiting = {
    status: WorkItemStatus.HumanFeedback,
    humanFeedbackReason: "Node Failed",
    currentLoopRunId: "run-1",
  };
  test.each([
    ["a run that ended on its failed node", LoopRunStatus.Failed, waiting, true],
    ["a completed run", LoopRunStatus.Completed, waiting, false],
    ["a cancelled run", LoopRunStatus.Cancelled, waiting, false],
    [
      "a run that failed for another reason",
      LoopRunStatus.Failed,
      { ...waiting, humanFeedbackReason: "Run Crashed" },
      false,
    ],
    [
      "a run its item no longer waits on",
      LoopRunStatus.Failed,
      { ...waiting, status: WorkItemStatus.Backlog },
      false,
    ],
    [
      "a run that is not its item's current run",
      LoopRunStatus.Failed,
      { ...waiting, currentLoopRunId: "run-2" },
      false,
    ],
  ])("whether a retry reopens %s", (_, status, workItem, reopens) => {
    expect(runReopensOnRetry({ id: "run-1", status }, workItem)).toBe(reopens);
  });

  test.each(Object.values(NodeType))("a failed %s node is retried", (nodeType) => {
    expect(nodeTakesRetry(LoopRunNodeStatus.Failed, nodeType)).toBe(true);
  });

  test("a Start or Cleanup that did not fail and the node running now are never retried; any other node is", () => {
    expect(nodeTakesRetry(LoopRunNodeStatus.Succeeded, NodeType.Start)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Succeeded, NodeType.Cleanup)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Running, NodeType.Start)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Running, NodeType.AI)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Succeeded, NodeType.AI)).toBe(true);
    expect(nodeTakesRetry(LoopRunNodeStatus.Succeeded, undefined)).toBe(true);
  });
});

describe("which node retry stands out", () => {
  test.each([
    [LoopRunNodeStatus.Failed, true],
    [LoopRunNodeStatus.Succeeded, false],
    [LoopRunNodeStatus.Interrupted, false],
    [LoopRunNodeStatus.Skipped, false],
    [LoopRunNodeStatus.Pending, false],
  ])("a %s node: primary Retry is %s", (status, primary) => {
    expect(nodeRetryIsPrimary(status)).toBe(primary);
  });
});
