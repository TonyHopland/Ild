import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup } from "@testing-library/react";
import { useWorkItemDetail } from "./useWorkItemDetail";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import { repositoryService, loopTemplateService, workItemService } from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Running,
    priority: WorkItemPriority.Medium,
    tags: [],
    conversation: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: "run-1",
    worktreePath: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

type Handler = (m: { type: string; payload: unknown; timestamp: string }) => void;

/**
 * Installs a controllable useSignalR mock. `invoke` resolves to whatever the
 * test supplies for the SubscribeToRun backlog; `emit` drives live events.
 */
function mockSignalR(subscribeResult: unknown) {
  const handlers = new Map<string, Set<Handler>>();
  const on = vi.fn((event: string, h: Handler) => {
    const set = handlers.get(event) ?? new Set<Handler>();
    set.add(h);
    handlers.set(event, set);
  });
  const off = vi.fn((event: string, h: Handler) => handlers.get(event)?.delete(h));
  const invoke = vi.fn().mockReturnValue(Promise.resolve(subscribeResult));
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on,
    off,
    invoke,
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  const emit = (event: string, payload: unknown) =>
    handlers.get(event)?.forEach((h) => h({ type: event, payload, timestamp: "" }));
  return { emit, invoke };
}

function stubServices() {
  vi.spyOn(repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
}

const progress = (runId: string, line: string, seq: number) => ({
  runId,
  nodeId: "n-1",
  line,
  seq,
});

describe("useWorkItemDetail live handoff", () => {
  test("seeds from the backlog and dedupes chunks already replayed", async () => {
    stubServices();
    const { emit } = mockSignalR({ text: "BACKLOG\n", lastSeq: 3 });

    // Stable references — re-creating them per render would churn the hook's
    // effect deps and defeat the test.
    const wi = makeWorkItem();
    const onSave = vi.fn();
    const { result } = renderHook(() => useWorkItemDetail(wi, onSave));

    // Let mount effects and the SubscribeToRun promise settle.
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(result.current.progressText).toBe("BACKLOG\n");

    // A chunk already contained in the backlog (seq <= 3) must be ignored.
    act(() => emit("NodeProgress", progress("run-1", "DUP", 2)));
    expect(result.current.progressText).toBe("BACKLOG\n");

    // A genuinely new live chunk appends without a gap.
    act(() => emit("NodeProgress", progress("run-1", "LIVE\n", 4)));
    expect(result.current.progressText).toBe("BACKLOG\nLIVE\n");
  });

  test("queues live chunks that arrive during seeding, then flushes in order", async () => {
    stubServices();
    let resolveSubscribe: (v: unknown) => void = () => {};
    const subscribe = new Promise<unknown>((res) => {
      resolveSubscribe = res;
    });
    const { emit } = mockSignalR(subscribe);

    const wi = makeWorkItem();
    const onSave = vi.fn();
    const { result } = renderHook(() => useWorkItemDetail(wi, onSave));
    await act(async () => {
      await Promise.resolve();
    });

    // Live chunk lands BEFORE the backlog replay resolves — it must be queued,
    // not dropped, and not applied yet.
    act(() => emit("NodeProgress", progress("run-1", "LIVE\n", 4)));
    expect(result.current.progressText).toBe("");

    // Backlog resolves; the queued chunk flushes on top of it.
    await act(async () => {
      resolveSubscribe({ text: "BACKLOG\n", lastSeq: 3 });
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(result.current.progressText).toBe("BACKLOG\nLIVE\n");
  });

  test("ignores progress for a different run", async () => {
    stubServices();
    const { emit } = mockSignalR({ text: "", lastSeq: 0 });

    const wi = makeWorkItem();
    const onSave = vi.fn();
    const { result } = renderHook(() => useWorkItemDetail(wi, onSave));
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    act(() => emit("NodeProgress", progress("other-run", "NOPE\n", 1)));
    expect(result.current.progressText).toBe("");
  });
});
