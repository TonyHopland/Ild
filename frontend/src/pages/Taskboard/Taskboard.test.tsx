import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, within, cleanup, waitFor, act, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route, useLocation } from "react-router";
import Taskboard from "./index";
import { mockTaskboardServer, pressEscapeUntil } from "../../test-support";
import { WorkItemStatus, WorkItemPriority, WorkItem, Repository, LoopTemplate } from "../../types";
import * as authServices from "../../services/auth";
import * as signalRHook from "../../hooks/useSignalR";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

beforeEach(() => {
  localStorage.clear();
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
  );
}

// Mocks the work item detail dialog's own data fetches so it can render.
function mockModalServices() {
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
}

async function dispatchSignalR(handler: (msg: any) => void, payload: unknown) {
  await act(async () => {
    handler({ payload });
  });
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
    priority: WorkItemPriority.Medium,
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

const ALL_STATUSES = Object.values(WorkItemStatus) as string[];

/** Item `id` created `n` hours into 2026; a higher `n` is newer. The title is the id. */
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

/** A hub whose events a test fires by name. */
function mockHub() {
  const handlers: Record<string, ((msg: any) => void)[]> = {};
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn((event: string, handler: (msg: any) => void) => {
      (handlers[event] ??= []).push(handler);
    }),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  });
  return {
    emit: async (event: string, payload: unknown) => {
      await act(async () => {
        for (const handler of handlers[event] ?? []) handler({ payload });
      });
    },
  };
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
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    mockTaskboardServer([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    const humanFeedbackHandlers = handlers["HumanFeedbackRequired"];
    expect(humanFeedbackHandlers).toBeDefined();
    expect(humanFeedbackHandlers!.length).toBeGreaterThan(0);

    await dispatchSignalR(humanFeedbackHandlers![0], {
      workItemId: "wi-1",
      reason: "PR Awaiting Merge",
    });

    await waitFor(() => {
      expect(screen.getByText("PR Awaiting Merge")).toBeTruthy();
    });
  });

  test("reconciles board item from server after WorkItemStateChanged event", async () => {
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

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

    await dispatchSignalR(handlers["WorkItemStateChanged"]![0], {
      workItemId: "wi-1",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-1");
      expect(screen.getByText("Human Input Needed")).toBeTruthy();
    });
  });

  test("adds a newly created item going Running->HumanFeedback when not yet on the board", async () => {
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

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
      expect(handlers["WorkItemStateChanged"]).toBeDefined();
    });

    await dispatchSignalR(handlers["WorkItemStateChanged"]![0], {
      workItemId: "wi-new",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-new");
      expect(screen.getByText("Fresh Item")).toBeTruthy();
      expect(screen.getByText("Human Input Needed")).toBeTruthy();
    });
  });

  test("adds a newly created item on HumanFeedbackRequired when not yet on the board", async () => {
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

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
      expect(handlers["HumanFeedbackRequired"]).toBeDefined();
    });

    await dispatchSignalR(handlers["HumanFeedbackRequired"]![0], {
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
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

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
    await dispatchSignalR(handlers["WorkItemRunProgressed"]![0], { workItemId: "wi-1" });
    await dispatchSignalR(handlers["WorkItemStateChanged"]![0], {
      workItemId: "wi-1",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    await waitFor(() => {
      expect(screen.getByText("Human Input Needed")).toBeTruthy();
    });

    // The earlier fetch finally returns its stale Running snapshot.
    await act(async () => {
      stale.resolve(staleRunning);
    });

    // It must not revert the card back to Running.
    expect(screen.queryByText("Human Input Needed")).toBeTruthy();
  });

  test("refreshes a running card's current step on WorkItemRunProgressed", async () => {
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

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

    await dispatchSignalR(handlers["WorkItemRunProgressed"]![0], {
      workItemId: "wi-1",
    });

    await waitFor(() => {
      expect(getByIdSpy).toHaveBeenCalledWith("wi-1");
      expect(screen.getByText("implement")).toBeTruthy();
    });
  });

  test("normalizes numeric status from a WorkItemStateChanged event", async () => {
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

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
    await dispatchSignalR(handlers["WorkItemStateChanged"]![0], {
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

    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    mockTaskboardServer([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await dispatchSignalR(handlers["HumanFeedbackRequired"]![0], {
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
    localStorage.setItem("ild_notifications_enabled", "false");

    const notificationCalls: Array<[string, NotificationOptions]> = [];
    class MockNotification {
      static permission = "granted";
      constructor(title: string, options?: NotificationOptions) {
        notificationCalls.push([title, options!]);
      }
    }
    vi.stubGlobal("Notification", MockNotification);

    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    mockTaskboardServer([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await dispatchSignalR(handlers["HumanFeedbackRequired"]![0], {
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

  test("a reconnect reloads each column's loaded window from the top, the scheduler state and the tags", async () => {
    let connectionState: "connected" | "reconnecting" | "disconnected" = "connected";
    vi.spyOn(signalRHook, "useSignalR").mockImplementation(() => ({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState,
    }));
    const server = mockTaskboardServer([
      ...Array.from({ length: 45 }, (_, n) => boardItem(`b${n}`, n)),
      boardItem("r0", 0, WorkItemStatus.Ready),
    ]);
    const settingsSpy = vi
      .spyOn(authServices.settingsService, "get")
      .mockResolvedValue({ key: "scheduler.isPaused", value: "false" });

    const { rerender } = render(taskboardTree());

    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    fireEvent.click(within(columnEl("Backlog")).getByRole("button", { name: "Load more" }));
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(40));
    expect(settingsSpy).toHaveBeenCalledTimes(1);
    const pagesBefore = server.getPage.mock.calls.length;
    const tagsBefore = server.getTags.mock.calls.length;

    // While the socket is down another client creates an item; its event is lost.
    server.items.push(boardItem("made-during-outage", 100));
    connectionState = "reconnecting";
    await act(async () => {
      rerender(taskboardTree());
    });
    expect(server.getPage.mock.calls.length).toBe(pagesBefore);

    connectionState = "connected";
    await act(async () => {
      rerender(taskboardTree());
    });

    await waitFor(() => expect(cardTitles("Backlog")[0]).toBe("made-during-outage"));
    expect(badge("Backlog")).toBe("46");
    const reload = server.getPage.mock.calls.slice(pagesBefore).map(([q]) => q);
    expect(reload.map((q) => q.status).sort()).toEqual([...ALL_STATUSES].sort());
    for (const q of reload) {
      expect(q.skip).toBe(0);
      expect(q.take).toBe(q.status === WorkItemStatus.Backlog ? 40 : 20);
    }
    await waitFor(() => expect(settingsSpy).toHaveBeenCalledTimes(2));
    expect(server.getTags.mock.calls.length).toBeGreaterThan(tagsBefore);
    expect(server.getAll).not.toHaveBeenCalled();
  });

  test("does not re-fetch on the very first connect", async () => {
    // A board that mounts while already connected must load exactly once: the
    // mount effect's load, with no extra fetch from the connect transition.
    let connectionState: "connected" | "reconnecting" | "disconnected" = "disconnected";
    vi.spyOn(signalRHook, "useSignalR").mockImplementation(() => ({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState,
    }));

    const server = mockTaskboardServer([makeItem()]);
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: "false",
    });

    const { rerender } = render(taskboardTree());

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    // The socket finishes its initial handshake.
    connectionState = "connected";
    await act(async () => {
      rerender(taskboardTree());
    });

    // Still a single first page per column — the first arrival at "connected"
    // is not a recovery.
    await waitFor(() => {
      expect(server.getPage.mock.calls.map(([q]) => q.status).sort()).toEqual(
        [...ALL_STATUSES].sort(),
      );
    });
  });
});

describe("Taskboard keyboard navigation", () => {
  test("ArrowRight on a focused card transitions to the next column", async () => {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });
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
  test("performs delayed refetch of editing item after WorkItemStateChanged to catch conversation data", async () => {
    const handlers: Record<string, ((msg: any) => void)[]> = {};
    const mockOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      handlers[eventType] = handlers[eventType] || [];
      handlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const initialItem = makeItem({ status: WorkItemStatus.Running });
    mockTaskboardServer([initialItem]);
    // Mock modal's internal fetches
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);

    // First refetch returns stale data (conversation not yet persisted)
    const staleItem = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      conversation: [],
    });

    // Delayed refetch returns fresh data with conversation
    const freshItem = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      conversation: [{ role: "ai", content: "AI response", timestamp: "2025-01-01T00:00:00Z" }],
    });

    let getByIdCallCount = 0;
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() => {
      getByIdCallCount++;
      return getByIdCallCount === 1 ? Promise.resolve(staleItem) : Promise.resolve(freshItem);
    });

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    // Click the card to open the modal (set editingItem)
    const card = await screen.findByRole("button", { name: /Test Item/i });
    const { fireEvent } = await import("@testing-library/react");
    fireEvent.click(card);

    // Wait for modal to open
    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeTruthy();
    });

    // Trigger WorkItemStateChanged event
    await dispatchSignalR(handlers["WorkItemStateChanged"]![0], {
      workItemId: "wi-1",
      oldStatus: "Running",
      newStatus: "HumanFeedback",
    });

    // Should refetch at least twice: immediate + delayed
    await waitFor(() => {
      expect(getByIdCallCount).toBeGreaterThan(1);
    });
  });
});

describe("Taskboard work item URL", () => {
  function mockSignalR() {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });
  }

  test("opens the detail dialog for the work item id in the URL", async () => {
    mockSignalR();
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
    mockSignalR();
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
    mockSignalR();
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

  test("redirects to the taskboard when the URL points at a missing work item", async () => {
    mockSignalR();
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

describe("Taskboard toolbar", () => {
  function mockSignalR() {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });
  }

  test("drops the page heading and groups the filter with the running toggle in one toolbar", async () => {
    mockSignalR();
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    mockTaskboardServer([makeItem()]);
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: "false",
    });

    const { container } = renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    // The heading is gone entirely.
    expect(screen.queryByRole("heading", { name: "Taskboard" })).toBeNull();

    // The search field and the running toggle now share a single toolbar.
    const toolbar = container.querySelector(".taskboard-toolbar");
    expect(toolbar).toBeTruthy();
    expect(within(toolbar as HTMLElement).getByLabelText("Search work items")).toBeTruthy();
    expect(within(toolbar as HTMLElement).getByLabelText("Scheduler running")).toBeTruthy();
  });

  test("the running toggle still flips the scheduler from its toolbar home", async () => {
    mockSignalR();
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    mockTaskboardServer([makeItem()]);
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "scheduler.isPaused",
      value: "false",
    });
    const putSpy = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: "scheduler.isPaused", value: "true" });

    renderTaskboard();

    const toggle = await screen.findByLabelText("Scheduler running");
    expect((toggle as HTMLInputElement).checked).toBe(true);

    fireEvent.click(toggle);

    await waitFor(() => {
      expect(putSpy).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.IsPaused, "true");
    });
    await waitFor(() => {
      expect(screen.getByText("Paused")).toBeTruthy();
    });
  });
});

