import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act } from "@testing-library/react";
import FilesPanel from "./FilesPanel";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Running,
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
    dependencyIds: [],
    dependentIds: [],
    worktreePath: "/tmp/wt",
    branchName: "ild/wi-1",
    ...overrides,
  };
}

async function renderPanel(workItem: WorkItem) {
  await act(async () => {
    render(<FilesPanel workItem={workItem} />);
    await Promise.resolve();
  });
}

describe("FilesPanel", () => {
  test("shows the file tree and filters to changes in PR mode", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [
        { path: "src/changed.ts", changeStatus: "modified" },
        { path: "src/untouched.ts", changeStatus: "none" },
      ],
    });

    await renderPanel(makeWorkItem());

    expect(screen.getByText("changed.ts")).toBeTruthy();
    expect(screen.getByText("untouched.ts")).toBeTruthy();

    // Switching to "Changes" hides files with no diff from the base branch.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Changes \(1\)/ }));
      await Promise.resolve();
    });

    expect(screen.getByText("changed.ts")).toBeTruthy();
    expect(screen.queryByText("untouched.ts")).toBeNull();
  });

  test("loads file content and toggles between code and diff views", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "a.ts", changeStatus: "modified" }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockResolvedValue({
      path: "a.ts",
      changeStatus: "modified",
      content: "line1\nline2",
      diff: "@@ -1 +1 @@\n-old\n+line1",
      isBinary: false,
    });

    await renderPanel(makeWorkItem());

    await act(async () => {
      fireEvent.click(screen.getByText("a.ts"));
      await Promise.resolve();
    });

    expect(authServices.workItemService.getFileContent).toHaveBeenCalledWith("wi-1", "a.ts");
    expect(screen.getByText("line1")).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Diff" }));
      await Promise.resolve();
    });

    expect(screen.getByText("+line1")).toBeTruthy();
    expect(screen.getByText("-old")).toBeTruthy();
  });

  test("renders an empty state when there is no worktree", async () => {
    const getFiles = vi.spyOn(authServices.workItemService, "getFiles");
    await renderPanel(makeWorkItem({ worktreePath: null }));

    expect(screen.getByText(/No worktree/)).toBeTruthy();
    expect(getFiles).not.toHaveBeenCalled();
  });
});
