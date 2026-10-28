import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, within, cleanup, waitFor, act, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route, useLocation } from "react-router";
import Taskboard from "./index";
import { foldedColumnKey } from "../../hooks/useFoldedColumns";
import {
  PreferenceKeys,
  clearPreferences,
  getPreference,
  seedPreferences,
  setPreference,
} from "../../utils/userPreferences";
import { mockTaskboardServer, pressEscapeUntil, signalRHub } from "../../test-support";
import { NodeType, WorkItemStatus, WorkItem, Repository, LoopTemplate } from "../../types";
import { stageParkedNode } from "../../test-support.parkedNode";
import * as authServices from "../../services/auth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);

afterEach(() => {
  vi.useRealTimers();
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

beforeEach(() => {
  signalRHub.reset();
  localStorage.clear();
  // Done starts folded; these tests read the cards in every column, so this
  // user has opened it.
  seedPreferences({ [foldedColumnKey(WorkItemStatus.Done)]: "false" });
});

function LocationDisplay() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

// The taskboard reads the open work item from the URL, so tests mount it behind
// the same two routes the app wires up (bare board + per-item deep link). The
// location probe lets a test assert the URL matches the open item.
function renderTaskboard(initialPath = "/taskboard") {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Routes>
        <Route path="/taskboard" element={<Taskboard />} />
        <Route path="/taskboard/:workItemId" element={<Taskboard />} />
      </Routes>
      <LocationDisplay />
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
}

// Mocks the work item detail dialog's own data fetches so it can render.
function mockModalServices() {
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Item",
    description: "desc",
    status: WorkItemStatus.Ready,
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
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function makeTemplate(overrides: Partial<LoopTemplate> = {}): LoopTemplate {
  return {
    id: "tmpl-1",
    name: "Bug Fix",
    description: "",
    version: 1,
    recoveryPolicy: {} as LoopTemplate["recoveryPolicy"],
    nodes: [],
    edges: [],
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
    ...overrides,
  };
}

function makeRepo(overrides: Partial<Repository> = {}): Repository {
  return {
    id: "repo-1",
    name: "Repo One",
    remoteProviderId: "rp-1",
    cloneUrl: "https://example.com/repo.git",
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: WorkItemStatus.Backlog,
    createdAt: "2025-01-01T00:00:00Z",
    ...overrides,
  };
}

const COLUMN_TITLES = ["Backlog", "Work queue", "Running", "Needs you", "Waiting for ILD", "Done"];

/** The board's search debounce, as Taskboard/index.tsx holds it. */
const SEARCH_DEBOUNCE_MS = 300;

const byText = (a: string, b: string) => a.localeCompare(b);

/** The statuses each board column reads, one entry per column, sorted. */
const COLUMN_STATUSES = [
  [WorkItemStatus.Backlog],
  [WorkItemStatus.WorkQueue, WorkItemStatus.Ready],
  [WorkItemStatus.Running],
  [WorkItemStatus.HumanFeedback],
  [WorkItemStatus.WaitingForIld],
  [WorkItemStatus.Done],
]
  .map((statuses) => [...statuses].sort(byText).join("+"))
  .sort(byText);

/** The statuses each of these page reads asked for, one entry per read, sorted. */
function statusSets(queries: { statuses: string[] }[]): string[] {
  return queries.map((q) => [...q.statuses].sort(byText).join("+")).sort(byText);
}

/** Item `id` created `n` hours into 2026; a higher `n` is newer and higher on the board. The title is the id. */
function boardItem(
  id: string,
  n: number,
  status: WorkItemStatus = WorkItemStatus.Backlog,
  overrides: Partial<WorkItem> = {},
): WorkItem {
  return makeItem({
    id,
    title: id,
    status,
    priority: -n,
    createdAt: new Date(Date.UTC(2026, 0, 1) + n * 3_600_000).toISOString(),
    ...overrides,
  });
}

function columnEl(label: string): HTMLElement {
  const column = Array.from(document.querySelectorAll<HTMLElement>(".taskboard-column")).find(
    (c) => c.querySelector(".taskboard-column-title")?.textContent === label,
  );
  if (!column) throw new Error(`no ${label} column`);
  return column;
}

/** The card titles of a column, top to bottom. */
function cardTitles(label: string): string[] {
  return Array.from(columnEl(label).querySelectorAll(".work-item-card")).map(
    (card) => card.getAttribute("aria-label")!.split(", status ")[0],
  );
}

function badge(label: string): string | null | undefined {
  return columnEl(label).querySelector(".taskboard-column-count")?.textContent;
}

/** Board mocks around a fake server whose getById and transition read and write its items. */
function mockBoard(items: WorkItem[], repositories: Repository[] = [makeRepo()]) {
  const server = mockTaskboardServer(items);
  vi.spyOn(authServices.workItemService, "getById").mockImplementation(async (id: string) => {
    const found = server.items.find((wi) => wi.id === id);
    if (!found) throw new Error("not found");
    return { ...found };
  });
  vi.spyOn(authServices.workItemService, "transition").mockImplementation(
    async (id: string, status: string) => {
      const found = server.items.find((wi) => wi.id === id)!;
      found.status = status as WorkItemStatus;
    },
  );
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue(repositories);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
    key: "scheduler.isPaused",
    value: "false",
  });
  return server;
}

/** Runs the board's timers for `ms` of fake time, with the promise callbacks they cause. */
async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

/** Lets pending promise callbacks and the renders they cause run. */
async function settle() {
  for (let i = 0; i < 5; i++) {
    await act(async () => {
      await Promise.resolve();
    });
  }
}

describe("Taskboard SignalR", () => {
  test("updates work item when HumanFeedbackRequired event arrives", async () => {
    mockTaskboardServer([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await signalRHub.emit("HumanFeedbackRequired", {
      workItemId: "wi-1",
      reason: "PR Awaiting Merge",
    });

    await waitFor(() => {
      expect(screen.getByText("PR awaiting merge")).toBeTruthy();
    });
  });

  test("reconciles board item from server after WorkItemStateChanged event", async () => {
    mockTaskboardServer([makeItem({ status: WorkItemStatus.Running })]);
    const getByIdSpy = vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
      }),
    );

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "wi-1",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-1");
      expect(screen.getByText("Human input needed")).toBeTruthy();
    });
  });

  test("adds a newly created item going Running->HumanFeedback when not yet on the board", async () => {
    // Board starts empty — the item was created after the page loaded.
    mockTaskboardServer([]);
    const getByIdSpy = vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeItem({
        id: "wi-new",
        title: "Fresh Item",
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
      }),
    );

    renderTaskboard();

    await waitFor(() => {
      expect(signalRHub.handlers["WorkItemStateChanged"]?.size).toBeGreaterThan(0);
    });

    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "wi-new",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-new");
      expect(screen.getByText("Fresh Item")).toBeTruthy();
      expect(screen.getByText("Human input needed")).toBeTruthy();
    });
  });

  test("adds a newly created item on HumanFeedbackRequired when not yet on the board", async () => {
    mockTaskboardServer([]);
    const getByIdSpy = vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeItem({
        id: "wi-new",
        title: "Fresh Item",
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
      }),
    );

    renderTaskboard();

    await waitFor(() => {
      expect(signalRHub.handlers["HumanFeedbackRequired"]?.size).toBeGreaterThan(0);
    });

    await signalRHub.emit("HumanFeedbackRequired", {
      workItemId: "wi-new",
      reason: "Human Input Needed",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-new");
      expect(screen.getByText("Fresh Item")).toBeTruthy();
    });
  });

  test("a stale late getById does not revert a fresher status", async () => {
    // Newly created items get a rapid burst of work-item-hub events
    // (create -> claim -> run-progressed -> human). Each fires a getById; an
    // early fetch the server answered with an older status can resolve *after*
    // a later one and clobber it. This pins that the late stale write loses.
    mockTaskboardServer([makeItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1" })]);

    const staleRunning = makeItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1" });
    const freshHuman = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
    });

    // First getById (issued by the earlier RunProgressed event) is held open so
    // it resolves last; every later fetch returns the fresh HumanFeedback view.
    const stale = deferred<WorkItem>();
    let calls = 0;
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() => {
      calls += 1;
      return calls === 1 ? stale.promise : Promise.resolve(freshHuman);
    });

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    // Burst: the run advances a node, then immediately parks for human input.
    await signalRHub.emit("WorkItemRunProgressed", { workItemId: "wi-1" });
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "wi-1",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    await waitFor(() => {
      expect(screen.getByText("Human input needed")).toBeTruthy();
    });

    // The earlier fetch finally returns its stale Running snapshot.
    await act(async () => {
      stale.resolve(staleRunning);
    });

    // It must not revert the card back to Running.
    expect(screen.queryByText("Human input needed")).toBeTruthy();
  });

  test("refreshes a running card's current step on WorkItemRunProgressed", async () => {
    mockTaskboardServer([
      makeItem({
        status: WorkItemStatus.Running,
        startedAt: "2025-01-01T00:00:00Z",
        currentLoopRunId: "run-1",
        currentNodeLabel: "plan",
      }),
    ]);
    const getByIdSpy = vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeItem({
        status: WorkItemStatus.Running,
        startedAt: "2025-01-01T00:00:00Z",
        currentLoopRunId: "run-1",
        currentNodeLabel: "implement",
      }),
    );

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("plan")).toBeTruthy();
    });

    await signalRHub.emit("WorkItemRunProgressed", {
      workItemId: "wi-1",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-1");
      expect(screen.getByText("implement")).toBeTruthy();
    });
  });

  test("normalizes numeric status from a WorkItemStateChanged event", async () => {
    mockTaskboardServer([makeItem({ status: WorkItemStatus.HumanFeedback })]);
    // SignalR delivers the enum as its numeric value, and the follow-up refetch
    // is irrelevant to what the event itself writes into state — fail it so the
    // assertion observes only the normalized event payload.
    vi.spyOn(authServices.workItemService, "getById").mockRejectedValue(new Error("offline"));

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    // 6 is the wire value for Done (RemoteWorkItemStatus.Done), mirroring an
    // approve moving the item out of HumanFeedback. A raw number would crash
    // status rendering and never match a column; the card should land in the
    // Done column instead.
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "wi-1",
      oldStatus: 4,
      newStatus: 6,
    });

    await waitFor(() => {
      expect(screen.getByLabelText(/Test Item, status Done/)).toBeTruthy();
    });
  });

  test("fires browser notification when HumanFeedbackRequired event arrives", async () => {
    const notificationCalls: Array<[string, NotificationOptions]> = [];
    class MockNotification {
      static permission = "granted";
      constructor(title: string, options?: NotificationOptions) {
        notificationCalls.push([title, options!]);
      }
    }
    vi.stubGlobal("Notification", MockNotification);

    mockTaskboardServer([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await signalRHub.emit("HumanFeedbackRequired", {
      workItemId: "wi-1",
      reason: "Node Failed",
    });

    await waitFor(() => {
      expect(notificationCalls).toHaveLength(1);
      expect(notificationCalls[0][0]).toBe("Work Item Needs Attention");
      expect(notificationCalls[0][1].body).toBe("Node Failed");
    });
  });

  test("skips notification when notifications disabled in settings", async () => {
    setPreference(PreferenceKeys.NotificationsEnabled, "false");

    const notificationCalls: Array<[string, NotificationOptions]> = [];
    class MockNotification {
      static permission = "granted";
      constructor(title: string, options?: NotificationOptions) {
        notificationCalls.push([title, options!]);
      }
    }
    vi.stubGlobal("Notification", MockNotification);

    mockTaskboardServer([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await signalRHub.emit("HumanFeedbackRequired", {
      workItemId: "wi-1",
      reason: "Node Failed",
    });

    expect(notificationCalls).toHaveLength(0);
  });
});

describe("Taskboard reconnect", () => {
  // The work-item hub buffers nothing on resubscribe, so the board can only
  // recover events missed during an outage by re-fetching when the socket
  // returns. These tests drive connectionState through a drop-and-recover cycle.
  function taskboardTree() {
    return (
      <MemoryRouter initialEntries={["/taskboard"]}>
        <Routes>
          <Route path="/taskboard" element={<Taskboard />} />
          <Route path="/taskboard/:workItemId" element={<Taskboard />} />
        </Routes>
        <LocationDisplay />
      </MemoryRouter>
    );
  }

  test("a reconnect reloads each column's loaded window from the top and the tags", async () => {
    const server = mockTaskboardServer([
      ...Array.from({ length: 45 }, (_, n) => boardItem(`b${n}`, n)),
      boardItem("r0", 0, WorkItemStatus.Ready),
    ]);
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: "false",
    });

    const { rerender } = render(taskboardTree(), { wrapper: WorkItemHubProvider });

    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    fireEvent.click(within(columnEl("Backlog")).getByRole("button", { name: "Load more" }));
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(40));
    const pagesBefore = server.getPage.mock.calls.length;
    const tagsBefore = server.getTags.mock.calls.length;

    // While the socket is down another client creates an item; its event is lost.
    server.items.push(boardItem("made-during-outage", 100));
    signalRHub.setState("reconnecting");
    await act(async () => {
      rerender(taskboardTree());
    });
    expect(server.getPage.mock.calls.length).toBe(pagesBefore);

    signalRHub.setState("connected");
    await act(async () => {
      rerender(taskboardTree());
    });

    await waitFor(() => expect(cardTitles("Backlog")[0]).toBe("made-during-outage"));
    expect(badge("Backlog")).toBe("46");
    const reload = server.getPage.mock.calls.slice(pagesBefore).map(([q]) => q);
    expect(statusSets(reload)).toEqual(COLUMN_STATUSES);
    for (const q of reload) {
      expect(q.skip).toBe(0);
      expect(q.take).toBe(q.statuses.includes(WorkItemStatus.Backlog) ? 40 : 20);
    }
    expect(server.getTags.mock.calls.length).toBeGreaterThan(tagsBefore);
    expect(server.getAll).not.toHaveBeenCalled();
  });

  test("does not re-fetch on the very first connect", async () => {
    // A board that mounts while already connected must load exactly once: the
    // mount effect's load, with no extra fetch from the connect transition.
    signalRHub.setState("disconnected");

    const server = mockTaskboardServer([makeItem()]);
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: "false",
    });

    const { rerender } = render(taskboardTree(), { wrapper: WorkItemHubProvider });

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    // The socket finishes its initial handshake.
    signalRHub.setState("connected");
    await act(async () => {
      rerender(taskboardTree());
    });

    // Still a single first page per column — the first arrival at "connected"
    // is not a recovery.
    await waitFor(() => {
      expect(statusSets(server.getPage.mock.calls.map(([q]) => q))).toEqual(COLUMN_STATUSES);
    });
  });
});

