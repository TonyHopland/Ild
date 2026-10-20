import { describe, expect, test } from "vite-plus/test";
import { LoopRunNodeStatus, LoopRunStatus, NodeType } from "../../types";
import { nodeTakesRetry, runOffersRetry, runTakesRetryNow } from "./retry";

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

  test("Start, Cleanup and the node running now are never retried; any other node is", () => {
    expect(nodeTakesRetry(LoopRunNodeStatus.Failed, NodeType.Start)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Failed, NodeType.Cleanup)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Running, NodeType.AI)).toBe(false);
    expect(nodeTakesRetry(LoopRunNodeStatus.Failed, NodeType.AI)).toBe(true);
    expect(nodeTakesRetry(LoopRunNodeStatus.Succeeded, undefined)).toBe(true);
  });
});
