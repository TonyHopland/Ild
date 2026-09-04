import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";
import RunsPanel from "./RunsPanel";
import { loopRunService } from "../../services/auth";
import { WorkItem, WorkItemStatus, WorkItemPriority, LoopRun, LoopRunStatus } from "../../types";

vi.mock("react-router", () => ({
  Link: ({ children, ...rest }: { children: React.ReactNode }) => <a {...rest}>{children}</a>,
}));

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function workItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags: [],
    conversation: [],
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
    worktreePath: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function run(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Completed,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 2,
    hasLocalGitState: true,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: "2025-01-01T01:00:00Z",
    nodes: [],
    ...overrides,
  };
}

function renderPanel(detail: LoopRun, onReclaimRun = vi.fn().mockResolvedValue(undefined)) {
  const getById = vi.spyOn(loopRunService, "getById").mockResolvedValue(detail);
  const onRunsChanged = vi.fn();
  render(
    <RunsPanel
      workItem={workItem()}
      runs={[detail]}
      progressText=""
      onRunsChanged={onRunsChanged}
      onReclaimRun={onReclaimRun}
    />,
  );
  return { getById, onReclaimRun, onRunsChanged };
}

const cleanUpButton = () => screen.queryByRole("button", { name: /clean up worktree/i });

describe("RunsPanel cleanup action", () => {
  test("confirms, then frees the run's worktree and branch and refetches", async () => {
    const { onReclaimRun, onRunsChanged, getById } = renderPanel(run());

    await waitFor(() => expect(cleanUpButton()).not.toBeNull());
    fireEvent.click(cleanUpButton()!);

    // The action is destructive on disk, so nothing happens before confirming.
    expect(onReclaimRun).not.toHaveBeenCalled();
    getById.mockResolvedValue(run({ hasLocalGitState: false }));
    fireEvent.click(screen.getByRole("button", { name: /confirm clean up/i }));

    await waitFor(() => expect(onReclaimRun).toHaveBeenCalledWith("run-1"));
    await waitFor(() => expect(onRunsChanged).toHaveBeenCalled());
    // Once reclaimed there is nothing left to reclaim.
    await waitFor(() => expect(cleanUpButton()).toBeNull());
  });

  test("shows why a refused cleanup did not happen", async () => {
    const onReclaimRun = vi.fn().mockRejectedValue(new Error("Could not reclaim the worktree"));
    renderPanel(run(), onReclaimRun);

    await waitFor(() => expect(cleanUpButton()).not.toBeNull());
    fireEvent.click(cleanUpButton()!);
    fireEvent.click(screen.getByRole("button", { name: /confirm clean up/i }));

    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toContain("Could not reclaim the worktree"),
    );
  });

  test("is not offered for a run that is still live", async () => {
    renderPanel(run({ status: LoopRunStatus.WaitingHuman, completedAt: null }));
    await waitFor(() => expect(screen.getByText(/waitinghuman/i)).not.toBeNull());
    expect(cleanUpButton()).toBeNull();
  });

  test("is not offered once the run holds no local git state", async () => {
    renderPanel(run({ hasLocalGitState: false }));
    await waitFor(() => expect(screen.getByText(/open full run view/i)).not.toBeNull());
    expect(cleanUpButton()).toBeNull();
  });
});