describe("Taskboard reconnect with a work item open", () => {
  test("the dialog of an item opened by link and not on the board shows its current state after a reconnect", async () => {
    mockModalServices();
    mockTaskboardServer([]);
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: "false",
    });
    vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
    vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation(async (runId) => ({
      runId,
      messages: [],
      lastEventId: null,
    }));
    vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
      entries: [],
      nextCursor: 0,
      hasMore: false,
    });
    stageParkedNode({ type: NodeType.PR });
    let onServer = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "PR Awaiting Merge",
      currentLoopRunId: "run-1",
      latestLoopRunId: "run-1",
    });
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(async () => ({
      ...onServer,
    }));
    const tree = () => (
      <MemoryRouter initialEntries={["/taskboard/wi-1"]}>
        <Routes>
          <Route path="/taskboard" element={<Taskboard />} />
          <Route path="/taskboard/:workItemId" element={<Taskboard />} />
        </Routes>
      </MemoryRouter>
    );
    const feedbackBox = () => document.querySelector(".wiv2-feedback");

    const { rerender } = render(tree(), { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(feedbackBox()?.textContent).toContain("PR feedback"));

    // The phone sleeps; the hub drops, and the run moves on meanwhile.
    signalRHub.setState("disconnected");
    await act(async () => {
      rerender(tree());
    });
    onServer = { ...onServer, status: WorkItemStatus.Running, humanFeedbackReason: null };
    signalRHub.setState("connected");
    await act(async () => {
      rerender(tree());
    });

    await waitFor(() => expect(feedbackBox()).toBeNull());
    expect(screen.getByRole("dialog")).toBeTruthy();
  });
});

