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

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
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
});
