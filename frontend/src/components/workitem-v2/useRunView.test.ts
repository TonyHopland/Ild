// @vitest-environment jsdom
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup } from "@testing-library/react";
import { useRunView } from "./useRunView";
import { LoopRun, LoopRunStatus } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import { loopRunService } from "../../services/auth";
import { deferred, fakeHub, flushPromises, routeEveryHub } from "../../test-support";

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

/** Answers every hub with one fake; returns how a test delivers a run event to it. */
function stageHub() {
  const hub = fakeHub();
  routeEveryHub(signalRHook, hub);
  return (event: string, runId: string) => hub.emit(event, { runId });
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
    await flushPromises();
    expect(result.current.run).toBe(first);

    for (const event of stateEvents) await emit(event, "run-1");
    const parked = makeRun({ status: LoopRunStatus.WaitingHuman });
    rerender({ run: parked });
    await flushPromises();

    expect(result.current.run).toBe(parked);
    expect(getById).not.toHaveBeenCalled();
  });

  test("of the current run is not shown while the item still holds another run's", async () => {
    stageHub();
    stageReads();
    const { result } = renderHook(() =>
      useRunView("run-1", 0, { isCurrent: true, run: makeRun({ id: "run-0" }) }),
    );
    await flushPromises();

    expect(result.current.run).toBeNull();
  });

  test("of another run is read once, and a burst of events while it is read costs one more read", async () => {
    const emit = stageHub();
    const { reads, getById } = stageReads();
    const { result } = renderHook(() => useRunView("run-1", 0, { isCurrent: false, run: null }));
    await flushPromises();
    expect(reads).toHaveLength(1);

    for (const event of stateEvents) await emit(event, "run-1");
    await flushPromises();
    expect(reads).toHaveLength(1);

    const read = makeRun({ status: LoopRunStatus.Completed });
    await act(async () => reads[0].resolve(read));
    await flushPromises();
    expect(result.current.run).toBe(read);
    expect(reads).toHaveLength(2);

    await act(async () => reads[1].resolve(read));
    await flushPromises();
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
    await flushPromises();

    rerender({ isCurrent: false });
    await flushPromises();
    expect(result.current.run).toBe(current);
    expect(reads).toHaveLength(1);

    const finished = makeRun({ status: LoopRunStatus.Completed });
    await act(async () => reads[0].resolve(finished));
    await flushPromises();
    expect(result.current.run).toBe(finished);
  });
});