describe("Taskboard keyboard navigation", () => {
  test("ArrowRight on a focused card transitions to the next column", async () => {
    const transitionSpy = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined as unknown as void);
    mockTaskboardServer([makeItem({ status: WorkItemStatus.Ready })]);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeItem({ status: WorkItemStatus.Running }),
    );

    renderTaskboard();

    const card = await screen.findByRole("button", { name: /Test Item/i });
    card.focus();
    const { fireEvent } = await import("@testing-library/react");
    fireEvent.keyDown(card, { key: "ArrowRight" });

    await waitFor(() => {
      expect(transitionSpy).toHaveBeenCalledWith("wi-1", WorkItemStatus.Running);
    });

    // aria-live region should announce the move
    const live = await screen.findByRole("status");
    expect(live.textContent).toContain("Running");
  });
});

describe("Taskboard editing item refetch", () => {
  test("a state change re-reads the item at once and again after 500 ms, to catch data saved after the event", async () => {
    const reads = [
      makeItem({ status: WorkItemStatus.HumanFeedback, humanFeedbackReason: "Human Input Needed" }),
      makeItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
        title: "Saved later",
      }),
    ];
    const server = mockTaskboardServer([makeItem({ status: WorkItemStatus.Running })]);
    const getById = vi
      .spyOn(authServices.workItemService, "getById")
      .mockImplementation(async () => reads.shift() ?? server.items[0]);

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    renderTaskboard();
    await advance(0);
    expect(screen.getByText("Test Item")).toBeTruthy();

    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "wi-1",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });
    await advance(499);
    expect(getById).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Saved later")).toBeNull();

    await advance(1);
    expect(getById).toHaveBeenCalledTimes(2);
    expect(screen.getByText("Saved later")).toBeTruthy();
  });
});

