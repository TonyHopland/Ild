import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import WorkItemModalV2 from "./WorkItemModalV2";
import {
  WorkItemStatus,
  WorkItemPriority,
  WorkItem,
  LoopRun,
  LoopRunStatus,
  LoopRunNodeStatus,
} from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "A **test** item",
    status: WorkItemStatus.Ready,
    priority: WorkItemPriority.Medium,
    tags: ["build"],
    conversation: [
      { role: "Human", content: "hello", timestamp: "2025-01-01T00:00:00Z", name: null },
    ],
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

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Completed,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 2,
    startedAt: "2025-01-02T00:00:00Z",
    completedAt: "2025-01-02T01:00:00Z",
    nodes: [
      {
        id: "rn-1",
        nodeId: "n-1",
        nodeLabel: "Implement",
        status: LoopRunNodeStatus.Succeeded,
        effectiveInput: JSON.stringify({ prompt: "do the thing" }),
        output: "done",
        error: null,
        startedAt: "2025-01-02T00:00:00Z",
        completedAt: "2025-01-02T00:30:00Z",
        executionCount: 1,
      },
    ],
    ...overrides,
  };
}

function mockServices(runs: LoopRun[] = [makeRun()]) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  });
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([
    {
      id: "repo-1",
      name: "my-repo",
      remoteProviderId: "rp-1",
      cloneUrl: "",
      defaultBranch: null,
      worktreesPath: null,
      defaultIntakeStatus: WorkItemStatus.Backlog,
      createdAt: "2025-01-01T00:00:00Z",
    },
  ]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue(runs);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(runs[0] ?? makeRun());
}

async function renderDialog(
  workItem: WorkItem,
  props: Partial<React.ComponentProps<typeof WorkItemModalV2>> = {},
) {
  await act(async () => {
    render(
      <MemoryRouter>
        <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} {...props} />
      </MemoryRouter>,
    );
    await Promise.resolve();
  });
}

describe("WorkItemModalV2", () => {
  test("renders header, tabs and overview", async () => {
    mockServices();
    await renderDialog(makeWorkItem());

    expect(screen.getByText("Test Work Item")).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Overview" })).toBeTruthy();
    expect(screen.getByRole("tab", { name: /Runs/ })).toBeTruthy();
    expect(screen.getByRole("tab", { name: /Conversation/ })).toBeTruthy();
    expect(screen.getByRole("tab", { name: /Preview/ })).toBeTruthy();
    // Overview shows the description by default.
    expect(screen.getByText("Description")).toBeTruthy();
  });

  test("runs tab shows run list and inline node timeline", async () => {
    mockServices();
    await renderDialog(makeWorkItem());

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Runs/ }));
      await Promise.resolve();
    });

    expect(screen.getByText(/node executions/)).toBeTruthy();
    expect(screen.getByText("Implement")).toBeTruthy();
    expect(screen.getByText("Open full run view ↗")).toBeTruthy();

    // Expanding a node reveals its input and output.
    await act(async () => {
      fireEvent.click(screen.getByText("Implement"));
      await Promise.resolve();
    });
    expect(screen.getByText("do the thing")).toBeTruthy();
    expect(screen.getByText("done")).toBeTruthy();
  });

  test("conversation tab shows messages", async () => {
    mockServices();
    await renderDialog(makeWorkItem());

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Conversation/ }));
      await Promise.resolve();
    });

    expect(screen.getByText("hello")).toBeTruthy();
  });

  test("overview shows work item metadata", async () => {
    mockServices();
    await renderDialog(makeWorkItem());

    // Repository name resolves from the mocked repository service.
    expect(await screen.findByText("my-repo")).toBeTruthy();
    expect(screen.getByText("Dependencies")).toBeTruthy();
  });

  test("feedback banner is pinned while waiting on a human", async () => {
    mockServices();
    await renderDialog(
      makeWorkItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
        currentLoopRunId: "run-1",
      }),
    );

    expect(screen.getByText("Human Feedback")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reject" })).toBeTruthy();
  });

  test("edit button switches to the inline edit form", async () => {
    mockServices();
    await renderDialog(makeWorkItem());

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      await Promise.resolve();
    });

    expect(screen.getByLabelText("Title")).toBeTruthy();
    expect((screen.getByLabelText("Title") as HTMLInputElement).value).toBe("Test Work Item");
  });

  test("Escape closes immediately when there are no unsaved changes", async () => {
    mockServices();
    const onClose = vi.fn();
    await renderDialog(makeWorkItem(), { onClose });

    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      await Promise.resolve();
    });

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Discard unsaved changes/)).toBeNull();
  });

  test("Escape with unsaved edits prompts to discard instead of closing", async () => {
    mockServices();
    const onClose = vi.fn();
    await renderDialog(makeWorkItem(), { onClose });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Changed title" } });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      await Promise.resolve();
    });

    // Dialog is still open and asks before discarding.
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText(/Discard unsaved changes/)).toBeTruthy();

    // Confirming the discard finally closes.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Discard" }));
      await Promise.resolve();
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  test("expanded run node survives a tab switch (panels stay mounted)", async () => {
    mockServices();
    await renderDialog(makeWorkItem());

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Runs/ }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByText("Implement"));
      await Promise.resolve();
    });
    expect(screen.getByText("done")).toBeTruthy();

    // Hop to another tab and back — the node should still be expanded without
    // re-clicking, because the panel was not unmounted.
    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Conversation/ }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Runs/ }));
      await Promise.resolve();
    });
    expect(screen.getByText("done")).toBeTruthy();
  });
});