describe("Taskboard filter", () => {
  function mockSignalR() {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });
  }

  const items = [
    makeItem({ id: "wi-1", title: "Add login page", repositoryId: "repo-1", tags: ["frontend"] }),
    makeItem({ id: "wi-2", title: "Fix logout bug", repositoryId: "repo-2", tags: ["backend"] }),
    makeItem({ id: "wi-3", title: "Unrelated chore", repositoryId: "repo-1", tags: ["backend"] }),
  ];

  test("search narrows the board to matching work items", async () => {
    mockSignalR();
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    mockTaskboardServer(items);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Add login page")).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText("Search work items"), {
      target: { value: "log" },
    });

    await waitFor(() => {
      expect(screen.getByText("Add login page")).toBeTruthy();
      expect(screen.getByText("Fix logout bug")).toBeTruthy();
      expect(screen.queryByText("Unrelated chore")).toBeNull();
    });
  });

  test("repository filter is labelled by repository name", async () => {
    mockSignalR();
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([
      makeRepo({ id: "repo-1", name: "Repo One" }),
      makeRepo({ id: "repo-2", name: "Repo Two" }),
    ]);
    mockTaskboardServer(items);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Add login page")).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText("Filter by repository"), {
      target: { value: "repo-2" },
    });

    await waitFor(() => {
      expect(screen.getByText("Fix logout bug")).toBeTruthy();
      expect(screen.queryByText("Add login page")).toBeNull();
      expect(screen.queryByText("Unrelated chore")).toBeNull();
    });
  });

  test("marks filter chips that name a loop template, leaving free-form chips plain", async () => {
    mockSignalR();
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
    mockSignalR();
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
    mockHub();
    const server = mockBoard([
      ...Array.from({ length: 45 }, (_, n) => boardItem(`b${n}`, n)),
      ...Array.from({ length: 3 }, (_, n) => boardItem(`d${n}`, 100 + n, WorkItemStatus.Done)),
    ]);

    renderTaskboard();

    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    const first = server.getPage.mock.calls.map(([q]) => q);
    expect(first.map((q) => q.status).sort()).toEqual([...ALL_STATUSES].sort());
    for (const q of first) {
      expect(q).toMatchObject({ skip: 0, take: 20 });
      expect(q.search ?? "").toBe("");
      expect(q.repositoryId ?? "").toBe("");
      expect(q.tags ?? []).toEqual([]);
    }
    expect(badge("Backlog")).toBe("45");
    expect(badge("Done")).toBe("3");
    expect(badge("Ready")).toBe("0");
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
      .filter((q) => q.status === WorkItemStatus.Backlog)
      .map((q) => q.skip);
    expect(backlogSkips).toEqual([0, 20, 40]);
    expect(badge("Backlog")).toBe("45");
    expect(server.getAll).not.toHaveBeenCalled();
  });

  test("search is debounced and finds an old item by id in its column, reloading every column from the top", async () => {
    mockHub();
    const server = mockBoard([
      boardItem("wi-forgotten", 0, WorkItemStatus.Backlog, { title: "Tidy up" }),
      ...Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, 10 + n)),
      boardItem("done-forgotten", 5, WorkItemStatus.Done, { title: "Shipped" }),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    expect(cardTitles("Backlog")).not.toContain("Tidy up");
    const before = server.getPage.mock.calls.length;

    const search = screen.getByLabelText("Search work items");
    fireEvent.change(search, { target: { value: "f" } });
    fireEvent.change(search, { target: { value: "forg" } });
    fireEvent.change(search, { target: { value: "forgotten" } });

    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["Tidy up"]));
    await waitFor(() => expect(cardTitles("Done")).toEqual(["Shipped"]));
    expect(badge("Backlog")).toBe("1");
    expect(badge("Done")).toBe("1");
    const reload = server.getPage.mock.calls.slice(before).map(([q]) => q);
    expect(reload.map((q) => q.status).sort()).toEqual([...ALL_STATUSES].sort());
    for (const q of reload) {
      expect(q.search).toBe("forgotten");
      expect(q.skip).toBe(0);
    }
  });

  test("a page answered for an outdated search never replaces the newer one", async () => {
    mockHub();
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

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(3));

    const search = screen.getByLabelText("Search work items");
    fireEvent.change(search, { target: { value: "first" } });
    await waitFor(() =>
      expect(server.getPage.mock.calls.some(([q]) => q.search === "first")).toBe(true),
    );
    fireEvent.change(search, { target: { value: "second" } });
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["second-match"]));

    held.resolve();
    await settle();

    expect(cardTitles("Backlog")).toEqual(["second-match"]);
    expect(badge("Backlog")).toBe("1");
  });

  test("repository and tag filters narrow every column, with options for every repository and every tag in use", async () => {
    mockHub();
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
    expect(tagged.map((q) => q.status).sort()).toEqual([...ALL_STATUSES].sort());
    for (const q of tagged) {
      expect(q.repositoryId).toBe("repo-2");
      expect(q.tags).toEqual(["ancient"]);
    }
  });
});