describe("Taskboard work item URL", () => {
  test("opens the detail dialog for the work item id in the URL", async () => {
    mockModalServices();
    mockTaskboardServer([makeItem()]);

    renderTaskboard("/taskboard/wi-1");

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeTruthy();
    });
    // The opened dialog is the one for the id in the URL.
    expect(
      within(screen.getByRole("dialog")).getByRole("heading", { name: "Test Item" }),
    ).toBeTruthy();
    expect(screen.getByTestId("location").textContent).toBe("/taskboard/wi-1");
  });

  test("clicking a card reflects the open item in the URL", async () => {
    mockModalServices();
    mockTaskboardServer([makeItem()]);

    renderTaskboard();
    expect(screen.getByTestId("location").textContent).toBe("/taskboard");

    const card = await screen.findByRole("button", { name: /Test Item/i });
    fireEvent.click(card);

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeTruthy();
    });
    expect(screen.getByTestId("location").textContent).toBe("/taskboard/wi-1");
  });

  test("closing the dialog clears the work item from the URL", async () => {
    mockModalServices();
    mockTaskboardServer([makeItem()]);

    renderTaskboard("/taskboard/wi-1");

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeTruthy();
    });

    await pressEscapeUntil(() => {
      expect(screen.queryByRole("dialog")).toBeNull();
    });
    expect(screen.getByTestId("location").textContent).toBe("/taskboard");
  });

  test("clicking a dependency link opens that dependency's dialog", async () => {
    mockModalServices();
    const dep = makeItem({ id: "wi-2", title: "Dep item" });
    mockTaskboardServer([makeItem({ dependencyIds: ["wi-2"] }), dep]);
    vi.spyOn(authServices.workItemService, "getDependencies").mockImplementation(async (id) =>
      id === "wi-1" ? [dep] : [],
    );

    renderTaskboard("/taskboard/wi-1");

    const link = await within(await screen.findByRole("dialog")).findByRole("link", {
      name: "Dep item",
    });
    fireEvent.click(link);

    await waitFor(() => {
      expect(screen.getByTestId("location").textContent).toBe("/taskboard/wi-2");
    });
    await waitFor(() => {
      expect(
        within(screen.getByRole("dialog")).getByRole("heading", { name: "Dep item" }),
      ).toBeTruthy();
    });
  });

  test("redirects to the taskboard when the URL points at a missing work item", async () => {
    mockModalServices();
    mockTaskboardServer([]);
    const getByIdSpy = vi
      .spyOn(authServices.workItemService, "getById")
      .mockRejectedValue(new Error("not found"));

    renderTaskboard("/taskboard/ghost");

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("ghost");
    });
    await waitFor(() => {
      expect(screen.getByTestId("location").textContent).toBe("/taskboard");
    });
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
describe("Taskboard filter", () => {
  const items = [
    makeItem({ id: "wi-1", title: "Add login page", repositoryId: "repo-1", tags: ["frontend"] }),
    makeItem({ id: "wi-2", title: "Fix logout bug", repositoryId: "repo-2", tags: ["backend"] }),
    makeItem({ id: "wi-3", title: "Unrelated chore", repositoryId: "repo-1", tags: ["backend"] }),
  ];

  test("each card names the repository its item is on", async () => {
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([
      makeRepo({ id: "repo-1", name: "Repo One" }),
      makeRepo({ id: "repo-2", name: "Repo Two" }),
    ]);
    mockTaskboardServer(items);

    renderTaskboard();

    const card = (title: string) =>
      screen.getByText(title).closest(".work-item-card") as HTMLElement;
    await waitFor(() => {
      expect(within(card("Add login page")).getByText("Repo One")).toBeTruthy();
      expect(within(card("Fix logout bug")).getByText("Repo Two")).toBeTruthy();
    });
  });

  test("marks filter chips that name a loop template, leaving free-form chips plain", async () => {
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    mockTaskboardServer([
      makeItem({ id: "wi-1", title: "Loop item", tags: ["bug fix"] }),
      makeItem({ id: "wi-2", title: "Plain item", tags: ["frontend"] }),
    ]);
    // The matcher is case-insensitive, so a "Bug Fix" template marks a "bug fix" chip.
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([
      makeTemplate({ name: "Bug Fix" }),
    ]);

    renderTaskboard();

    const loopChip = await screen.findByRole("button", { name: "bug fix" });
    const plainChip = await screen.findByRole("button", { name: "frontend" });
    expect(loopChip.className).toContain("taskboard-filter-tag--loop");
    expect(plainChip.className).not.toContain("taskboard-filter-tag--loop");
  });

  test("tag chips narrow the board and clearing restores every item", async () => {
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    mockTaskboardServer(items);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Add login page")).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "backend" }));

    await waitFor(() => {
      expect(screen.getByText("Fix logout bug")).toBeTruthy();
      expect(screen.getByText("Unrelated chore")).toBeTruthy();
      expect(screen.queryByText("Add login page")).toBeNull();
    });

    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));

    await waitFor(() => {
      expect(screen.getByText("Add login page")).toBeTruthy();
      expect(screen.getByText("Fix logout bug")).toBeTruthy();
      expect(screen.getByText("Unrelated chore")).toBeTruthy();
    });
  });
});

