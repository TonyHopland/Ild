import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor, act, fireEvent } from "@testing-library/react";
import CombinedPreviewDrawer from "./CombinedPreviewDrawer";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  CombinedPreview,
  CombinedPreviewMember,
} from "../types";
import * as authServices from "../services/auth";
import * as signalRHook from "../hooks/useSignalR";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "1",
    title: "Item",
    description: "",
    status: WorkItemStatus.Running,
    priority: WorkItemPriority.Medium,
    tags: [],
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    branchName: "ild/wi-1-run-a",
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function member(overrides: Partial<CombinedPreviewMember> = {}): CombinedPreviewMember {
  return {
    workItemId: "1",
    title: "Item",
    branchName: "ild/wi-1-run-a",
    mergeStatus: "pending",
    conflictedFiles: [],
    stale: false,
    ...overrides,
  };
}

function combined(overrides: Partial<CombinedPreview> = {}): CombinedPreview {
  return {
    integrationBranch: "ild/combined-1-2",
    state: "notStarted",
    stale: false,
    worktreePath: null,
    message: null,
    members: [
      member({ workItemId: "1" }),
      member({ workItemId: "2", branchName: "ild/wi-2-run-b" }),
    ],
    preview: null,
    ...overrides,
  };
}

// SignalR is mocked to a no-op so the drawer renders without a live hub; tests
// that need an event capture the registered handler via the returned mockOn.
function mockSignalR() {
  const handlers: Record<string, ((msg: any) => void)[]> = {};
  const on = vi.fn((eventType: string, handler: (msg: any) => void) => {
    handlers[eventType] = handlers[eventType] || [];
    handlers[eventType].push(handler);
  });
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on,
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  });
  return handlers;
}

async function renderDrawer(items: WorkItem[]) {
  await act(async () => {
    render(<CombinedPreviewDrawer items={items} onClose={() => {}} />);
    await Promise.resolve();
  });
}

describe("CombinedPreviewDrawer", () => {
  test("starts a combined preview and renders its services", async () => {
    mockSignalR();
    vi.spyOn(authServices.combinedPreviewService, "get").mockResolvedValue(combined());
    const start = vi.spyOn(authServices.combinedPreviewService, "start").mockResolvedValue(
      combined({
        state: "running",
        worktreePath: "/tmp/wt",
        members: [
          member({ mergeStatus: "clean" }),
          member({ workItemId: "2", mergeStatus: "clean" }),
        ],
        preview: {
          configured: true,
          state: "running",
          worktreePath: "/tmp/wt",
          configPath: null,
          profileName: "default",
          publicHost: "127.0.0.1",
          stateDirectory: null,
          message: null,
          services: [
            {
              name: "web",
              portAlias: "web",
              status: "running",
              port: 3000,
              suggestedPort: null,
              healthUrl: null,
              publicUrl: "http://127.0.0.1:3000",
              logFilePath: null,
              processId: 1,
              exitCode: null,
            },
          ],
        },
      }),
    );

    await renderDrawer([makeItem({ id: "1" }), makeItem({ id: "2" })]);

    await act(async () => {
      fireEvent.click(screen.getByText("Start preview"));
      await Promise.resolve();
    });

    expect(start).toHaveBeenCalledWith(["1", "2"], { skip: [], onConflict: undefined });
    await waitFor(() => {
      expect(screen.getByText(/web: running on :3000/)).toBeTruthy();
    });
    expect(screen.getByText("http://127.0.0.1:3000")).toBeTruthy();
    expect(screen.getByText("Stop preview")).toBeTruthy();
  });

  test("surfaces a conflict and skipping the member re-starts without it", async () => {
    mockSignalR();
    vi.spyOn(authServices.combinedPreviewService, "get").mockResolvedValue(combined());
    const start = vi.spyOn(authServices.combinedPreviewService, "start");

    // First start conflicts on member 2; after skipping it, the rest previews.
    start.mockResolvedValueOnce(
      combined({
        state: "conflict",
        worktreePath: "/tmp/wt",
        message: "Merge conflict in #2 (ild/wi-2-run-b).",
        members: [
          member({ workItemId: "1", mergeStatus: "clean" }),
          member({
            workItemId: "2",
            branchName: "ild/wi-2-run-b",
            mergeStatus: "conflict",
            conflictedFiles: ["base.txt"],
          }),
        ],
      }),
    );
    start.mockResolvedValueOnce(
      combined({
        state: "partial",
        worktreePath: "/tmp/wt",
        members: [
          member({ workItemId: "1", mergeStatus: "clean" }),
          member({ workItemId: "2", branchName: "ild/wi-2-run-b", mergeStatus: "skipped" }),
        ],
      }),
    );

    await renderDrawer([makeItem({ id: "1" }), makeItem({ id: "2" })]);

    await act(async () => {
      fireEvent.click(screen.getByText("Start preview"));
      await Promise.resolve();
    });

    await waitFor(() => {
      expect(screen.getByText("Merge conflict")).toBeTruthy();
    });
    expect(screen.getByText("base.txt")).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByText("Skip #2"));
      await Promise.resolve();
    });

    expect(start).toHaveBeenLastCalledWith(["1", "2"], { skip: ["2"], onConflict: undefined });
    await waitFor(() => {
      expect(screen.getByText("Partial preview")).toBeTruthy();
    });
  });

  test("shows a stale indicator and offers a rebuild", async () => {
    mockSignalR();
    vi.spyOn(authServices.combinedPreviewService, "get").mockResolvedValue(
      combined({
        state: "running",
        stale: true,
        worktreePath: "/tmp/wt",
        members: [
          member({ workItemId: "1", mergeStatus: "clean", stale: true }),
          member({ workItemId: "2", branchName: "ild/wi-2-run-b", mergeStatus: "clean" }),
        ],
        preview: {
          configured: true,
          state: "running",
          worktreePath: "/tmp/wt",
          configPath: null,
          profileName: null,
          publicHost: null,
          stateDirectory: null,
          message: null,
          services: [],
        },
      }),
    );

    await renderDrawer([makeItem({ id: "1" }), makeItem({ id: "2" })]);

    await waitFor(() => {
      expect(screen.getByText("Stale")).toBeTruthy();
    });
    expect(screen.getByText("Rebuild")).toBeTruthy();
  });
});
