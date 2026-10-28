import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { useState, type ReactNode } from "react";
import * as signalRHook from "./useSignalR";
import { WorkItemHubProvider } from "./useWorkItemHub";
import { useEditProposals } from "./useEditProposals";
import { useWorkItemDetail } from "../components/workitem-v2/useWorkItemDetail";
import {
  aiProviderService,
  loopRunService,
  loopTemplateService,
  repositoryService,
  workItemService,
} from "../services/auth";
import { fakeHub, reply, routeHubs, stubFetch, type FakeHub } from "../test-support";
import { WorkItemStatus, type WorkItem, type WorktreePreview } from "../types";

// Every hook that listens on the work-item hub does so on the one
// connection WorkItemHubProvider holds: it asks for no work-item connection of
// its own, live events on the shared hub reach it, and since that connection
// outlives the consumer, unmounting the consumer takes its handlers off it.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

interface Hubs {
  shared: FakeHub;
  chat: FakeHub;
  loopRun: FakeHub;
  asked: (string | null)[];
}

function hubs(sharedState: FakeHub["connection"]["connectionState"] = "connected"): Hubs {
  const shared = fakeHub(sharedState);
  const chat = fakeHub();
  const loopRun = fakeHub();
  const asked = routeHubs(signalRHook, {
    "/hubs/work-item": shared,
    "/hubs/chat": chat,
    "/hubs/loop-run": loopRun,
  });
  stubFetch(() => reply(404, { error: "not stubbed" }));
  return { shared, chat, loopRun, asked };
}

/** Every work-item hub connection asked for. */
const workItemAsks = (asked: Hubs["asked"]) => asked.filter((url) => url === "/hubs/work-item");

/** The events among `events` that still have a handler on `hub`. */
const attached = (hub: FakeHub, events: string[]) =>
  events.filter((event) => (hub.handlers[event]?.size ?? 0) > 0);

let removeConsumer: () => void = () => {};

/** Holds a consumer the test can unmount while the provider around it stays. */
function Removable({ children }: { children: ReactNode }) {
  const [shown, setShown] = useState(true);
  removeConsumer = () => act(() => setShown(false));
  return shown ? children : null;
}

function hookWrapper({ children }: { children: ReactNode }) {
  return (
    <WorkItemHubProvider>
      <Removable>{children}</Removable>
    </WorkItemHubProvider>
  );
}

describe("the work item detail on the shared work-item hub", () => {
  test("a PreviewStateChanged on the shared hub re-reads the preview; the run stream stays on the loop-run hub", async () => {
    const { shared, asked } = hubs();
    vi.spyOn(repositoryService, "getAll").mockResolvedValue([]);
    vi.spyOn(loopTemplateService, "getAll").mockResolvedValue([]);
    vi.spyOn(aiProviderService, "getAll").mockResolvedValue([]);
    vi.spyOn(workItemService, "getRuns").mockResolvedValue([]);
    vi.spyOn(workItemService, "getDependencies").mockResolvedValue([]);
    vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
    vi.spyOn(loopRunService, "getById").mockRejectedValue(new Error("not stubbed"));
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
    const getPreview = vi
      .spyOn(workItemService, "getPreview")
      .mockResolvedValueOnce(preview("stopped"))
      .mockResolvedValue(preview("running"));
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

    const { result } = renderHook(() => useWorkItemDetail(workItem, vi.fn()), {
      wrapper: hookWrapper,
    });
    await waitFor(() => expect(result.current.preview?.state).toBe("stopped"));
    expect(attached(shared, ["PreviewStateChanged"])).toEqual(["PreviewStateChanged"]);
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);
    expect(asked).toContain("/hubs/loop-run");

    await shared.emit("PreviewStateChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(result.current.preview?.state).toBe("running"));
    expect(getPreview).toHaveBeenCalledTimes(2);

    removeConsumer();
    expect(attached(shared, ["PreviewStateChanged"])).toEqual([]);
  });
});

describe("edit proposals on the shared work-item hub", () => {
  test("a work item's proposals join and read on the shared hub, on connect and reconnect and on a hint, and unmounting takes the listener off", async () => {
    const { shared, chat, asked } = hubs("connecting");
    const read = vi.spyOn(workItemService, "listEditProposals").mockResolvedValue([]);

    const { rerender } = renderHook(() => useEditProposals({ workItemId: "wi-1" }), {
      wrapper: hookWrapper,
    });
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);
    expect(asked).not.toContain("/hubs/chat");
    expect(attached(shared, ["WorkItemEditProposalsChanged"])).toEqual([
      "WorkItemEditProposalsChanged",
    ]);
    expect(read).not.toHaveBeenCalled();

    shared.setState("connected");
    rerender();
    await waitFor(() => expect(read).toHaveBeenCalledTimes(1));
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");

    shared.invoke.mockClear();
    shared.setState("reconnecting");
    rerender();
    shared.setState("connected");
    rerender();
    await waitFor(() => expect(read).toHaveBeenCalledTimes(2));
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");

    await shared.emit("WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(read).toHaveBeenCalledTimes(3));
    expect(chat.invoke).not.toHaveBeenCalled();

    removeConsumer();
    expect(attached(shared, ["WorkItemEditProposalsChanged"])).toEqual([]);
  });

  test("a chat's proposals still join and leave that chat's group on a chat hub of their own", async () => {
    const { shared, chat, asked } = hubs();
    const read = vi.spyOn(workItemService, "listEditProposalsFor").mockResolvedValue([]);

    renderHook(() => useEditProposals({ chatSessionId: "c-1" }), { wrapper: hookWrapper });
    await waitFor(() => expect(read).toHaveBeenCalledWith({ chatSessionId: "c-1" }));
    expect(chat.invoke).toHaveBeenCalledWith("SubscribeToChat", "c-1");
    expect(asked).toContain("/hubs/chat");
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);
    expect(attached(chat, ["ChatEditProposalsChanged"])).toEqual(["ChatEditProposalsChanged"]);
    expect(attached(shared, ["ChatEditProposalsChanged", "WorkItemEditProposalsChanged"])).toEqual(
      [],
    );

    removeConsumer();
    expect(chat.invoke).toHaveBeenCalledWith("UnsubscribeFromChat", "c-1");
    expect(attached(chat, ["ChatEditProposalsChanged"])).toEqual([]);
  });
});