describe("Taskboard server-paged columns", () => {
  test("each column loads its own first page and shows the server total, and Load more pages it to the end", async () => {
    const server = mockBoard([
      ...Array.from({ length: 45 }, (_, n) => boardItem(`b${n}`, n)),
      ...Array.from({ length: 3 }, (_, n) => boardItem(`d${n}`, 100 + n, WorkItemStatus.Done)),
    ]);

    renderTaskboard();

    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    const first = server.getPage.mock.calls.map(([q]) => q);
    expect(statusSets(first)).toEqual(COLUMN_STATUSES);
    expect(
      Array.from(document.querySelectorAll(".taskboard-column-title")).map((t) => t.textContent),
    ).toEqual(COLUMN_TITLES);
    for (const q of first) {
      expect(q).toMatchObject({ skip: 0, take: 20 });
      expect(q.search ?? "").toBe("");
      expect(q.repositoryId ?? "").toBe("");
      expect(q.tags ?? []).toEqual([]);
    }
    expect(badge("Backlog")).toBe("45");
    expect(badge("Done")).toBe("3");
    expect(badge("Work queue")).toBe("0");
    expect(cardTitles("Backlog")[0]).toBe("b44");

    const loadMore = () => within(columnEl("Backlog")).queryByRole("button", { name: "Load more" });
    fireEvent.click(loadMore()!);
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(40));
    fireEvent.click(loadMore()!);
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(45));

    expect(loadMore()).toBeNull();
    expect(within(columnEl("Done")).queryByRole("button", { name: "Load more" })).toBeNull();
    expect(cardTitles("Backlog")).toEqual(Array.from({ length: 45 }, (_, n) => `b${44 - n}`));
    const backlogSkips = server.getPage.mock.calls
      .map(([q]) => q)
      .filter((q) => q.statuses.includes(WorkItemStatus.Backlog))
      .map((q) => q.skip);
    expect(backlogSkips).toEqual([0, 20, 40]);
    expect(badge("Backlog")).toBe("45");
    expect(server.getAll).not.toHaveBeenCalled();
  });

  test("search is debounced and finds an old item by id in its column, reloading every column from the top", async () => {
    const server = mockBoard([
      boardItem("wi-forgotten", 0, WorkItemStatus.Backlog, { title: "Tidy up" }),
      ...Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, 10 + n)),
      boardItem("done-forgotten", 5, WorkItemStatus.Done, { title: "Shipped" }),
    ]);

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    renderTaskboard();
    await advance(0);
    expect(cardTitles("Backlog")).toHaveLength(20);
    expect(cardTitles("Backlog")).not.toContain("Tidy up");
    const before = server.getPage.mock.calls.length;

    const search = screen.getByLabelText("Search work items");
    fireEvent.change(search, { target: { value: "f" } });
    await advance(200);
    fireEvent.change(search, { target: { value: "forg" } });
    await advance(200);
    fireEvent.change(search, { target: { value: "forgotten" } });
    await advance(SEARCH_DEBOUNCE_MS - 1);
    expect(server.getPage.mock.calls).toHaveLength(before);

    await advance(1);
    expect(cardTitles("Backlog")).toEqual(["Tidy up"]);
    expect(cardTitles("Done")).toEqual(["Shipped"]);
    expect(badge("Backlog")).toBe("1");
    expect(badge("Done")).toBe("1");
    const reload = server.getPage.mock.calls.slice(before).map(([q]) => q);
    expect(statusSets(reload)).toEqual(COLUMN_STATUSES);
    for (const q of reload) {
      expect(q.search).toBe("forgotten");
      expect(q.skip).toBe(0);
    }
  });

  test("a page answered for an outdated search never replaces the newer one", async () => {
    const server = mockBoard([
      boardItem("first-match", 1),
      boardItem("second-match", 2),
      boardItem("unrelated", 3),
    ]);
    const held = deferred<void>();
    server.getPage.mockImplementation(async (q) => {
      const snapshot = server.page(q);
      if (q.search === "first") await held.promise;
      return snapshot;
    });

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    renderTaskboard();
    await advance(0);
    expect(cardTitles("Backlog")).toHaveLength(3);

    const search = screen.getByLabelText("Search work items");
    fireEvent.change(search, { target: { value: "first" } });
    await advance(SEARCH_DEBOUNCE_MS);
    expect(server.getPage.mock.calls.some(([q]) => q.search === "first")).toBe(true);
    fireEvent.change(search, { target: { value: "second" } });
    await advance(SEARCH_DEBOUNCE_MS);
    expect(cardTitles("Backlog")).toEqual(["second-match"]);

    held.resolve();
    await advance(0);

    expect(cardTitles("Backlog")).toEqual(["second-match"]);
    expect(badge("Backlog")).toBe("1");
  });

  test("repository and tag filters narrow every column, with options for every repository and every tag in use", async () => {
    const server = mockBoard(
      [
        ...Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, 10 + n)),
        boardItem("ancient", 0, WorkItemStatus.Backlog, {
          repositoryId: "repo-2",
          tags: ["ancient", "frontend"],
        }),
        boardItem("beta-newer", 50, WorkItemStatus.Backlog, {
          repositoryId: "repo-2",
          tags: ["frontend"],
        }),
        boardItem("beta-done", 40, WorkItemStatus.Done, {
          repositoryId: "repo-2",
          tags: ["frontend"],
        }),
      ],
      [
        makeRepo({ id: "repo-1", name: "Alpha" }),
        makeRepo({ id: "repo-2", name: "Beta" }),
        makeRepo({ id: "repo-3", name: "Gamma" }),
      ],
    );

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    expect(cardTitles("Backlog")).not.toContain("ancient");
    // Options come from the server, not from the cards that happen to be loaded.
    expect(await screen.findByRole("option", { name: "Gamma" })).toBeTruthy();
    expect(await screen.findByRole("button", { name: "ancient" })).toBeTruthy();

    let before = server.getPage.mock.calls.length;
    fireEvent.change(screen.getByLabelText("Filter by repository"), {
      target: { value: "repo-2" },
    });
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["beta-newer", "ancient"]));
    await waitFor(() => expect(cardTitles("Done")).toEqual(["beta-done"]));
    expect(badge("Backlog")).toBe("2");
    expect(badge("Done")).toBe("1");
    for (const [q] of server.getPage.mock.calls.slice(before)) {
      expect(q.repositoryId).toBe("repo-2");
      expect(q.skip).toBe(0);
    }

    before = server.getPage.mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: "ancient" }));
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["ancient"]));
    await waitFor(() => expect(cardTitles("Done")).toEqual([]));
    expect(badge("Backlog")).toBe("1");
    expect(badge("Done")).toBe("0");
    const tagged = server.getPage.mock.calls.slice(before).map(([q]) => q);
    expect(statusSets(tagged)).toEqual(COLUMN_STATUSES);
    for (const q of tagged) {
      expect(q.repositoryId).toBe("repo-2");
      expect(q.tags).toEqual(["ancient"]);
    }
  });
});

