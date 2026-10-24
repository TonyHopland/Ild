import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, render, renderHook, screen, waitFor } from "@testing-library/react";
import { useState, type ReactNode } from "react";
import { MemoryRouter } from "react-router";
import * as signalRHook from "./useSignalR";
import { WorkItemHubProvider } from "./useWorkItemHub";
import { useEditProposals } from "./useEditProposals";
import { useWorkItemDetail } from "../components/workitem-v2/useWorkItemDetail";
import LoggingSettings from "../pages/Settings/sections/LoggingSettings";
import NetworkSettings from "../pages/Settings/sections/NetworkSettings";
import GlobalVariables from "../pages/GlobalVariables";
import Schedules from "../pages/Chat/Schedules";
import {
  aiProviderService,
  chatScheduleService,
  globalVariableService,
  loggingService,
  loopRunService,
  loopTemplateService,
  NetworkSettingKeys,
  networkService,
  repositoryService,
  settingsService,
  workItemService,
} from "../services/auth";
import { fakeHub, reply, routeHubs, stubFetch, type FakeHub } from "../test-support";
import {
  WorkItemStatus,
  type ChatScheduleList,
  type NetworkLogEntry,
  type NetworkPolicyEntry,
  type WorkItem,
  type WorktreePreview,
} from "../types";

// Every page and hook that listens on the work-item hub does so on the one
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
  asked: (string | null | undefined)[];
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

/** Every work-item hub connection asked for: by its URL or by useSignalR's default. */
const workItemAsks = (asked: Hubs["asked"]) =>
  asked.filter((url) => url !== null && url !== "/hubs/chat" && url !== "/hubs/loop-run");

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

function underProvider(ui: ReactNode) {
  return render(
    <MemoryRouter>
      <WorkItemHubProvider>
        <Removable>{ui}</Removable>
      </WorkItemHubProvider>
    </MemoryRouter>,
  );
}

function hookWrapper({ children }: { children: ReactNode }) {
  return (
    <WorkItemHubProvider>
      <Removable>{children}</Removable>
    </WorkItemHubProvider>
  );
}

describe("Logging settings on the shared work-item hub", () => {
  test("a log line appended on the shared hub appears, and leaving the page takes its listener off", async () => {
    const { shared, asked } = hubs();
    vi.spyOn(loggingService, "getLevel").mockResolvedValue({
      level: "Information",
      startupLevel: "Information",
      isOverride: false,
    });
    vi.spyOn(loggingService, "getEntries").mockResolvedValue([]);

    underProvider(<LoggingSettings />);

    await waitFor(() =>
      expect(attached(shared, ["LogEntryAppended"])).toEqual(["LogEntryAppended"]),
    );
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);

    await shared.emit("LogEntryAppended", {
      id: 9,
      timestamp: "2026-09-03T09:00:00Z",
      level: "Warning",
      source: "ILD.Core.Remote.PrStatusPoller",
      message: "Rate limit at 12% remaining",
      detail: null,
    });
    expect(await screen.findByText("Rate limit at 12% remaining")).toBeTruthy();

    removeConsumer();
    expect(attached(shared, ["LogEntryAppended"])).toEqual([]);
  });
});

describe("Network settings on the shared work-item hub", () => {
  const events = ["NetworkPolicyChanged", "NetworkLogAppended", "NetworkLogCleared"];
  const github: NetworkPolicyEntry = {
    id: "e1",
    host: ".github.com",
    listKind: "Whitelist",
    aiProviderId: null,
    createdAt: "2026-09-01T10:00:00Z",
  };
  const npm: NetworkPolicyEntry = { ...github, id: "e2", host: "registry.npmjs.org" };
  const line = (id: string, host: string): NetworkLogEntry => ({
    id,
    host,
    port: 443,
    timestamp: "2026-09-02T12:00:00Z",
    decision: "Blocked",
    aiProviderId: null,
  });

  test("policy changes and log lines on the shared hub reach the page, and leaving it takes all three listeners off", async () => {
    const { shared, asked } = hubs();
    let entries = [github];
    let log = [line("l1", "first.example")];
    vi.spyOn(networkService, "getStatus").mockResolvedValue({
      enforcement: "enforced",
      reason: "",
      proxyEnabled: true,
      proxyPort: 3128,
    });
    const getEntries = vi
      .spyOn(networkService, "getEntries")
      .mockImplementation(async () => entries);
    vi.spyOn(networkService, "getForwards").mockResolvedValue([]);
    const getLog = vi.spyOn(networkService, "getLog").mockImplementation(async () => log);
    vi.spyOn(settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === NetworkSettingKeys.Mode ? "off" : "30",
    }));
    vi.spyOn(aiProviderService, "getAll").mockResolvedValue([]);

    underProvider(<NetworkSettings />);

    expect(await screen.findByText("first.example")).toBeTruthy();
    expect(attached(shared, events)).toEqual(events);
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);

    await shared.emit("NetworkLogAppended", line("l2", "appended.example"));
    expect(await screen.findByText("appended.example")).toBeTruthy();

    entries = [github, npm];
    const entryReads = getEntries.mock.calls.length;
    await shared.emit("NetworkPolicyChanged", {});
    await waitFor(() => expect(getEntries.mock.calls.length).toBeGreaterThan(entryReads));
    expect(await screen.findByText("registry.npmjs.org")).toBeTruthy();

    log = [line("l3", "survivor.example")];
    const logReads = getLog.mock.calls.length;
    await shared.emit("NetworkLogCleared", {});
    await waitFor(() => expect(getLog.mock.calls.length).toBeGreaterThan(logReads));
    expect(await screen.findByText("survivor.example")).toBeTruthy();

    removeConsumer();
    expect(attached(shared, events)).toEqual([]);
  });
});

