import { describe, expect, test } from "vite-plus/test";
import { WorkItem, WorkItemStatus } from "../../types";
import { EMPTY_TASKBOARD_FILTER } from "../taskboardFilter";
import { applyItem } from "../taskboardColumns";

type Board = Parameters<typeof applyItem>[0];

function boardItem(id: string, priority: number, status = WorkItemStatus.Backlog): WorkItem {
  return {
    id,
    title: id,
    description: "",
    status,
    priority,
    tags: [],
    repositoryId: "repo-1",
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
  };
}

/** Backlog holds b1, b4, b7 of the 10 it has on the server; Ready holds r1 of 1. */
function pagedBoard(): Board {
  const board: Record<string, unknown> = {};
  for (const status of Object.values(WorkItemStatus))
    board[status] = { items: [], total: 0, loaded: true, loadingMore: false };
  board[WorkItemStatus.Backlog] = {
    items: [boardItem("b1", 1), boardItem("b4", 4), boardItem("b7", 7)],
    total: 10,
    loaded: true,
    loadingMore: false,
  };
  board[WorkItemStatus.Ready] = {
    items: [boardItem("r1", 1, WorkItemStatus.Ready)],
    total: 1,
    loaded: true,
    loadingMore: false,
  };
  return board as unknown as Board;
}

const ids = (board: Board, status: WorkItemStatus) => board[status].items.map((wi) => wi.id);

describe("applyItem for a card dropped below the last loaded card of a paged column", () => {
  test("a card from the same column shows at the bottom of the loaded cards", () => {
    const next = applyItem(pagedBoard(), boardItem("b1", 7.5), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
      placedAfter: "b7",
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b4", "b7", "b1"]);
    expect(next.Backlog.total).toBe(10);
  });

  test("a card from another column shows at the bottom of the loaded cards", () => {
    const next = applyItem(pagedBoard(), boardItem("r1", 7.5), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
      placedAfter: "b7",
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b1", "b4", "b7", "r1"]);
    expect(next.Backlog.total).toBe(11);
    expect(ids(next, WorkItemStatus.Ready)).toEqual([]);
  });

  test("placed after a card that is not the last loaded one, the order alone decides", () => {
    const next = applyItem(pagedBoard(), boardItem("b1", 7.5), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
      placedAfter: "b4",
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b4", "b7"]);
  });
});