describe("Taskboard live changes keep server order", () => {
  test("creating a work item puts its card at the top of Backlog at once and counts it", async () => {
    const server = mockBoard(Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, n)));
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
    vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
      maxBytesPerFile: 1024 * 1024,
      maxFilesPerRequest: 10,
      maxTotalBytesPerWorkItem: 10 * 1024 * 1024,
    });
    vi.spyOn(authServices.workItemService, "create").mockImplementation(async (data) => {
      const created = boardItem("wi-created", 1000, WorkItemStatus.Backlog, {
        title: data.title ?? "",
      });
      server.items.push(created);
      return created;
    });

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    expect(badge("Backlog")).toBe("25");

    fireEvent.click(screen.getByRole("button", { name: "New work item" }));
    await within(await screen.findByRole("dialog")).findByRole("option", { name: "Repo One" });
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Brand new" } });
      fireEvent.change(screen.getByLabelText("Repository"), { target: { value: "repo-1" } });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Create" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(cardTitles("Backlog")[0]).toBe("Brand new"));
    expect(cardTitles("Backlog").slice(1, 3)).toEqual(["b24", "b23"]);
    await settle();
    expect(badge("Backlog")).toBe("26");
    expect(cardTitles("Backlog")[0]).toBe("Brand new");
  });

  test("an item created elsewhere lands in server order over SignalR, and one beyond the loaded page is only counted", async () => {
    const server = mockBoard(Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, 10 + n)));

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    const created = (id: string, n: number) => {
      server.items.push(boardItem(id, n));
      return signalRHub.emit("WorkItemStateChanged", {
        workItemId: id,
        oldStatus: "Backlog",
        newStatus: "Backlog",
      });
    };

    await created("newest", 100);
    await waitFor(() => expect(cardTitles("Backlog")[0]).toBe("newest"));

    await created("between", 30.5);
    await waitFor(() => expect(cardTitles("Backlog")).toContain("between"));
    const titles = cardTitles("Backlog");
    expect(titles.slice(titles.indexOf("b21"), titles.indexOf("b21") + 3)).toEqual([
      "b21",
      "between",
      "b20",
    ]);

    await created("very-old", 1);
    await waitFor(() => expect(badge("Backlog")).toBe("28"));
    expect(cardTitles("Backlog")).not.toContain("very-old");
    expect(cardTitles("Backlog")).toHaveLength(22);
  });

  test("an item created while its column is reloading is not lost when the older response lands", async () => {
    const server = mockBoard([
      ...Array.from({ length: 5 }, (_, n) => boardItem(`a${n}`, n)),
      boardItem("elsewhere", 50, WorkItemStatus.Backlog, { repositoryId: "repo-2" }),
    ]);
    const held = deferred<void>();
    let holding = true;
    server.getPage.mockImplementation(async (q) => {
      const snapshot = server.page(q);
      if (holding && q.statuses.includes(WorkItemStatus.Backlog) && q.repositoryId === "repo-1") {
        holding = false;
        await held.promise;
      }
      return snapshot;
    });

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(6));

    fireEvent.change(screen.getByLabelText("Filter by repository"), {
      target: { value: "repo-1" },
    });
    await waitFor(() => expect(holding).toBe(false));

    server.items.push(boardItem("fresh", 100));
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "fresh",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await settle();
    held.resolve();

    await waitFor(() =>
      expect(cardTitles("Backlog")).toEqual(["fresh", "a4", "a3", "a2", "a1", "a0"]),
    );
    await waitFor(() => expect(badge("Backlog")).toBe("6"));
  });

  test("a status change over SignalR moves the card to its sorted place and updates both totals", async () => {
    const server = mockBoard([
      boardItem("r7", 7, WorkItemStatus.Ready),
      boardItem("u9", 9, WorkItemStatus.Running),
      boardItem("u2", 2, WorkItemStatus.Running),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Running")).toEqual(["u9", "u2"]));

    server.items.find((wi) => wi.id === "r7")!.status = WorkItemStatus.Running;
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "r7",
      oldStatus: "Ready",
      newStatus: "Running",
    });

    await waitFor(() => expect(cardTitles("Running")).toEqual(["u9", "r7", "u2"]));
    expect(cardTitles("Work queue")).toEqual([]);
    await waitFor(() => expect(badge("Running")).toBe("3"));
    expect(badge("Work queue")).toBe("0");
  });

  test("a reorder announced over SignalR moves the card to its new place in the same column", async () => {
    const server = mockBoard([
      boardItem("a", 9, WorkItemStatus.Ready),
      boardItem("b", 5, WorkItemStatus.Ready),
      boardItem("c", 2, WorkItemStatus.Ready),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Work queue")).toEqual(["a", "b", "c"]));

    server.items.find((wi) => wi.id === "c")!.priority = -100;
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "c",
      oldStatus: "Ready",
      newStatus: "Ready",
    });
    await waitFor(() => expect(cardTitles("Work queue")).toEqual(["c", "a", "b"]));

    server.items.find((wi) => wi.id === "a")!.priority = -3;
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "a",
      oldStatus: "Ready",
      newStatus: "Ready",
    });
    await waitFor(() => expect(cardTitles("Work queue")).toEqual(["c", "b", "a"]));
    await settle();
    expect(badge("Work queue")).toBe("3");
  });

  test("a renumber announced once for the whole board re-reads the columns in their new order", async () => {
    const server = mockBoard([
      boardItem("a", 9, WorkItemStatus.Ready),
      boardItem("b", 5, WorkItemStatus.Ready),
      boardItem("c", 2, WorkItemStatus.Backlog),
      boardItem("d", 1, WorkItemStatus.Backlog),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Work queue")).toEqual(["a", "b"]));
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["c", "d"]));

    const renumbered: Record<string, number> = { b: 0, a: 2, d: 4, c: 6 };
    for (const wi of server.items) wi.priority = renumbered[wi.id];
    await signalRHub.emit("WorkItemBoardReordered", { workItemId: "b" });

    await waitFor(() => expect(cardTitles("Work queue")).toEqual(["b", "a"]));
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["d", "c"]));
  });

  test("under a filter a card edited out of it leaves and a non-matching new item never shows", async () => {
    const server = mockBoard([
      boardItem("keep", 3),
      boardItem("moves-repo", 2),
      boardItem("other-repo", 1, WorkItemStatus.Backlog, { repositoryId: "repo-2" }),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(3));
    fireEvent.change(screen.getByLabelText("Filter by repository"), {
      target: { value: "repo-1" },
    });
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["keep", "moves-repo"]));

    server.items.find((wi) => wi.id === "moves-repo")!.repositoryId = "repo-2";
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "moves-repo",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["keep"]));
    await waitFor(() => expect(badge("Backlog")).toBe("1"));

    server.items.push(
      boardItem("new-elsewhere", 100, WorkItemStatus.Backlog, { repositoryId: "repo-2" }),
    );
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "new-elsewhere",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await settle();
    expect(cardTitles("Backlog")).toEqual(["keep"]);
    expect(badge("Backlog")).toBe("1");
  });

  test("a keyboard move takes the card from its column to the next and both totals follow", async () => {
    mockBoard([
      boardItem("mover", 5, WorkItemStatus.Ready),
      boardItem("u9", 9, WorkItemStatus.Running),
    ]);

    renderTaskboard();
    const card = await screen.findByRole("button", { name: /^mover,/ });
    card.focus();
    fireEvent.keyDown(card, { key: "ArrowRight" });

    await waitFor(() => expect(cardTitles("Running")).toEqual(["u9", "mover"]));
    expect(cardTitles("Work queue")).toEqual([]);
    await waitFor(() => expect(badge("Running")).toBe("2"));
    expect(badge("Work queue")).toBe("0");
  });

  test("totals reconcile from the server with one counts request in flight and one follow-up", async () => {
    const server = mockBoard(Array.from({ length: 6 }, (_, n) => boardItem(`b${n}`, n)));
    const held = deferred<void>();
    let armed = false;
    server.getCounts.mockImplementation(async (filter) => {
      if (armed) {
        armed = false;
        await held.promise;
      }
      return server.counts(filter);
    });

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(6));
    armed = true;
    const countsBefore = server.getCounts.mock.calls.length;

    // Deleted by someone else, with no event for it: only the count the next
    // edit asks for can tell.
    server.items.splice(
      server.items.findIndex((wi) => wi.id === "b0"),
      1,
    );
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "b1",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await waitFor(() => expect(server.getCounts.mock.calls.length).toBe(countsBefore + 1));
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "b2",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "b3",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await settle();
    expect(server.getCounts.mock.calls.length).toBe(countsBefore + 1);

    held.resolve();
    await waitFor(() => expect(server.getCounts.mock.calls.length).toBe(countsBefore + 2));
    await settle();
    expect(server.getCounts.mock.calls.length).toBe(countsBefore + 2);
    await waitFor(() => expect(badge("Backlog")).toBe("5"));
  });

  test("a counts answer for an outdated filter is dropped", async () => {
    const server = mockBoard(
      [
        ...Array.from({ length: 4 }, (_, n) => boardItem(`b${n}`, n)),
        boardItem("other", 10, WorkItemStatus.Backlog, { repositoryId: "repo-2" }),
      ],
      [makeRepo(), makeRepo({ id: "repo-2", name: "Repo Two" })],
    );
    const held = deferred<void>();
    let armed = false;
    server.getCounts.mockImplementation(async (filter) => {
      if (armed) {
        armed = false;
        await held.promise;
      }
      return server.counts(filter);
    });

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(5));
    armed = true;
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "b1",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await waitFor(() => expect(armed).toBe(false));

    fireEvent.change(screen.getByLabelText("Filter by repository"), {
      target: { value: "repo-2" },
    });
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["other"]));
    held.resolve();
    await settle();

    expect(badge("Backlog")).toBe("1");
  });
});

