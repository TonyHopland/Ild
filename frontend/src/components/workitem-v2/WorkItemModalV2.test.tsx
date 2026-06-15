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
  LoopTemplate,
  RecoveryPolicy,
} from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

// The real terminal spins up an xterm instance and a WebSocket, neither of which
// jsdom provides. Stub it with a marker that echoes the props the modal passes so
// the tab wiring (open/close, embedded rendering, session persistence) can be
// asserted without a live PTY.
vi.mock("../LoopRunTerminal", () => ({
  default: ({
    loopRunId,
    embedded,
    onClose,
  }: {
    loopRunId: string;
    embedded?: boolean;
    onClose: () => void;
  }) => (
    <div
      data-testid="embedded-terminal"
      data-loop-run-id={loopRunId}
      data-embedded={String(!!embedded)}
    >
      <button type="button" onClick={onClose}>
        terminal-close
      </button>
    </div>
  ),
}));

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

function makeTemplate(overrides: Partial<LoopTemplate> = {}): LoopTemplate {
  return {
    id: "tmpl-1",
    name: "build-loop",
    description: "",
    version: 1,
    recoveryPolicy: RecoveryPolicy.AutoResume,
    nodes: [],
    edges: [],
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
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

  test("opens on the Overview tab even for a running item", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1" }));

    expect(screen.getByRole("tab", { name: "Overview" }).getAttribute("aria-selected")).toBe(
      "true",
    );
    expect(screen.getByRole("tab", { name: /Runs/ }).getAttribute("aria-selected")).toBe("false");
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

  test("runs tab retry button restarts the run from a node and refreshes", async () => {
    mockServices();
    const retrySpy = vi
      .spyOn(authServices.loopRunService, "retryFromNode")
      .mockResolvedValue(undefined);
    const getRunsSpy = vi.spyOn(authServices.workItemService, "getRuns");
    await renderDialog(makeWorkItem());

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Runs/ }));
      await Promise.resolve();
    });

    const retryButton = await screen.findByRole("button", { name: "Retry from this node" });
    expect((retryButton as HTMLButtonElement).disabled).toBe(false);

    // getRuns is called once on mount; the retry should trigger a refresh.
    const callsBeforeRetry = getRunsSpy.mock.calls.length;

    await act(async () => {
      fireEvent.click(retryButton);
      await Promise.resolve();
    });

    expect(retrySpy).toHaveBeenCalledWith("run-1", "rn-1");
    expect(getRunsSpy.mock.calls.length).toBeGreaterThan(callsBeforeRetry);
  });

  test("runs tab retry is disabled while the run is actively executing", async () => {
    // A running, non-paused run cannot be retried — its node is shown but the
    // retry button is disabled.
    const runningRun = makeRun({
      status: LoopRunStatus.Running,
      completedAt: null,
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
    });
    mockServices([runningRun]);
    const retrySpy = vi
      .spyOn(authServices.loopRunService, "retryFromNode")
      .mockResolvedValue(undefined);
    await renderDialog(makeWorkItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1" }));

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Runs/ }));
      await Promise.resolve();
    });

    const retryButton = await screen.findByRole("button", { name: "Retry from this node" });
    expect((retryButton as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      fireEvent.click(retryButton);
      await Promise.resolve();
    });
    expect(retrySpy).not.toHaveBeenCalled();
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

  test("overview shows the loop name and current node while the item is mid run", async () => {
    const run = makeRun({
      status: LoopRunStatus.Running,
      completedAt: null,
      currentNodeId: "n-1",
    });
    mockServices([run]);
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([
      makeTemplate({ id: "tmpl-1", name: "build-loop" }),
    ]);
    await renderDialog(makeWorkItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1" }));

    const loopLabel = await screen.findByText("Loop");
    const row = loopLabel.parentElement;
    expect(row?.textContent).toContain("build-loop");
    // The current node label is shown beside the loop name.
    expect(row?.textContent).toContain("Implement");
  });

  test("overview shows the loop name without a node when the run has no current node", async () => {
    const run = makeRun({
      status: LoopRunStatus.Running,
      completedAt: null,
      currentNodeId: null,
    });
    mockServices([run]);
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([
      makeTemplate({ id: "tmpl-1", name: "build-loop" }),
    ]);
    await renderDialog(makeWorkItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1" }));

    const loopLabel = await screen.findByText("Loop");
    const row = loopLabel.parentElement;
    expect(row?.textContent).toContain("build-loop");
    expect(row?.textContent).not.toContain("·");
  });

  test("overview hides the loop row when the item is not mid run", async () => {
    const run = makeRun({ currentNodeId: "n-1" });
    mockServices([run]);
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([
      makeTemplate({ id: "tmpl-1", name: "build-loop" }),
    ]);
    await renderDialog(makeWorkItem({ status: WorkItemStatus.Done, currentLoopRunId: "run-1" }));

    // Overview is rendered (repository name resolves) but the loop row is absent.
    expect(await screen.findByText("my-repo")).toBeTruthy();
    expect(screen.queryByText("Loop")).toBeNull();
  });

  test("overview push branch button commits and pushes, then shows the branch", async () => {
    mockServices();
    const pushSpy = vi
      .spyOn(authServices.workItemService, "pushBranch")
      .mockResolvedValue({ branch: "ild/wi-1-run-1" });
    await renderDialog(
      makeWorkItem({ branchName: "ild/wi-1-run-1", worktreePath: "/tmp/wt/wi-1" }),
    );

    const pushButton = screen.getByRole("button", { name: "Push branch" });

    await act(async () => {
      fireEvent.click(pushButton);
      await Promise.resolve();
    });

    expect(pushSpy).toHaveBeenCalledWith("wi-1");
    expect(screen.getByText("Pushed ild/wi-1-run-1 to origin.")).toBeTruthy();
  });

  test("overview push branch button surfaces the error when the push fails", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "pushBranch").mockRejectedValue({
      message: "Failed to push branch 'ild/wi-1-run-1': no upstream",
    });
    await renderDialog(
      makeWorkItem({ branchName: "ild/wi-1-run-1", worktreePath: "/tmp/wt/wi-1" }),
    );

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Push branch" }));
      await Promise.resolve();
    });

    expect(screen.getByText("Failed to push branch 'ild/wi-1-run-1': no upstream")).toBeTruthy();
  });

  test("overview hides push branch button when there is no worktree", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ branchName: "ild/wi-1-run-1", worktreePath: null }));

    expect(screen.queryByRole("button", { name: "Push branch" })).toBeNull();
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

  test("footer never shows a Mark Merged button, even when a PR URL is set", async () => {
    mockServices();
    await renderDialog(
      makeWorkItem({
        status: WorkItemStatus.HumanFeedback,
        currentLoopRunId: "run-1",
        prUrl: "https://forgejo.example.com/repo/pull/42",
      }),
    );

    expect(screen.queryByRole("button", { name: "Mark Merged" })).toBeNull();
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

  test("shows a Terminal tab only once the run has a worktree", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ currentLoopRunId: "run-1", worktreePath: "/tmp/wt/wi-1" }));
    expect(screen.getByRole("tab", { name: /Terminal/ })).toBeTruthy();
  });

  test("hides the Terminal tab when the run has no worktree", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ currentLoopRunId: "run-1", worktreePath: null }));
    expect(screen.queryByRole("tab", { name: /Terminal/ })).toBeNull();
  });

  test("opens the terminal in its tab and keeps the session mounted across tab switches", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ currentLoopRunId: "run-1", worktreePath: "/tmp/wt/wi-1" }));

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Terminal/ }));
      await Promise.resolve();
    });
    // The tab opens on a prompt, not a live session.
    expect(screen.getByRole("button", { name: "Open Terminal" })).toBeTruthy();
    expect(screen.queryByTestId("embedded-terminal")).toBeNull();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Open Terminal" }));
      await Promise.resolve();
    });
    const term = screen.getByTestId("embedded-terminal");
    // Rendered inline inside the tab, wired to the current run.
    expect(term.getAttribute("data-embedded")).toBe("true");
    expect(term.getAttribute("data-loop-run-id")).toBe("run-1");

    // Navigating to another tab must not tear down the session — the panel is
    // only hidden, so the same terminal node stays mounted.
    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: "Overview" }));
      await Promise.resolve();
    });
    const stillThere = screen.getByTestId("embedded-terminal");
    expect(stillThere).toBe(term);
    expect(stillThere.closest("[role='tabpanel']")?.hasAttribute("hidden")).toBe(true);
  });

  test("closing the terminal tears down the session and returns to the open prompt", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ currentLoopRunId: "run-1", worktreePath: "/tmp/wt/wi-1" }));

    await act(async () => {
      fireEvent.click(screen.getByRole("tab", { name: /Terminal/ }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Open Terminal" }));
      await Promise.resolve();
    });
    expect(screen.getByTestId("embedded-terminal")).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "terminal-close" }));
      await Promise.resolve();
    });
    expect(screen.queryByTestId("embedded-terminal")).toBeNull();
    expect(screen.getByRole("button", { name: "Open Terminal" })).toBeTruthy();
  });
});
