import { afterEach, describe, expect, test } from "vite-plus/test";
import { cleanup, render, screen } from "@testing-library/react";
import WorkItemCard from "./WorkItemCard";
import { WorkItem, WorkItemStatus } from "../types";

afterEach(cleanup);

function makeItem(overrides: Partial<WorkItem>): WorkItem {
  return {
    id: "wi-1",
    title: "Queued item",
    description: "",
    status: WorkItemStatus.Ready,
    priority: 0,
    tags: [],
    repositoryId: "repo-1",
    createdAt: "2026-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  } as WorkItem;
}

describe("a Work queue column card's accessible name", () => {
  test.each([
    [{ status: WorkItemStatus.WorkQueue }, false, "status Work queue, Waiting on dependencies."],
    [
      { status: WorkItemStatus.Ready, repositoryParallelismHold: { inProgress: 2, limit: 2 } },
      false,
      "status Ready, Waiting: repository parallelism limit (2/2 in progress).",
    ],
    [{ status: WorkItemStatus.Ready }, true, "status Ready, Scheduler paused."],
    [{ status: WorkItemStatus.Ready }, false, "status Ready."],
  ] as [Partial<WorkItem>, boolean, string][])(
    "names why the item waits: %o paused=%s",
    (overrides, schedulerPaused, expected) => {
      render(<WorkItemCard workItem={makeItem(overrides)} schedulerPaused={schedulerPaused} />);

      expect(screen.getByRole("button").getAttribute("aria-label")).toBe(
        `Queued item, ${expected} Use left and right arrow keys to move between columns.`,
      );
    },
  );
});