describe("Taskboard direct link", () => {
  test("an item outside the loaded page opens in the dialog without joining its column", async () => {
    mockModalServices();
    mockBoard(Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, n)));
    const getById = vi.mocked(authServices.workItemService.getById);

    renderTaskboard("/taskboard/b0");

    await waitFor(() =>
      expect(within(screen.getByRole("dialog")).getByRole("heading", { name: "b0" })).toBeTruthy(),
    );
    expect(getById).toHaveBeenCalledWith("b0");
    await settle();
    const fetched = getById.mock.calls.filter(([id]) => id === "b0").length;
    await settle();

    expect(getById.mock.calls.filter(([id]) => id === "b0").length).toBe(fetched);
    expect(cardTitles("Backlog")).toHaveLength(20);
    expect(cardTitles("Backlog")).not.toContain("b0");
    expect(badge("Backlog")).toBe("25");
  });
});

describe("Taskboard on a phone", () => {
  beforeEach(() => {
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: query === "(max-width: 767px)",
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }));
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([makeRepo()]);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const items = [
    makeItem({ id: "wi-1", title: "Waiting on me", status: WorkItemStatus.HumanFeedback }),
    makeItem({ id: "wi-2", title: "Not started", status: WorkItemStatus.Backlog }),
  ];

  test("shows one column at a time, picked above the board, opening on Needs you", async () => {
    mockTaskboardServer(items);
    renderTaskboard();

    expect(await screen.findByText("Waiting on me")).toBeTruthy();
    expect(screen.queryByText("Not started")).toBeNull();

    const picker = within(screen.getByRole("tablist", { name: "Columns" }));
    const needsYou = picker.getByRole("tab", { name: /Needs you/ });
    expect(needsYou.getAttribute("aria-selected")).toBe("true");
    expect(needsYou.textContent).toBe("Needs you1");

    fireEvent.click(picker.getByRole("tab", { name: /Backlog/ }));
    expect(await screen.findByText("Not started")).toBeTruthy();
    expect(screen.queryByText("Waiting on me")).toBeNull();
  });

  test("the column picker offers the six board columns, with Work queue counting Ready items too", async () => {
    mockTaskboardServer([
      ...items,
      makeItem({ id: "wi-3", title: "Queued", status: WorkItemStatus.WorkQueue }),
      makeItem({ id: "wi-4", title: "Startable", status: WorkItemStatus.Ready }),
    ]);
    renderTaskboard();
    await screen.findByText("Waiting on me");

    const tabs = within(screen.getByRole("tablist", { name: "Columns" })).getAllByRole("tab");
    await waitFor(() =>
      expect(tabs.map((tab) => tab.textContent)).toEqual([
        "Backlog1",
        "Work queue2",
        "Running0",
        "Needs you1",
        "Waiting for ILD0",
        "Done0",
      ]),
    );

    fireEvent.click(screen.getByRole("tab", { name: /Work queue/ }));
    expect(await screen.findByText("Queued")).toBeTruthy();
    expect(screen.getByText("Startable")).toBeTruthy();
  });

  test("an empty column says what lands there instead of folding to a strip", async () => {
    mockTaskboardServer(items);
    renderTaskboard();
    await screen.findByText("Waiting on me");

    fireEvent.click(screen.getByRole("tab", { name: /Running/ }));
    expect(screen.getByText("Ready items start here while the scheduler is running.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Show column/ })).toBeNull();
  });

  test("the repository and tag filters move into a Filters sheet, and New work item is an icon", async () => {
    mockTaskboardServer(items);
    renderTaskboard();
    await screen.findByText("Waiting on me");

    expect(screen.queryByLabelText("Filter by repository")).toBeNull();
    expect(screen.getByRole("button", { name: "New work item" }).textContent).toBe("");

    fireEvent.click(screen.getByRole("button", { name: "Filters" }));
    const sheet = within(screen.getByRole("dialog", { name: "Filters" }));
    fireEvent.change(sheet.getByLabelText("Filter by repository"), {
      target: { value: "repo-1" },
    });
    fireEvent.click(sheet.getByRole("button", { name: "Show results" }));

    expect(screen.queryByRole("dialog", { name: "Filters" })).toBeNull();
    expect(screen.getByRole("button", { name: /Filters/ }).textContent).toBe("Filters1 on");
  });
});

describe("Taskboard folded columns", () => {
  beforeEach(() => {
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  });

  test("opens each column as the user left it, and saves a fold to their preferences", async () => {
    seedPreferences({ [foldedColumnKey(WorkItemStatus.Backlog)]: "true" });
    mockTaskboardServer([
      makeItem({ id: "wi-1", title: "Not started", status: WorkItemStatus.Backlog }),
      makeItem({ id: "wi-2", title: "Shipped", status: WorkItemStatus.Done }),
    ]);
    renderTaskboard();

    expect(
      await screen.findByRole("button", { name: "Backlog: 1 item. Show column" }),
    ).toBeTruthy();
    // Done has no choice of its own, so it folds by its rule.
    expect(screen.getByRole("button", { name: "Done: 1 item. Show column" })).toBeTruthy();
    expect(screen.queryByText("Not started")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Done: 1 item. Show column" }));
    expect(screen.getByText("Shipped")).toBeTruthy();
    expect(getPreference(foldedColumnKey(WorkItemStatus.Done))).toBe("false");
  });

  test("shows no columns until the preferences are loaded", async () => {
    clearPreferences();
    mockTaskboardServer([makeItem({ id: "wi-1", title: "Not started" })]);
    renderTaskboard();

    await settle();
    expect(screen.getByText("Loading taskboard...")).toBeTruthy();
    expect(screen.queryByText("Not started")).toBeNull();
  });
});

describe("Taskboard Work queue column", () => {
  const QUEUE_PILL =
    /^(Waiting on dependencies|Waiting: repository parallelism limit \(\d+\/\d+ in progress\)|Scheduler paused|Ready)$/;

  function cardEl(id: string): HTMLElement {
    const card = document.querySelector<HTMLElement>(`.work-item-card[data-work-item-id="${id}"]`);
    if (!card) throw new Error(`no card ${id}`);
    return card;
  }

  /** The waiting reason pills a card shows. */
  function queuePills(id: string): string[] {
    return Array.from(cardEl(id).querySelectorAll("*"))
      .filter((el) => el.children.length === 0 && QUEUE_PILL.test(el.textContent ?? ""))
      .map((el) => el.textContent!);
  }

  function pausedReads(paused: boolean) {
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: paused ? "true" : "false",
    });
  }

  test("a Work queue card whose dependencies finish turns Ready in its place without a reload", async () => {
    const server = mockBoard([
      boardItem("first", 9, WorkItemStatus.Ready),
      boardItem("waiting", 5, WorkItemStatus.WorkQueue),
      boardItem("last", 2, WorkItemStatus.WorkQueue),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Work queue")).toEqual(["first", "waiting", "last"]));
    await waitFor(() => expect(queuePills("first")).toEqual(["Ready"]));
    expect(queuePills("waiting")).toEqual(["Waiting on dependencies"]);
    const reads = server.getPage.mock.calls.length;

    server.items.find((wi) => wi.id === "waiting")!.status = WorkItemStatus.Ready;
    await signalRHub.emit("WorkItemStateChanged", {
      workItemId: "waiting",
      oldStatus: "WorkQueue",
      newStatus: "Ready",
    });

    await waitFor(() => expect(queuePills("waiting")).toEqual(["Ready"]));
    expect(cardTitles("Work queue")).toEqual(["first", "waiting", "last"]);
    await settle();
    expect(badge("Work queue")).toBe("3");
    expect(server.getPage.mock.calls.length).toBe(reads);
  });

  test("each card shows why it waits, and the Ready ones follow the scheduler's pause and resume", async () => {
    mockBoard([
      boardItem("queued", 9, WorkItemStatus.WorkQueue),
      boardItem("held", 7, WorkItemStatus.Ready, {
        repositoryParallelismHold: { inProgress: 2, limit: 2 },
      }),
      boardItem("startable", 5, WorkItemStatus.Ready),
      boardItem("going", 3, WorkItemStatus.Running),
    ]);

    renderTaskboard();
    await waitFor(() => expect(queuePills("startable")).toEqual(["Ready"]));
    expect(queuePills("queued")).toEqual(["Waiting on dependencies"]);
    expect(queuePills("held")).toEqual(["Waiting: repository parallelism limit (2/2 in progress)"]);
    expect(queuePills("going")).toEqual([]);

    await signalRHub.emit("SchedulerStateChanged", { isPaused: true });

    await waitFor(() => expect(queuePills("startable")).toEqual(["Scheduler paused"]));
    expect(queuePills("held")).toEqual(["Waiting: repository parallelism limit (2/2 in progress)"]);
    expect(queuePills("queued")).toEqual(["Waiting on dependencies"]);
    expect(queuePills("going")).toEqual([]);

    await signalRHub.emit("SchedulerStateChanged", { isPaused: false });

    await waitFor(() => expect(queuePills("startable")).toEqual(["Ready"]));
  });

  test("a Ready card shows Scheduler paused on a board opened while the scheduler is paused", async () => {
    mockBoard([boardItem("startable", 5, WorkItemStatus.Ready)]);
    pausedReads(true);

    renderTaskboard();

    await waitFor(() => expect(queuePills("startable")).toEqual(["Scheduler paused"]));
  });

  test("an open Ready item's dialog offers Start now while paused and follows a live resume and pause", async () => {
    mockModalServices();
    mockBoard([boardItem("startable", 5, WorkItemStatus.Ready)]);
    pausedReads(true);

    renderTaskboard();
    await waitFor(() => expect(queuePills("startable")).toEqual(["Scheduler paused"]));
    expect(within(cardEl("startable")).queryByRole("button", { name: /start/i })).toBeNull();
    fireEvent.click(cardEl("startable"));
    const dialog = await screen.findByRole("dialog");
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Start now (scheduler paused)" }),
      ).toBeTruthy(),
    );

    await signalRHub.emit("SchedulerStateChanged", { isPaused: false });

    await waitFor(() =>
      expect(
        within(dialog).queryByRole("button", { name: "Start now (scheduler paused)" }),
      ).toBeNull(),
    );

    await signalRHub.emit("SchedulerStateChanged", { isPaused: true });

    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Start now (scheduler paused)" }),
      ).toBeTruthy(),
    );
  });

  test.each([
    [WorkItemStatus.Backlog, "ArrowRight", WorkItemStatus.WorkQueue, "Work queue"],
    [WorkItemStatus.WorkQueue, "ArrowRight", WorkItemStatus.Running, "Running"],
    [WorkItemStatus.Ready, "ArrowRight", WorkItemStatus.Running, "Running"],
    [WorkItemStatus.WorkQueue, "ArrowLeft", WorkItemStatus.Backlog, "Backlog"],
    [WorkItemStatus.Ready, "ArrowLeft", WorkItemStatus.Backlog, "Backlog"],
    [WorkItemStatus.Running, "ArrowLeft", WorkItemStatus.WorkQueue, "Work queue"],
  ])("%s with %s moves to %s and says so", async (from, key, target, label) => {
    mockBoard([boardItem("mover", 5, from)]);
    const transition = vi.spyOn(authServices.workItemService, "transition");

    renderTaskboard();
    const card = await screen.findByRole("button", { name: /^mover,/ });
    card.focus();
    fireEvent.keyDown(card, { key });

    await waitFor(() => expect(transition).toHaveBeenCalledWith("mover", target));
    expect(transition).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.getByRole("status").textContent).toContain(label));
  });

  test.each([
    ["with a dependency still open", WorkItemStatus.WorkQueue, "Waiting on dependencies"],
    ["with every dependency Done", WorkItemStatus.Ready, "Ready"],
  ])(
    "a Backlog card dropped into the empty Work queue column %s is queued and shown there",
    async (_deps, lands, pill) => {
      seedPreferences({
        [foldedColumnKey(WorkItemStatus.Done)]: "false",
        [foldedColumnKey(WorkItemStatus.WorkQueue)]: "false",
      });
      const server = mockBoard([boardItem("planned", 5)]);
      const transition = vi
        .spyOn(authServices.workItemService, "transition")
        .mockImplementation(async (id: string, status: string) => {
          // The server queues the item and promotes it at once when nothing blocks it.
          const found = server.items.find((wi) => wi.id === id)!;
          found.status = status === WorkItemStatus.WorkQueue ? lands : (status as WorkItemStatus);
        });

      renderTaskboard();
      await waitFor(() => expect(cardTitles("Backlog")).toEqual(["planned"]));

      fireEvent.drop(columnEl("Work queue"), {
        dataTransfer: { getData: () => "planned" },
      } as unknown as DragEvent);

      await waitFor(() => expect(cardTitles("Work queue")).toEqual(["planned"]));
      expect(transition).toHaveBeenCalledWith("planned", WorkItemStatus.WorkQueue);
      expect(cardTitles("Backlog")).toEqual([]);
      await waitFor(() => expect(queuePills("planned")).toEqual([pill]));
      await settle();
      expect(badge("Work queue")).toBe("1");
      expect(badge("Backlog")).toBe("0");
    },
  );
});
