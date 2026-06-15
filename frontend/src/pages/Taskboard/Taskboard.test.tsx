import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, within, cleanup, waitFor, act, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route, useLocation } from "react-router-dom";
import Taskboard from "./index";
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

    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);

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

    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([
      makeItem({ status: WorkItemStatus.Running }),
    ]);
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

    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([
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

    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([
      makeItem({ status: WorkItemStatus.HumanFeedback }),
    ]);
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

    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);

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

    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);

    renderTaskboard();

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    await dispatchSignalR(handlers["HumanFeedbackRequired"]![0], {
      workItemId: "wi-1",
      reason: "Node Failed",
    });

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 100));
    });
    expect(notificationCalls).toHaveLength(0);
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([
      makeItem({ status: WorkItemStatus.Ready }),
    ]);
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([initialItem]);
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);

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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);

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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);

    renderTaskboard("/taskboard/wi-1");

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeTruthy();
    });

    fireEvent.keyDown(document, { key: "Escape" });

    await waitFor(() => {
      expect(screen.queryByRole("dialog")).toBeNull();
    });
    expect(screen.getByTestId("location").textContent).toBe("/taskboard");
  });

  test("redirects to the taskboard when the URL points at a missing work item", async () => {
    mockSignalR();
    mockModalServices();
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([makeItem()]);
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue(items);

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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue(items);

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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([
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
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue(items);

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
