import { describe, expect, test } from "vite-plus/test";
import { LoopRunStatus, WorkItemStatus } from "../../types";
import type { LoopRun, WorkItem } from "../../types";
import { asksForManualMerge, offersMerge, repositoryHoldOf, startOfferOf } from "./offers";

const hold = { inProgress: 1, limit: 1 };

describe("repositoryHoldOf: the repository limit holding a Ready item back", () => {
  test("a Ready item carries the hold its repository's limit puts on it", () => {
    expect(
      repositoryHoldOf({ status: WorkItemStatus.Ready, repositoryParallelismHold: hold }),
    ).toEqual(hold);
  });

  test.each<[string, Pick<WorkItem, "status" | "repositoryParallelismHold">]>([
    ["Ready with a null hold", { status: WorkItemStatus.Ready, repositoryParallelismHold: null }],
    ["Ready with no hold", { status: WorkItemStatus.Ready }],
    ["Backlog", { status: WorkItemStatus.Backlog, repositoryParallelismHold: hold }],
    ["WorkQueue", { status: WorkItemStatus.WorkQueue, repositoryParallelismHold: hold }],
    ["Running", { status: WorkItemStatus.Running, repositoryParallelismHold: hold }],
    ["HumanFeedback", { status: WorkItemStatus.HumanFeedback, repositoryParallelismHold: hold }],
  ])("%s has no hold", (_, item) => {
    expect(repositoryHoldOf(item)).toBeNull();
  });
});

describe("startOfferOf: which start the footer offers", () => {
  test.each<
    [
      string,
      Pick<WorkItem, "status" | "repositoryParallelismHold">,
      boolean | undefined,
      string | null,
    ]
  >([
    [
      "a held Ready item, scheduler running",
      { status: WorkItemStatus.Ready, repositoryParallelismHold: hold },
      false,
      "anyway",
    ],
    [
      "a held Ready item, scheduler paused",
      { status: WorkItemStatus.Ready, repositoryParallelismHold: hold },
      true,
      "anyway",
    ],
    [
      "a held Ready item, scheduler unknown",
      { status: WorkItemStatus.Ready, repositoryParallelismHold: hold },
      undefined,
      "anyway",
    ],
    ["an unheld Ready item, scheduler paused", { status: WorkItemStatus.Ready }, true, "now"],
    ["an unheld Ready item, scheduler running", { status: WorkItemStatus.Ready }, false, null],
    ["an unheld Ready item, scheduler unknown", { status: WorkItemStatus.Ready }, undefined, null],
    ["a Backlog item, scheduler paused", { status: WorkItemStatus.Backlog }, true, null],
    ["a WorkQueue item, scheduler paused", { status: WorkItemStatus.WorkQueue }, true, null],
    ["a Running item, scheduler paused", { status: WorkItemStatus.Running }, true, null],
    [
      "a HumanFeedback item, scheduler paused",
      { status: WorkItemStatus.HumanFeedback },
      true,
      null,
    ],
    ["a Done item, scheduler paused", { status: WorkItemStatus.Done }, true, null],
  ])("%s offers %s", (_, item, schedulerPaused, offer) => {
    expect(startOfferOf(item, schedulerPaused)).toBe(offer);
  });
});

describe("asksForManualMerge: ILD could not merge the AutoMerge pull request", () => {
  type MergeRun = Pick<LoopRun, "status" | "prAutoMerge" | "prSnapshot">;
  const open = { state: "open", merged: false } as LoopRun["prSnapshot"];
  const failed: MergeRun = {
    status: LoopRunStatus.WaitingHuman,
    prAutoMerge: "IldFailed",
    prSnapshot: open,
  };

  test("a run waiting on a person after ILD failed to merge an open pull request asks", () => {
    expect(asksForManualMerge(failed)).toBe(true);
  });

  test("a run with no snapshot yet still asks", () => {
    expect(asksForManualMerge({ ...failed, prSnapshot: null })).toBe(true);
  });

  test.each<[string, Partial<MergeRun>]>([
    [
      "the pull request has since been merged",
      { prSnapshot: { state: "closed", merged: true } as LoopRun["prSnapshot"] },
    ],
    [
      "the pull request has since been closed",
      { prSnapshot: { state: "closed", merged: false } as LoopRun["prSnapshot"] },
    ],
    ["ILD has not tried yet", { prAutoMerge: "IldPending" }],
    ["the provider merges it", { prAutoMerge: "Native" }],
    ["ILD merged it", { prAutoMerge: "IldMerged" }],
    ["it is not an AutoMerge pull request", { prAutoMerge: null }],
    ["the run has moved on", { status: LoopRunStatus.Running }],
  ])("asks for no manual merge when %s", (_, change) => {
    expect(asksForManualMerge({ ...failed, ...change })).toBe(false);
  });
});

describe("offersMerge: the feedback that carries a Merge button", () => {
  test.each([
    ["PR Awaiting Merge", true],
    ["Human Input Needed", false],
    ["Node Failed", false],
  ])("feedback for %s offers Merge: %s", (reason, offers) => {
    expect(offersMerge({ reason })).toBe(offers);
  });
});