describe("Global Variables on the shared work-item hub", () => {
  test("joins the board on the shared hub and re-reads on connect, on reconnect and on a hint", async () => {
    const { shared, asked } = hubs("connecting");
    const getAll = vi.spyOn(globalVariableService, "getAll").mockResolvedValue([]);
    vi.spyOn(globalVariableService, "listProposals").mockResolvedValue([]);
    vi.spyOn(settingsService, "get").mockRejectedValue(new Error("not stubbed"));
    const page = () => (
      <MemoryRouter>
        <WorkItemHubProvider>
          <GlobalVariables />
        </WorkItemHubProvider>
      </MemoryRouter>
    );

    const { rerender } = render(page());
    await waitFor(() => expect(getAll).toHaveBeenCalledTimes(1));
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);
    expect(attached(shared, ["GlobalVariableProposalsChanged"])).toEqual([
      "GlobalVariableProposalsChanged",
    ]);

    shared.setState("connected");
    rerender(page());
    await waitFor(() => expect(getAll).toHaveBeenCalledTimes(2));
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");

    shared.invoke.mockClear();
    shared.setState("reconnecting");
    rerender(page());
    shared.setState("connected");
    rerender(page());
    await waitFor(() => expect(getAll).toHaveBeenCalledTimes(3));
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");

    await shared.emit("GlobalVariableProposalsChanged", {});
    await waitFor(() => expect(getAll).toHaveBeenCalledTimes(4));
  });
});

describe("Chat schedules on the shared work-item hub", () => {
  test("a scheduler pause on the shared hub re-reads the list, while the chat inbox stays on the chat hub", async () => {
    const { shared, chat, asked } = hubs();
    let list: ChatScheduleList = { schedulerPaused: false, schedules: [] };
    const read = vi.spyOn(chatScheduleService, "list").mockImplementation(async () => list);
    vi.spyOn(repositoryService, "getEvery").mockResolvedValue([]);
    vi.spyOn(aiProviderService, "getAll").mockResolvedValue([]);

    underProvider(<Schedules />);

    await waitFor(() => expect(chat.invoke).toHaveBeenCalledWith("SubscribeToChatInbox"));
    expect(attached(shared, ["SchedulerStateChanged"])).toEqual(["SchedulerStateChanged"]);
    expect(attached(chat, ["ChatSchedulesChanged"])).toEqual(["ChatSchedulesChanged"]);
    expect(attached(chat, ["SchedulerStateChanged"])).toEqual([]);
    expect(workItemAsks(asked)).toEqual(["/hubs/work-item"]);
    expect(asked).toContain("/hubs/chat");
    expect(screen.queryByText(/The scheduler is paused/)).toBeNull();

    list = { schedulerPaused: true, schedules: [] };
    const reads = read.mock.calls.length;
    await shared.emit("SchedulerStateChanged", { isPaused: true, maxConcurrent: 5 });
    await waitFor(() => expect(read.mock.calls.length).toBeGreaterThan(reads));
    expect(await screen.findByText(/The scheduler is paused/)).toBeTruthy();

    removeConsumer();
    expect(attached(shared, ["SchedulerStateChanged"])).toEqual([]);
    expect(chat.invoke).toHaveBeenCalledWith("UnsubscribeFromChatInbox");
  });
});

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
