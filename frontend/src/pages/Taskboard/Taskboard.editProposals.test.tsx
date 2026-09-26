import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import Taskboard from "./index";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Old backlog item",
    description: "",
    status: WorkItemStatus.Backlog,
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

function renderTaskboard() {
  return render(
    <MemoryRouter initialEntries={["/taskboard"]}>
      <Routes>
        <Route path="/taskboard" element={<Taskboard />} />
        <Route path="/taskboard/:workItemId" element={<Taskboard />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("the board's pending edit proposal indicator", () => {
  test("shows the pending count on the card and follows the item as proposals are decided", async () => {
    const handlers: Record<string, ((msg: { payload: unknown }) => void)[]> = {};
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn((event: string, handler: (msg: { payload: unknown }) => void) => {
        (handlers[event] ??= []).push(handler);
      }),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    } as unknown as ReturnType<typeof signalRHook.useSignalR>);
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([
      makeItem({ pendingEditProposalCount: 2 }),
      makeItem({ id: "wi-2", title: "Untouched item", pendingEditProposalCount: 0 }),
    ]);
    const getById = vi
      .spyOn(authServices.workItemService, "getById")
      .mockResolvedValue(makeItem({ pendingEditProposalCount: 0 }));

    renderTaskboard();

    expect(await screen.findByTitle("2 proposed edits")).toBeTruthy();
    expect(screen.queryAllByTitle(/proposed edit/)).toHaveLength(1);

    await act(async () => {
      handlers["WorkItemEditProposalsChanged"]?.forEach((h) =>
        h({ payload: { workItemId: "wi-1" } }),
      );
    });

    await waitFor(() => expect(screen.queryByTitle(/proposed edit/)).toBeNull());
    expect(getById).toHaveBeenCalledWith("wi-1");
    expect(screen.getByText("Old backlog item")).toBeTruthy();
  });
});