describe("Taskboard live changes keep server order", () => {
  test("creating a work item puts its card at the top of Backlog at once and counts it", async () => {
    mockHub();
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

    fireEvent.click(within(columnEl("Backlog")).getByRole("button", { name: "New item" }));
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
    const hub = mockHub();
    const server = mockBoard(Array.from({ length: 25 }, (_, n) => boardItem(`b${n}`, 10 + n)));

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Backlog")).toHaveLength(20));
    const created = (id: string, n: number) => {
      server.items.push(boardItem(id, n));
      return hub.emit("WorkItemStateChanged", {
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
    const hub = mockHub();
    const server = mockBoard([
      ...Array.from({ length: 5 }, (_, n) => boardItem(`a${n}`, n)),
      boardItem("elsewhere", 50, WorkItemStatus.Backlog, { repositoryId: "repo-2" }),
    ]);
    const held = deferred<void>();
    let holding = true;
    server.getPage.mockImplementation(async (q) => {
      const snapshot = server.page(q);
      if (holding && q.status === WorkItemStatus.Backlog && q.repositoryId === "repo-1") {
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
    await hub.emit("WorkItemStateChanged", {
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
    const hub = mockHub();
    const server = mockBoard([
      boardItem("r7", 7, WorkItemStatus.Ready),
      boardItem("u9", 9, WorkItemStatus.Running),
      boardItem("u2", 2, WorkItemStatus.Running),
    ]);

    renderTaskboard();
    await waitFor(() => expect(cardTitles("Running")).toEqual(["u9", "u2"]));

    server.items.find((wi) => wi.id === "r7")!.status = WorkItemStatus.Running;
    await hub.emit("WorkItemStateChanged", {
      workItemId: "r7",
      oldStatus: "Ready",
      newStatus: "Running",
    });

    await waitFor(() => expect(cardTitles("Running")).toEqual(["u9", "r7", "u2"]));
    expect(cardTitles("Ready")).toEqual([]);
    await waitFor(() => expect(badge("Running")).toBe("3"));
    expect(badge("Ready")).toBe("0");
  });

  test("under a filter a card edited out of it leaves and a non-matching new item never shows", async () => {
    const hub = mockHub();
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
    await hub.emit("WorkItemRunProgressed", { workItemId: "moves-repo" });
    await waitFor(() => expect(cardTitles("Backlog")).toEqual(["keep"]));
    await waitFor(() => expect(badge("Backlog")).toBe("1"));

    server.items.push(
      boardItem("new-elsewhere", 100, WorkItemStatus.Backlog, { repositoryId: "repo-2" }),
    );
    await hub.emit("WorkItemStateChanged", {
      workItemId: "new-elsewhere",
      oldStatus: "Backlog",
      newStatus: "Backlog",
    });
    await settle();
    expect(cardTitles("Backlog")).toEqual(["keep"]);
    expect(badge("Backlog")).toBe("1");
  });

  test("a keyboard move takes the card from its column to the next and both totals follow", async () => {
    mockHub();
    mockBoard([
      boardItem("mover", 5, WorkItemStatus.Ready),
      boardItem("u9", 9, WorkItemStatus.Running),
    ]);

    renderTaskboard();
    const card = await screen.findByRole("button", { name: /^mover,/ });
    card.focus();
    fireEvent.keyDown(card, { key: "ArrowRight" });

    await waitFor(() => expect(cardTitles("Running")).toEqual(["u9", "mover"]));
    expect(cardTitles("Ready")).toEqual([]);
    await waitFor(() => expect(badge("Running")).toBe("2"));
    expect(badge("Ready")).toBe("0");
  });

  test("totals reconcile from the server with one counts request in flight and one follow-up", async () => {
    const hub = mockHub();
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

    // Deleted by someone else, with no event for it: only a count can tell.
    server.items.splice(
      server.items.findIndex((wi) => wi.id === "b0"),
      1,
    );
    await hub.emit("WorkItemRunProgressed", { workItemId: "b1" });
    await waitFor(() => expect(server.getCounts.mock.calls.length).toBe(countsBefore + 1));
    await hub.emit("WorkItemRunProgressed", { workItemId: "b2" });
    await hub.emit("WorkItemRunProgressed", { workItemId: "b3" });
    await settle();
    expect(server.getCounts.mock.calls.length).toBe(countsBefore + 1);

    held.resolve();
    await waitFor(() => expect(server.getCounts.mock.calls.length).toBe(countsBefore + 2));
    await settle();
    expect(server.getCounts.mock.calls.length).toBe(countsBefore + 2);
    await waitFor(() => expect(badge("Backlog")).toBe("5"));
  });

  test("a counts answer for an outdated filter is dropped", async () => {
    const hub = mockHub();
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
    await hub.emit("WorkItemRunProgressed", { workItemId: "b1" });
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
    mockHub();
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
