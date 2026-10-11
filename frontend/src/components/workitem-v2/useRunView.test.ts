import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup } from "@testing-library/react";
import { useRunView } from "./useRunView";
import { LoopRun, LoopRunStatus } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import { loopRunService } from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Running,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 0,
    startedAt: "2026-09-24T09:00:00Z",
    completedAt: null,
    nodes: [],
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

async function flush() {
  await act(async () => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  });
}

type Handler = (m: { type: string; payload: unknown; timestamp: string }) => void;

function stageHub() {
  const handlers = new Map<string, Set<Handler>>();
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: (event: string, h: Handler) => {
      const set = handlers.get(event) ?? new Set<Handler>();
      set.add(h);
      handlers.set(event, set);
    },
    off: (event: string, h: Handler) => handlers.get(event)?.delete(h),
    invoke: () => Promise.resolve(undefined),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  return (event: string, runId: string) =>
    act(async () => {
      handlers.get(event)?.forEach((h) => h({ type: event, payload: { runId }, timestamp: "" }));
    });
}

function stageReads() {
  vi.spyOn(loopRunService, "getConversation").mockImplementation((runId: string) =>
    Promise.resolve({ runId, messages: [], lastEventId: null } as unknown as Awaited<
      ReturnType<typeof loopRunService.getConversation>
    >),
  );
  const reads: ReturnType<typeof deferred<LoopRun>>[] = [];
  const getById = vi.spyOn(loopRunService, "getById").mockImplementation(() => {
    const read = deferred<LoopRun>();
    reads.push(read);
    return read.promise;
  });
  return { reads, getById };
}

const stateEvents = ["LoopRunStateChanged", "NodeStateChanged", "RunHalted"];

describe("useRunView's run detail", () => {
  test("of the item's current run is the item's copy, kept as it changes, and never read again", async () => {
    const emit = stageHub();
    const { getById } = stageReads();
    const first = makeRun();
    const { result, rerender } = renderHook(
      ({ run }) => useRunView("run-1", 0, { isCurrent: true, run }),
      { initialProps: { run: first } },
    );
    await flush();
    expect(result.current.run).toBe(first);

    for (const event of stateEvents) await emit(event, "run-1");
    const parked = makeRun({ status: LoopRunStatus.WaitingHuman });
    rerender({ run: parked });
    await flush();

    expect(result.current.run).toBe(parked);
    expect(getById).not.toHaveBeenCalled();
  });

  test("of the current run is not shown while the item still holds another run's", async () => {
    stageHub();
    stageReads();
    const { result } = renderHook(() =>
      useRunView("run-1", 0, { isCurrent: true, run: makeRun({ id: "run-0" }) }),
    );
    await flush();

    expect(result.current.run).toBeNull();
  });

  test("of another run is read once, and a burst of events while it is read costs one more read", async () => {
    const emit = stageHub();
    const { reads, getById } = stageReads();
    const { result } = renderHook(() => useRunView("run-1", 0, { isCurrent: false, run: null }));
    await flush();
    expect(reads).toHaveLength(1);

    for (const event of stateEvents) await emit(event, "run-1");
    await flush();
    expect(reads).toHaveLength(1);

    const read = makeRun({ status: LoopRunStatus.Completed });
    await act(async () => reads[0].resolve(read));
    await flush();
    expect(result.current.run).toBe(read);
    expect(reads).toHaveLength(2);

    await act(async () => reads[1].resolve(read));
    await flush();
    expect(getById).toHaveBeenCalledTimes(2);
  });

  test("of a run that stops being the current one keeps its last copy until its own read lands", async () => {
    stageHub();
    const { reads } = stageReads();
    const current = makeRun();
    const { result, rerender } = renderHook(
      ({ isCurrent }) => useRunView("run-1", 0, { isCurrent, run: isCurrent ? current : null }),
      { initialProps: { isCurrent: true } },
    );
    await flush();

    rerender({ isCurrent: false });
    await flush();
    expect(result.current.run).toBe(current);
    expect(reads).toHaveLength(1);

    const finished = makeRun({ status: LoopRunStatus.Completed });
    await act(async () => reads[0].resolve(finished));
    await flush();
    expect(result.current.run).toBe(finished);
  });
});
