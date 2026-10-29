// @vitest-environment jsdom
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup } from "@testing-library/react";
import { useWorkItemDetail } from "./useWorkItemDetail";
import { WorkItem, WorkItemStatus, WorktreePreview } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import {
  repositoryService,
  loopTemplateService,
  workItemService,
  aiProviderService,
} from "../../services/auth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";
import { fakeHub, routeEveryHub } from "../../test-support";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const workItem: WorkItem = {
  id: "wi-1",
  title: "Test",
  description: "",
  status: WorkItemStatus.Running,
  priority: 0,
  tags: [],
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
  worktreePath: "/tmp/wt",
  dependencyIds: [],
  dependentIds: [],
};

const preview = (state: string): WorktreePreview => ({
  configured: true,
  state,
  worktreePath: "/tmp/wt",
  configPath: null,
  profileName: null,
  publicHost: null,
  stateDirectory: null,
  message: null,
  services: [],
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const flush = () =>
  act(async () => {
    for (let i = 0; i < 5; i++) await Promise.resolve();
  });

async function mountWithStoppedPreview() {
  vi.spyOn(repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
  const hub = fakeHub();
  hub.invoke.mockImplementation(() => Promise.resolve({ text: "", lastSeq: 0 } as unknown as void));
  routeEveryHub(signalRHook, hub);
  const getPreview = vi.spyOn(workItemService, "getPreview");
  getPreview.mockResolvedValueOnce(preview("stopped"));
  const hook = renderHook(() => useWorkItemDetail(workItem, vi.fn()), {
    wrapper: WorkItemHubProvider,
  });
  await flush();
  expect(hook.result.current.preview?.state).toBe("stopped");
  const changed = () => hub.dispatch("PreviewStateChanged", { workItemId: "wi-1" });
  return { ...hook, getPreview, changed };
}

describe("useWorkItemDetail preview start progress", () => {
  test("a progress event during a start re-reads quietly and the busy flag stays the start's own", async () => {
    const { result, getPreview, changed } = await mountWithStoppedPreview();
    const start = deferred<WorktreePreview>();
    vi.spyOn(workItemService, "startPreview").mockReturnValue(start.promise);
    const read = deferred<WorktreePreview>();
    getPreview.mockReturnValueOnce(read.promise);

    let startCall!: Promise<unknown>;
    act(() => {
      startCall = result.current.handleStartPreview();
    });
    await flush();
    expect(result.current.previewStarting).toBe(true);

    act(() => changed());
    await flush();
    expect(result.current.previewLoading).toBe(false);

    await act(async () => {
      read.resolve(preview("installing"));
    });
    await flush();
    expect(result.current.preview?.state).toBe("installing");
    expect(result.current.previewLoading).toBe(false);
    expect(result.current.previewStarting).toBe(true);

    await act(async () => {
      start.resolve(preview("running"));
      await startCall;
    });
    expect(result.current.previewStarting).toBe(false);
    expect(result.current.preview?.state).toBe("running");
  });

  test("a re-read that began before the start settled does not overwrite the start's answer", async () => {
    const { result, getPreview, changed } = await mountWithStoppedPreview();
    const start = deferred<WorktreePreview>();
    vi.spyOn(workItemService, "startPreview").mockReturnValue(start.promise);
    const lateRead = deferred<WorktreePreview>();
    getPreview.mockReturnValueOnce(lateRead.promise);

    let startCall!: Promise<unknown>;
    act(() => {
      startCall = result.current.handleStartPreview();
    });
    await flush();
    act(() => changed());
    await flush();

    await act(async () => {
      start.resolve(preview("running"));
      await startCall;
    });
    await act(async () => {
      lateRead.resolve(preview("starting"));
    });
    await flush();

    expect(result.current.preview?.state).toBe("running");
  });

  test("an older re-read never overwrites a newer one", async () => {
    const { result, getPreview, changed } = await mountWithStoppedPreview();
    const first = deferred<WorktreePreview>();
    const second = deferred<WorktreePreview>();
    getPreview.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    act(() => changed());
    act(() => changed());
    await flush();

    await act(async () => {
      second.resolve(preview("running"));
    });
    await act(async () => {
      first.resolve(preview("starting"));
    });
    await flush();

    expect(result.current.preview?.state).toBe("running");
  });

  test("a failed start ends on the server's final snapshot even when the event's re-read finished first", async () => {
    const { result, getPreview, changed } = await mountWithStoppedPreview();
    const start = deferred<WorktreePreview>();
    vi.spyOn(workItemService, "startPreview").mockReturnValue(start.promise);
    // Every re-read after the event answers with what the server holds once the
    // start has failed; the one the event triggers resolves only after the rejection.
    const eventRead = deferred<WorktreePreview>();
    getPreview.mockReturnValueOnce(eventRead.promise).mockResolvedValue(preview("failed"));

    let startCall!: Promise<unknown>;
    act(() => {
      startCall = result.current.handleStartPreview();
    });
    await flush();
    act(() => changed());
    await flush();

    await act(async () => {
      start.reject(new Error("web exited with code 1"));
      await startCall.catch(() => {});
    });
    await act(async () => {
      eventRead.resolve(preview("failed"));
    });
    await flush();

    expect(result.current.previewStarting).toBe(false);
    expect(result.current.preview?.state).toBe("failed");
  });

  test.each(["stop", "startService", "stopService"] as const)(
    "a failed %s is followed by a re-read whose result applies",
    async (write) => {
      const { result, getPreview } = await mountWithStoppedPreview();
      const refused = new Error("refused");
      vi.spyOn(workItemService, "stopPreview").mockRejectedValue(refused);
      vi.spyOn(workItemService, "startPreviewService").mockRejectedValue(refused);
      vi.spyOn(workItemService, "stopPreviewService").mockRejectedValue(refused);
      getPreview.mockResolvedValue(preview("failed"));

      await act(async () => {
        if (write === "stop") await result.current.handleStopPreview();
        else if (write === "startService") await result.current.handleStartService("web", {});
        else await result.current.handleStopService("web");
      });
      await flush();

      expect(result.current.preview?.state).toBe("failed");
    },
  );
});
