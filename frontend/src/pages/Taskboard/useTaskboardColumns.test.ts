import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, renderHook, waitFor } from "@testing-library/react";
import { useTaskboardColumns } from "./useTaskboardColumns";
import { mockTaskboardServer } from "../../test-support";
import { WorkItem, WorkItemPage, WorkItemPriority, WorkItemStatus } from "../../types";
import { EMPTY_TASKBOARD_FILTER } from "../../utils/taskboardFilter";

afterEach(() => {
  vi.restoreAllMocks();
});

function backlogItem(n: number): WorkItem {
  return {
    id: `b${String(n).padStart(4, "0")}`,
    title: `b${n}`,
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags: [],
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: new Date(Date.UTC(2026, 0, 1) + n * 60_000).toISOString(),
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
  };
}

function itemIn(status: WorkItemStatus, n: number): WorkItem {
  return {
    ...backlogItem(n),
    id: `${status}-${String(n).padStart(4, "0")}`,
    title: `${status} ${n}`,
    status,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

/** Holds the next read of `status` until the returned release is called; the page is taken when asked. */
function holdNextRead(server: ReturnType<typeof mockTaskboardServer>, status: WorkItemStatus) {
  const held = deferred<void>();
  const answer = server.getPage.getMockImplementation()!;
  let armed = true;
  server.getPage.mockImplementation(async (q) => {
    if (!armed || q.status !== status) return answer(q);
    armed = false;
    const page: WorkItemPage = server.page(q);
    await held.promise;
    return page;
  });
  return () => act(async () => held.resolve());
}

/**
 * Leaves every counts request unanswered, so the totals a test reads are the
 * board's own; returns how many were asked for so far.
 */
function holdCounts(server: ReturnType<typeof mockTaskboardServer>) {
  server.getCounts.mockImplementation(() => new Promise(() => {}));
  return server.getCounts.mock.calls.length;
}

function ids(result: { current: ReturnType<typeof useTaskboardColumns> }, status: WorkItemStatus) {
  return result.current.columns[status].items.map((item) => `${item.id}:${item.status}`);
}

function renderColumns() {
  return renderHook(() => useTaskboardColumns(EMPTY_TASKBOARD_FILTER, () => {}));
}

function backlog(result: { current: ReturnType<typeof useTaskboardColumns> }) {
  return result.current.columns[WorkItemStatus.Backlog];
}

function running(result: { current: ReturnType<typeof useTaskboardColumns> }) {
  return result.current.columns[WorkItemStatus.Running];
}

/** How many reads of `status` the board has asked the server for so far. */
function reads(server: ReturnType<typeof mockTaskboardServer>, status: WorkItemStatus) {
  return server.getPage.mock.calls.filter(([q]) => q.status === status).length;
}

/** A board showing three Running cards, with two more on the server it has not heard of. */
async function runningColumnBehindItsServer(onError: (message: string) => void = () => {}) {
  const server = mockTaskboardServer(
    Array.from({ length: 3 }, (_, n) => itemIn(WorkItemStatus.Running, n)),
  );
  const { result } = renderHook(() => useTaskboardColumns(EMPTY_TASKBOARD_FILTER, onError));
  await waitFor(() => expect(running(result).items).toHaveLength(3));
  server.items.push(itemIn(WorkItemStatus.Running, 3), itemIn(WorkItemStatus.Running, 4));
  return { server, result };
}

describe("useTaskboardColumns", () => {
  test("a reload of more than one request's worth of cards keeps every loaded card", async () => {
    const server = mockTaskboardServer(Array.from({ length: 530 }, (_, n) => backlogItem(n)));
    const { result } = renderColumns();
    await waitFor(() => expect(backlog(result).items).toHaveLength(20));
    while (backlog(result).items.length < 520) {
      const loaded = backlog(result).items.length;
      await act(async () => result.current.loadMore(WorkItemStatus.Backlog));
      await waitFor(() => expect(backlog(result).items.length).toBeGreaterThan(loaded));
    }
    const before = server.getPage.mock.calls.length;

    await act(async () => result.current.reloadAll({ windowed: true }));

    await waitFor(() =>
      expect(
        server.getPage.mock.calls
          .slice(before)
          .filter(([q]) => q.status === WorkItemStatus.Backlog)
          .map(([q]) => [q.skip, q.take]),
      ).toEqual([
        [0, 500],
        [500, 20],
      ]),
    );
    await waitFor(() => expect(backlog(result).loaded).toBe(true));
    expect(backlog(result).items).toHaveLength(520);
    expect(backlog(result).items[519].id).toBe(
      server.page({ status: "Backlog", skip: 519, take: 1 }).items[0].id,
    );
  });

  test("a card changed while its column is being read keeps its live copy when the read lands", async () => {
    const server = mockTaskboardServer(Array.from({ length: 5 }, (_, n) => backlogItem(n)));
    const { result } = renderColumns();
    await waitFor(() => expect(backlog(result).items).toHaveLength(5));

    const held = deferred<void>();
    server.getPage.mockImplementationOnce(async (q) => {
      const page: WorkItemPage = server.page(q);
      await held.promise;
      return page;
    });
    await act(async () => result.current.reloadAll({ windowed: true }));
    const live = { ...backlog(result).items[0], title: "renamed while the read was out" };
    act(() => result.current.applyItem(live, { countAsNew: false }));

    await act(async () => held.resolve());

    await waitFor(() => expect(backlog(result).items[0].title).toBe(live.title));
    expect(backlog(result).items).toHaveLength(5);
  });

  test("a card moved while its old column is being read leaves that column when the read lands", async () => {
    const server = mockTaskboardServer([itemIn(WorkItemStatus.Running, 0)]);
    const { result } = renderColumns();
    await waitFor(() =>
      expect(result.current.columns[WorkItemStatus.Running].items).toHaveLength(1),
    );
    // Created elsewhere unheard, so only the reload about to be held brings it to Ready.
    const moving = itemIn(WorkItemStatus.Ready, 1);
    server.items.unshift(moving);
    const release = holdNextRead(server, WorkItemStatus.Ready);
    const countsBefore = holdCounts(server);

    await act(async () => result.current.reloadAll({ windowed: true }));
    server.items[0] = { ...moving, status: WorkItemStatus.Running };
    act(() => result.current.applyItem(server.items[0], { countAsNew: false }));
    await release();

    await waitFor(() => expect(result.current.columns[WorkItemStatus.Ready].loaded).toBe(true));
    await waitFor(() =>
      expect(ids(result, WorkItemStatus.Running)).toEqual([
        `${moving.id}:Running`,
        "Running-0000:Running",
      ]),
    );
    expect(ids(result, WorkItemStatus.Ready)).toEqual([]);
    expect(result.current.columns[WorkItemStatus.Ready].total).toBe(0);
    expect(server.getCounts.mock.calls.length).toBeGreaterThan(countsBefore);
  });

  test("an item deleted while a Load more that holds it is out does not come back", async () => {
    const server = mockTaskboardServer(Array.from({ length: 25 }, (_, n) => backlogItem(n)));
    const { result } = renderColumns();
    await waitFor(() => expect(backlog(result).items).toHaveLength(20));
    const deleted = server.items[0];
    const release = holdNextRead(server, WorkItemStatus.Backlog);
    const countsBefore = holdCounts(server);

    act(() => result.current.loadMore(WorkItemStatus.Backlog));
    server.items.splice(0, 1);
    act(() => result.current.removeItem(deleted.id));
    await release();

    await waitFor(() => expect(backlog(result).loadingMore).toBe(false));
    expect(backlog(result).items).toHaveLength(24);
    expect(ids(result, WorkItemStatus.Backlog)).not.toContain(`${deleted.id}:Backlog`);
    expect(backlog(result).total).toBe(24);
    expect(server.getCounts.mock.calls.length).toBeGreaterThan(countsBefore);
  });

  test("an unloaded item moved past its new column's loaded page leaves the old column's read", async () => {
    const server = mockTaskboardServer([
      ...Array.from({ length: 25 }, (_, n) => itemIn(WorkItemStatus.Ready, n)),
      ...Array.from({ length: 25 }, (_, n) => itemIn(WorkItemStatus.Running, 100 + n)),
    ]);
    const { result } = renderColumns();
    await waitFor(() =>
      expect(result.current.columns[WorkItemStatus.Ready].items).toHaveLength(20),
    );
    const moving = server.items[0];
    const release = holdNextRead(server, WorkItemStatus.Ready);
    const countsBefore = holdCounts(server);

    act(() => result.current.loadMore(WorkItemStatus.Ready));
    server.items[0] = { ...moving, status: WorkItemStatus.Running };
    act(() => result.current.applyItem(server.items[0], { countAsNew: false }));
    await release();

    await waitFor(() =>
      expect(result.current.columns[WorkItemStatus.Ready].loadingMore).toBe(false),
    );
    expect(ids(result, WorkItemStatus.Ready)).toHaveLength(24);
    expect(ids(result, WorkItemStatus.Ready).some((id) => id.startsWith(moving.id))).toBe(false);
    expect(result.current.columns[WorkItemStatus.Ready].total).toBe(24);
    expect(server.getCounts.mock.calls.length).toBeGreaterThan(countsBefore);
    expect(ids(result, WorkItemStatus.Running)).toHaveLength(20);
  });

  test("a card moved to a short column after that column's total arrived is shown there, not left behind Load more", async () => {
    const server = mockTaskboardServer([itemIn(WorkItemStatus.Ready, 0)]);
    const { result } = renderColumns();
    await waitFor(() => expect(result.current.columns[WorkItemStatus.Ready].items).toHaveLength(1));

    server.items[0] = { ...server.items[0], status: WorkItemStatus.Running };
    await act(async () => result.current.requestCountsRefresh());
    await waitFor(() => expect(running(result).total).toBe(1));
    act(() => result.current.applyItem(server.items[0], { countAsNew: false }));

    await waitFor(() => {
      expect(ids(result, WorkItemStatus.Running)).toEqual(["Ready-0000:Running"]);
      expect(running(result).total).toBe(1);
    });
    expect(ids(result, WorkItemStatus.Ready)).toEqual([]);
    expect(result.current.columns[WorkItemStatus.Ready].total).toBe(0);
  });

  test("a card moved to a short column while that column is being re-read starts no second read and is shown when the read lands", async () => {
    const server = mockTaskboardServer([itemIn(WorkItemStatus.Ready, 0)]);
    const { result } = renderColumns();
    await waitFor(() => expect(result.current.columns[WorkItemStatus.Ready].items).toHaveLength(1));
    await waitFor(() => expect(running(result).loaded).toBe(true));
    const readsBefore = reads(server, WorkItemStatus.Running);
    const release = holdNextRead(server, WorkItemStatus.Running);

    server.items[0] = { ...server.items[0], status: WorkItemStatus.Running };
    await act(async () => result.current.requestCountsRefresh());
    await waitFor(() => expect(running(result).total).toBe(1));
    await waitFor(() => expect(reads(server, WorkItemStatus.Running)).toBe(readsBefore + 1));
    act(() => result.current.applyItem(server.items[0], { countAsNew: false }));
    expect(reads(server, WorkItemStatus.Running)).toBe(readsBefore + 1);
    await release();

    await waitFor(() => {
      expect(ids(result, WorkItemStatus.Running)).toEqual(["Ready-0000:Running"]);
      expect(running(result).total).toBe(1);
    });
    expect(ids(result, WorkItemStatus.Ready)).toEqual([]);
    expect(result.current.columns[WorkItemStatus.Ready].total).toBe(0);
  });

  test("a short column whose total grows by cards the board never held shows them without Load more", async () => {
    const { server, result } = await runningColumnBehindItsServer();

    await act(async () => result.current.requestCountsRefresh());

    await waitFor(() =>
      expect(ids(result, WorkItemStatus.Running)).toEqual(
        server
          .page({ status: WorkItemStatus.Running, skip: 0, take: 20 })
          .items.map((item) => `${item.id}:Running`),
      ),
    );
    expect(running(result).items).toHaveLength(5);
    expect(running(result).total).toBe(5);
  });

  test("a loaded card removed from a column with more than a page is replaced, and Load more stays", async () => {
    const server = mockTaskboardServer(Array.from({ length: 25 }, (_, n) => backlogItem(n)));
    const { result } = renderColumns();
    await waitFor(() => expect(backlog(result).items).toHaveLength(20));
    const removed = backlog(result).items[0];

    server.items.splice(
      server.items.findIndex((item) => item.id === removed.id),
      1,
    );
    act(() => result.current.removeItem(removed.id));

    await waitFor(() => expect(backlog(result).items).toHaveLength(20));
    expect(backlog(result).items.map((item) => item.id)).toEqual(
      server.page({ status: "Backlog", skip: 0, take: 20 }).items.map((item) => item.id),
    );
    expect(backlog(result).total).toBe(24);
  });

  test("a move past a full loaded page, and a counts refresh that leaves no column short, start no column read", async () => {
    const server = mockTaskboardServer([
      ...Array.from({ length: 25 }, (_, n) => itemIn(WorkItemStatus.Ready, n)),
      ...Array.from({ length: 25 }, (_, n) => itemIn(WorkItemStatus.Running, 100 + n)),
    ]);
    const { result } = renderColumns();
    await waitFor(() => expect(result.current.isInitialLoading).toBe(false));
    const shown = ids(result, WorkItemStatus.Running);
    expect(shown).toHaveLength(20);
    const readsBefore = server.getPage.mock.calls.length;

    server.items[0] = { ...server.items[0], status: WorkItemStatus.Running };
    act(() => result.current.applyItem(server.items[0], { countAsNew: false }));
    await act(async () => result.current.requestCountsRefresh());

    await waitFor(() => expect(running(result).total).toBe(26));
    expect(result.current.columns[WorkItemStatus.Ready].total).toBe(24);
    expect(ids(result, WorkItemStatus.Running)).toEqual(shown);
    expect(result.current.columns[WorkItemStatus.Ready].items).toHaveLength(20);
    expect(server.getPage.mock.calls.length).toBe(readsBefore);
  });

  test("a column left short while its Load more is out is not read a second time", async () => {
    const server = mockTaskboardServer(Array.from({ length: 45 }, (_, n) => backlogItem(n)));
    const { result } = renderColumns();
    await waitFor(() => expect(backlog(result).items).toHaveLength(20));
    const removed = backlog(result).items[0];
    const readsBefore = reads(server, WorkItemStatus.Backlog);
    const release = holdNextRead(server, WorkItemStatus.Backlog);

    act(() => result.current.loadMore(WorkItemStatus.Backlog));
    server.items.splice(
      server.items.findIndex((item) => item.id === removed.id),
      1,
    );
    act(() => result.current.removeItem(removed.id));
    expect(reads(server, WorkItemStatus.Backlog)).toBe(readsBefore + 1);
    await release();

    await waitFor(() => expect(backlog(result).loadingMore).toBe(false));
    expect(backlog(result).items).toHaveLength(39);
    expect(backlog(result).total).toBe(44);
  });

  test("a re-read of a short column that a reload overtook neither alters the board nor blocks the next re-read", async () => {
    const { server, result } = await runningColumnBehindItsServer();
    const release = holdNextRead(server, WorkItemStatus.Running);
    const readsBefore = reads(server, WorkItemStatus.Running);
    await act(async () => result.current.requestCountsRefresh());
    await waitFor(() => expect(reads(server, WorkItemStatus.Running)).toBe(readsBefore + 1));

    await act(async () => result.current.reloadAll({ windowed: true }));
    await waitFor(() => expect(running(result).items).toHaveLength(5));
    server.items.push(itemIn(WorkItemStatus.Running, 5));
    await act(async () => result.current.requestCountsRefresh());

    await waitFor(() => expect(running(result).items).toHaveLength(6));
    expect(running(result).total).toBe(6);
    await release();
    expect(running(result).items).toHaveLength(6);
    expect(running(result).total).toBe(6);
  });

  test("a card changed while its short column is being re-read keeps its live copy when the read lands", async () => {
    const { server, result } = await runningColumnBehindItsServer();
    const release = holdNextRead(server, WorkItemStatus.Running);
    await act(async () => result.current.requestCountsRefresh());
    await waitFor(() => expect(running(result).total).toBe(5));

    const live = { ...running(result).items[0], title: "renamed while the re-read was out" };
    act(() => result.current.applyItem(live, { countAsNew: false }));
    await release();

    await waitFor(() => expect(running(result).items).toHaveLength(5));
    expect(running(result).items.find((item) => item.id === live.id)?.title).toBe(live.title);
    expect(running(result).total).toBe(5);
  });

  test("a failed re-read of a short column is reported once, not retried, and the column is re-read on the next refresh", async () => {
    const onError = vi.fn();
    const { server, result } = await runningColumnBehindItsServer(onError);
    const readsBefore = reads(server, WorkItemStatus.Running);
    server.getPage.mockImplementationOnce(async () => {
      throw new Error("the column could not be read");
    });

    await act(async () => result.current.requestCountsRefresh());

    await waitFor(() => expect(onError).toHaveBeenCalledWith("the column could not be read"));
    expect(onError).toHaveBeenCalledTimes(1);
    expect(reads(server, WorkItemStatus.Running)).toBe(readsBefore + 1);
    expect(running(result).items).toHaveLength(3);

    await act(async () => result.current.requestCountsRefresh());

    await waitFor(() => expect(running(result).items).toHaveLength(5));
    expect(running(result).total).toBe(5);
    expect(onError).toHaveBeenCalledTimes(1);
  });
});
