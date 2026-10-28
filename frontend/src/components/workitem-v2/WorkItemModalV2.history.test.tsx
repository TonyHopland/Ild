import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { WorkItem, WorkItemStatus } from "../../types";
import type { WorkItemHistoryEntry } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: 0,
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
    latestLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function mockServices(history: () => Promise<WorkItemHistoryEntry[]>) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getById").mockImplementation(async (id: string) =>
    makeWorkItem({ id }),
  );
  vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation(async (runId) => ({
    runId,
    messages: [],
    lastEventId: null,
  }));
  return vi.spyOn(authServices.workItemService, "getHistory").mockImplementation(history);
}

function dialog(workItem: WorkItem) {
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>
  );
}

async function renderDialog(workItem: WorkItem) {
  let result!: ReturnType<typeof render>;
  await act(async () => {
    result = render(dialog(workItem), { wrapper: WorkItemHubProvider });
    await Promise.resolve();
  });
  return result;
}

const text = (el: Element) => (el.textContent ?? "").replace(/\s+/g, " ").trim();

describe("WorkItemModalV2 History tab", () => {
  const tabNames = () =>
    screen.getAllByRole("tab").map((t) =>
      text(t)
        .replace(/\s*\(\d+\)/, "")
        .replace(/\s*●/, ""),
    );

  test("History is the last tab, after Terminal when there is one", async () => {
    mockServices(async () => []);
    const view = await renderDialog(makeWorkItem());
    expect(tabNames()).toEqual(["Overview", "Action", "Runs", "Files", "Preview", "History"]);
    view.unmount();

    await renderDialog(
      makeWorkItem({ id: "wi-2", currentLoopRunId: "run-1", worktreePath: "/worktrees/wi-2" }),
    );
    expect(tabNames()).toEqual([
      "Overview",
      "Action",
      "Runs",
      "Files",
      "Preview",
      "Terminal",
      "History",
    ]);
  });
});
