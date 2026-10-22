import { describe, expect, test } from "vite-plus/test";
import { WorkItem, WorkItemStatus } from "../../types";
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
    priority: 0,
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

/**
 * Item `id` at board position `priority` (lower is higher on the board). Its
 * creation time runs the other way, so newest-first would invert the board order.
 */
function boardItem(id: string, priority: number, overrides: Partial<WorkItem> = {}): WorkItem {
  return makeItem({
    id,
    title: id,
    priority,
    createdAt: new Date(Date.UTC(2026, 0, 1) + (priority + 1000) * 60_000).toISOString(),
    ...overrides,
  });
}

/** A board whose columns hold the given items (already in board order) and totals. */
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
  test("orders by priority, lowest first, regardless of creation time", () => {
    const items = [boardItem("a", 3), boardItem("b", -2), boardItem("c", 0.5), boardItem("d", 0)];
    expect([...items].sort(compareServerOrder).map((wi) => wi.id)).toEqual(["b", "d", "c", "a"]);
  });

  test("breaks equal priorities by id, shorter first then by code unit, never by creation time", () => {
    const at = (day: number) => `2026-01-${String(day).padStart(2, "0")}T00:00:00Z`;
    const items = [
      makeItem({ id: "10", priority: 1, createdAt: at(1) }),
      makeItem({ id: "ab", priority: 1, createdAt: at(2) }),
      makeItem({ id: "2", priority: 1, createdAt: at(3) }),
      makeItem({ id: "b", priority: 1, createdAt: at(4) }),
      makeItem({ id: "9", priority: 1, createdAt: at(5) }),
    ];
    expect([...items].sort(compareServerOrder).map((wi) => wi.id)).toEqual([
      "2",
      "9",
      "b",
      "10",
      "ab",
    ]);
  });
});

describe("insertInServerOrder", () => {
  const loaded = [boardItem("p1", 1), boardItem("p4", 4), boardItem("p7", 7)];

  test("puts an item with the lowest priority at the top, not the bottom", () => {
    expect(insertInServerOrder(loaded, boardItem("new", 0)).map((wi) => wi.id)).toEqual([
      "new",
      "p1",
      "p4",
      "p7",
    ]);
  });

  test("puts an item between its neighbours in board order", () => {
    expect(insertInServerOrder(loaded, boardItem("mid", 5)).map((wi) => wi.id)).toEqual([
      "p1",
      "p4",
      "mid",
      "p7",
    ]);
  });
});

