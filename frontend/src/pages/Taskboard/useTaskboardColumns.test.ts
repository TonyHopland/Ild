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

function ids(result: { current: ReturnType<typeof useTaskboardColumns> }, status: WorkItemStatus) {
  return result.current.columns[status].items.map((item) => `${item.id}:${item.status}`);
}

function renderColumns() {
  return renderHook(() => useTaskboardColumns(EMPTY_TASKBOARD_FILTER, () => {}));
}

function backlog(result: { current: ReturnType<typeof useTaskboardColumns> }) {
  return result.current.columns[WorkItemStatus.Backlog];
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
  });

  test("an item deleted while a Load more that holds it is out does not come back", async () => {
    const server = mockTaskboardServer(Array.from({ length: 25 }, (_, n) => backlogItem(n)));
    const { result } = renderColumns();
    await waitFor(() => expect(backlog(result).items).toHaveLength(20));
    const deleted = server.items[0];
    const release = holdNextRead(server, WorkItemStatus.Backlog);

    act(() => result.current.loadMore(WorkItemStatus.Backlog));
    server.items.splice(0, 1);
    act(() => result.current.removeItem(deleted.id));
    await release();

    await waitFor(() => expect(backlog(result).loadingMore).toBe(false));
    expect(backlog(result).items).toHaveLength(24);
    expect(ids(result, WorkItemStatus.Backlog)).not.toContain(`${deleted.id}:Backlog`);
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

    act(() => result.current.loadMore(WorkItemStatus.Ready));
    server.items[0] = { ...moving, status: WorkItemStatus.Running };
    act(() => result.current.applyItem(server.items[0], { countAsNew: false }));
    await release();

    await waitFor(() =>
      expect(result.current.columns[WorkItemStatus.Ready].loadingMore).toBe(false),
    );
    expect(ids(result, WorkItemStatus.Ready)).toHaveLength(24);
    expect(ids(result, WorkItemStatus.Ready).some((id) => id.startsWith(moving.id))).toBe(false);
    expect(ids(result, WorkItemStatus.Running)).toHaveLength(20);
  });
});
