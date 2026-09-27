import { describe, expect, test } from "vite-plus/test";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../../types";
import { EMPTY_TASKBOARD_FILTER } from "../taskboardFilter";
import {
  applyItem,
  compareServerOrder,
  insertInServerOrder,
  removeItem,
} from "../taskboardColumns";

type Board = Parameters<typeof applyItem>[0];

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Item",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
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
    ...overrides,
  };
}

/** Item `id` created on day `day` of 2026-01; a higher day is newer. */
function dayItem(id: string, day: number, overrides: Partial<WorkItem> = {}): WorkItem {
  return makeItem({
    id,
    title: id,
    createdAt: `2026-01-${String(day).padStart(2, "0")}T00:00:00Z`,
    ...overrides,
  });
}

/** A board whose columns hold the given items (already in server order) and totals. */
function boardWith(columns: Partial<Record<WorkItemStatus, { items: WorkItem[]; total: number }>>) {
  const board: Record<string, unknown> = {};
  for (const status of Object.values(WorkItemStatus)) {
    const column = columns[status] ?? { items: [], total: 0 };
    board[status] = { ...column, loaded: true, loadingMore: false };
  }
  return board as unknown as Board;
}

function ids(board: Board, status: WorkItemStatus): string[] {
  return board[status].items.map((wi: WorkItem) => wi.id);
}

describe("compareServerOrder", () => {
  test("orders newest first and breaks equal creation times by id, highest first", () => {
    const items = [
      dayItem("a", 1),
      dayItem("10", 5),
      dayItem("c", 9),
      dayItem("9", 5),
      dayItem("b", 5),
    ];
    expect([...items].sort(compareServerOrder).map((wi) => wi.id)).toEqual([
      "c",
      "b",
      "9",
      "10",
      "a",
    ]);
  });
});

describe("insertInServerOrder", () => {
  const loaded = [dayItem("d9", 9), dayItem("d6", 6), dayItem("d3", 3)];

  test("puts the newest item at the top, not the bottom", () => {
    expect(insertInServerOrder(loaded, dayItem("new", 20)).map((wi) => wi.id)).toEqual([
      "new",
      "d9",
      "d6",
      "d3",
    ]);
  });

  test("puts an item between its neighbours in server order", () => {
    expect(insertInServerOrder(loaded, dayItem("mid", 5)).map((wi) => wi.id)).toEqual([
      "d9",
      "d6",
      "mid",
      "d3",
    ]);
  });
});

describe("applyItem", () => {
  const backlogPage = [dayItem("b9", 9), dayItem("b6", 6), dayItem("b3", 3)];

  test("a created item lands at the top of its column and the total goes up by one", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 10 } });

    const next = applyItem(board, dayItem("new", 20), EMPTY_TASKBOARD_FILTER, {
      countAsNew: true,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["new", "b9", "b6", "b3"]);
    expect(next.Backlog.total).toBe(11);
  });

  test("an item sorting after the last loaded card of a column with more on the server is not inserted", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 10 } });

    const next = applyItem(board, dayItem("old", 1), EMPTY_TASKBOARD_FILTER, {
      countAsNew: true,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b9", "b6", "b3"]);
    expect(next.Backlog.total).toBe(11);
  });

  test("an old item joins the bottom of a column that is fully loaded", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const next = applyItem(board, dayItem("old", 1), EMPTY_TASKBOARD_FILTER, {
      countAsNew: true,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b9", "b6", "b3", "old"]);
    expect(next.Backlog.total).toBe(4);
  });

  test("a status change moves the card to its sorted place in the new column and updates both totals", () => {
    const board = boardWith({
      Ready: { items: [dayItem("r7", 7, { status: WorkItemStatus.Ready })], total: 1 },
      Running: {
        items: [
          dayItem("u9", 9, { status: WorkItemStatus.Running }),
          dayItem("u2", 2, { status: WorkItemStatus.Running }),
        ],
        total: 2,
      },
    });

    const next = applyItem(
      board,
      dayItem("r7", 7, { status: WorkItemStatus.Running }),
      EMPTY_TASKBOARD_FILTER,
      { countAsNew: false },
    );

    expect(ids(next, WorkItemStatus.Ready)).toEqual([]);
    expect(next.Ready.total).toBe(0);
    expect(ids(next, WorkItemStatus.Running)).toEqual(["u9", "r7", "u2"]);
    expect(next.Running.total).toBe(3);
  });

  test("a card moved beyond the loaded page of its new column leaves the view but stays counted", () => {
    const board = boardWith({
      Ready: { items: [dayItem("r1", 1, { status: WorkItemStatus.Ready })], total: 1 },
      Done: { items: [dayItem("d9", 9, { status: WorkItemStatus.Done })], total: 40 },
    });

    const next = applyItem(
      board,
      dayItem("r1", 1, { status: WorkItemStatus.Done }),
      EMPTY_TASKBOARD_FILTER,
      { countAsNew: false },
    );

    expect(ids(next, WorkItemStatus.Ready)).toEqual([]);
    expect(next.Ready.total).toBe(0);
    expect(ids(next, WorkItemStatus.Done)).toEqual(["d9"]);
    expect(next.Done.total).toBe(41);
  });

  test("an edit keeps the card in place when it still matches", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const next = applyItem(board, dayItem("b6", 6, { title: "Renamed" }), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
    });

    expect(next.Backlog.items.map((wi: WorkItem) => wi.title)).toEqual(["b9", "Renamed", "b3"]);
    expect(next.Backlog.total).toBe(3);
  });

  test("a card edited out of the active filter is removed and a new non-matching item is not shown", () => {
    const filter = { ...EMPTY_TASKBOARD_FILTER, repositoryId: "repo-1" };
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const edited = applyItem(board, dayItem("b6", 6, { repositoryId: "repo-2" }), filter, {
      countAsNew: false,
    });
    expect(ids(edited, WorkItemStatus.Backlog)).toEqual(["b9", "b3"]);
    expect(edited.Backlog.total).toBe(2);

    const created = applyItem(edited, dayItem("new", 20, { repositoryId: "repo-2" }), filter, {
      countAsNew: true,
    });
    expect(ids(created, WorkItemStatus.Backlog)).toEqual(["b9", "b3"]);
    expect(created.Backlog.total).toBe(2);
  });
});

describe("removeItem", () => {
  test("removes the card and lowers its column's total; an unknown id changes nothing", () => {
    const board = boardWith({
      Backlog: { items: [dayItem("b9", 9), dayItem("b3", 3)], total: 5 },
    });

    const next = removeItem(board, "b9");
    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b3"]);
    expect(next.Backlog.total).toBe(4);

    const unchanged = removeItem(next, "ghost");
    expect(ids(unchanged, WorkItemStatus.Backlog)).toEqual(["b3"]);
    expect(unchanged.Backlog.total).toBe(4);
  });
});
