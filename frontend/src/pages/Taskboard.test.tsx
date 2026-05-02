import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Taskboard from "./Taskboard";
import { WorkItemStatus, WorkItemPriority, WorkItem } from "../types";
import * as authServices from "../services/auth";
import * as signalRHook from "../hooks/useSignalR";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

beforeEach(() => {
  localStorage.clear();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Item",
    description: "desc",
    status: WorkItemStatus.Ready,
    priority: WorkItemPriority.Medium,
    labels: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    pullRequestUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    dependencyIds: [],
    dependentIds: [],
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

    render(
      <MemoryRouter>
        <Taskboard />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    const humanFeedbackHandlers = handlers["HumanFeedbackRequired"];
    expect(humanFeedbackHandlers).toBeDefined();
    expect(humanFeedbackHandlers!.length).toBeGreaterThan(0);

    humanFeedbackHandlers![0]({
      payload: { workItemId: "wi-1", reason: "PR Awaiting Merge" },
    });

    await waitFor(() => {
      expect(screen.getByText("PR Awaiting Merge")).toBeTruthy();
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

    render(
      <MemoryRouter>
        <Taskboard />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    handlers["HumanFeedbackRequired"]![0]({
      payload: { workItemId: "wi-1", reason: "Node Failed" },
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

    render(
      <MemoryRouter>
        <Taskboard />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Test Item")).toBeTruthy();
    });

    handlers["HumanFeedbackRequired"]![0]({
      payload: { workItemId: "wi-1", reason: "Node Failed" },
    });

    await new Promise((r) => setTimeout(r, 100));
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

    const { default: Taskboard } = await import("./Taskboard");
    render(
      <MemoryRouter>
        <Taskboard />
      </MemoryRouter>,
    );

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