describe("applyItem", () => {
  const backlogPage = [boardItem("b1", 1), boardItem("b4", 4), boardItem("b7", 7)];

  test("a created item lands at the top of its column and the total goes up by one", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 10 } });

    const next = applyItem(board, boardItem("new", 0), EMPTY_TASKBOARD_FILTER, {
      countAsNew: true,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["new", "b1", "b4", "b7"]);
    expect(next.Backlog.total).toBe(11);
  });

  test("an item sorting after the last loaded card of a column with more on the server is not inserted", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 10 } });

    const next = applyItem(board, boardItem("late", 9), EMPTY_TASKBOARD_FILTER, {
      countAsNew: true,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b1", "b4", "b7"]);
    expect(next.Backlog.total).toBe(11);
  });

  test("an item sorting last joins the bottom of a column that is fully loaded", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const next = applyItem(board, boardItem("late", 9), EMPTY_TASKBOARD_FILTER, {
      countAsNew: true,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b1", "b4", "b7", "late"]);
    expect(next.Backlog.total).toBe(4);
  });

  test("a status change moves the card to its sorted place in the new column and updates both totals", () => {
    const board = boardWith({
      Ready: { items: [boardItem("r5", 5, { status: WorkItemStatus.Ready })], total: 1 },
      Running: {
        items: [
          boardItem("u2", 2, { status: WorkItemStatus.Running }),
          boardItem("u8", 8, { status: WorkItemStatus.Running }),
        ],
        total: 2,
      },
    });

    const next = applyItem(
      board,
      boardItem("r5", 5, { status: WorkItemStatus.Running }),
      EMPTY_TASKBOARD_FILTER,
      { countAsNew: false },
    );

    expect(ids(next, WorkItemStatus.Ready)).toEqual([]);
    expect(next.Ready.total).toBe(0);
    expect(ids(next, WorkItemStatus.Running)).toEqual(["u2", "r5", "u8"]);
    expect(next.Running.total).toBe(3);
  });

  test("a card moved beyond the loaded page of its new column leaves the view but stays counted", () => {
    const board = boardWith({
      Ready: { items: [boardItem("r9", 9, { status: WorkItemStatus.Ready })], total: 1 },
      Done: { items: [boardItem("d1", 1, { status: WorkItemStatus.Done })], total: 40 },
    });

    const next = applyItem(
      board,
      boardItem("r9", 9, { status: WorkItemStatus.Done }),
      EMPTY_TASKBOARD_FILTER,
      { countAsNew: false },
    );

    expect(ids(next, WorkItemStatus.Ready)).toEqual([]);
    expect(next.Ready.total).toBe(0);
    expect(ids(next, WorkItemStatus.Done)).toEqual(["d1"]);
    expect(next.Done.total).toBe(41);
  });

  test("an edit keeps the card in place when it still matches", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const next = applyItem(
      board,
      boardItem("b4", 4, { title: "Renamed" }),
      EMPTY_TASKBOARD_FILTER,
      { countAsNew: false },
    );

    expect(next.Backlog.items.map((wi: WorkItem) => wi.title)).toEqual(["b1", "Renamed", "b7"]);
    expect(next.Backlog.total).toBe(3);
  });

  test("a reordered card that stays in its column moves to its new place there", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const toTop = applyItem(board, boardItem("b7", 0), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
    });
    expect(ids(toTop, WorkItemStatus.Backlog)).toEqual(["b7", "b1", "b4"]);
    expect(toTop.Backlog.total).toBe(3);

    const between = applyItem(toTop, boardItem("b1", 5), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
    });
    expect(ids(between, WorkItemStatus.Backlog)).toEqual(["b7", "b4", "b1"]);
    expect(between.Backlog.total).toBe(3);
  });

  test("a card reordered past the last loaded card of a column with more on the server leaves the view but stays counted", () => {
    const board = boardWith({ Backlog: { items: backlogPage, total: 10 } });

    const next = applyItem(board, boardItem("b1", 20), EMPTY_TASKBOARD_FILTER, {
      countAsNew: false,
    });

    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b4", "b7"]);
    expect(next.Backlog.total).toBe(10);
  });

  test("a card edited out of the active filter is removed and a new non-matching item is not shown", () => {
    const filter = { ...EMPTY_TASKBOARD_FILTER, repositoryId: "repo-1" };
    const board = boardWith({ Backlog: { items: backlogPage, total: 3 } });

    const edited = applyItem(board, boardItem("b4", 4, { repositoryId: "repo-2" }), filter, {
      countAsNew: false,
    });
    expect(ids(edited, WorkItemStatus.Backlog)).toEqual(["b1", "b7"]);
    expect(edited.Backlog.total).toBe(2);

    const created = applyItem(edited, boardItem("new", 0, { repositoryId: "repo-2" }), filter, {
      countAsNew: true,
    });
    expect(ids(created, WorkItemStatus.Backlog)).toEqual(["b1", "b7"]);
    expect(created.Backlog.total).toBe(2);
  });
});

describe("removeItem", () => {
  test("removes the card and lowers its column's total; an unknown id changes nothing", () => {
    const board = boardWith({
      Backlog: { items: [boardItem("b1", 1), boardItem("b7", 7)], total: 5 },
    });

    const next = removeItem(board, "b1");
    expect(ids(next, WorkItemStatus.Backlog)).toEqual(["b7"]);
    expect(next.Backlog.total).toBe(4);

    const unchanged = removeItem(next, "ghost");
    expect(ids(unchanged, WorkItemStatus.Backlog)).toEqual(["b7"]);
    expect(unchanged.Backlog.total).toBe(4);
  });
});
