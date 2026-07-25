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

    // Folders start collapsed — open src to reveal its files.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /src/ }));
      await Promise.resolve();
    });

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

  test("starts with every folder collapsed and expands on demand", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "src/nested/deep.ts", changeStatus: "none" }],
    });

    await renderPanel(makeWorkItem());

    // The top-level folder shows, but its contents stay hidden until expanded.
    expect(screen.getByText("src")).toBeTruthy();
    expect(screen.queryByText("nested")).toBeNull();
    expect(screen.queryByText("deep.ts")).toBeNull();

    // Expanding reveals the next level — which is itself still collapsed.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /src/ }));
      await Promise.resolve();
    });
    expect(screen.getByText("nested")).toBeTruthy();
    expect(screen.queryByText("deep.ts")).toBeNull();
  });

  test("expands folders by default in the Changes view but stays collapsible", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [
        { path: "src/changed.ts", changeStatus: "modified" },
        { path: "src/untouched.ts", changeStatus: "none" },
      ],
    });

    await renderPanel(makeWorkItem());

    // The All-files view starts collapsed, so the file is hidden under src.
    expect(screen.queryByText("changed.ts")).toBeNull();

    // The Changes view starts expanded — changed files show without a click.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Changes \(1\)/ }));
      await Promise.resolve();
    });
    expect(screen.getByText("changed.ts")).toBeTruthy();

    // It can still be collapsed from there.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /src/ }));
      await Promise.resolve();
    });
    expect(screen.queryByText("changed.ts")).toBeNull();
  });

  test("refreshes the file list and open file when the work item updates", async () => {
    const getFiles = vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "a.ts", changeStatus: "modified" }],
    });
    const getFileContent = vi
      .spyOn(authServices.workItemService, "getFileContent")
      .mockResolvedValue({
        path: "a.ts",
        changeStatus: "modified",
        content: "before",
        diff: null,
        isBinary: false,
      });

    const workItem = makeWorkItem();
    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={workItem} />);
      await Promise.resolve();
    });

    await act(async () => {
      fireEvent.click(screen.getByText("a.ts"));
      await Promise.resolve();
    });
    expect(screen.getByText("before")).toBeTruthy();

    // The worktree changes underneath: a new file appears and a.ts is rewritten.
    getFiles.mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [
        { path: "a.ts", changeStatus: "modified" },
        { path: "b.ts", changeStatus: "added" },
      ],
    });
    getFileContent.mockResolvedValue({
      path: "a.ts",
      changeStatus: "modified",
      content: "after",
      diff: null,
      isBinary: false,
    });

    // The parent refetches the work item and hands down a fresh object (same id).
    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem }} />);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByText("b.ts")).toBeTruthy();
    expect(screen.getByText("after")).toBeTruthy();
    expect(screen.queryByText("before")).toBeNull();
  });

  test("keeps expanded folders open across a background refresh", async () => {
    const getFiles = vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "src/a.ts", changeStatus: "none" }],
    });

    const workItem = makeWorkItem();
    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={workItem} />);
      await Promise.resolve();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /src/ }));
      await Promise.resolve();
    });
    expect(screen.getByText("a.ts")).toBeTruthy();

    // A background refresh brings in a new sibling file under the same folder.
    getFiles.mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [
        { path: "src/a.ts", changeStatus: "none" },
        { path: "src/b.ts", changeStatus: "added" },
      ],
    });

    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem }} />);
      await Promise.resolve();
      await Promise.resolve();
    });

    // The folder the user opened stays open and now lists both files.
    expect(screen.getByText("a.ts")).toBeTruthy();
    expect(screen.getByText("b.ts")).toBeTruthy();
  });
});

/**
 * The rendering contract only — which rows the parse produces, and why, is
 * pinned directly in `utils/__tests__/unifiedDiff.test.ts`. What matters here is
 * that a segmented row survives the trip into the DOM intact: the marker outside
 * the spans, the raw patch text reproduced, and the class vocabulary the
 * stylesheet's two tiers key off.
 */
describe("FilesPanel diff rendering", () => {
  async function showDiff(diff: string) {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "a.ts", changeStatus: "modified" }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockResolvedValue({
      path: "a.ts",
      changeStatus: "modified",
      content: "",
      diff,
      isBinary: false,
    });

    await renderPanel(makeWorkItem());
    await act(async () => {
      fireEvent.click(screen.getByText("a.ts"));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Diff" }));
      await Promise.resolve();
    });
    return Array.from(document.querySelectorAll(".wiv2-diff-line"));
  }

  test("renders a paired line as its marker plus strongly shaded word spans", async () => {
    const [hunk, del, add] = await showDiff(
      ["@@ -1 +1 @@", "-const timeout = 30;", "+const timeout = 60;"].join("\n"),
    );

    const strong = (row: Element) =>
      Array.from(row.querySelectorAll(".wiv2-diff-seg-add, .wiv2-diff-seg-del")).map(
        (seg) => seg.textContent,
      );
    expect(strong(del)).toEqual(["30"]);
    expect(strong(add)).toEqual(["60"]);

    // Splitting a line into spans must not change what it reads or copies as:
    // the marker stays outside them and the payload is reproduced in full.
    expect(del.textContent).toBe("-const timeout = 30;");
    expect(add.textContent).toBe("+const timeout = 60;");
    // The marker is a bare leading text node, not part of any segment span, so
    // it can never pick up the strong tier.
    for (const [row, marker] of [
      [del, "-"],
      [add, "+"],
    ] as const) {
      expect(row.firstChild?.nodeType).toBe(Node.TEXT_NODE);
      expect(row.firstChild?.textContent).toBe(marker);
    }

    // The light tier still comes from the row, so both tiers stack.
    expect(del.className).toBe("wiv2-diff-line wiv2-diff-del");
    expect(add.className).toBe("wiv2-diff-line wiv2-diff-add");
    expect(hunk.className).toBe("wiv2-diff-line wiv2-diff-hunk");
  });

  test("renders an unsegmented line as plain text in its shading class", async () => {
    // "hello" and "world" share nothing, so the pair keeps the light tier alone
    // and the row stays a single text node.
    const rows = await showDiff(["@@ -1 +1 @@", "-hello", "+world"].join("\n"));

    expect(document.querySelectorAll(".wiv2-diff-seg-add, .wiv2-diff-seg-del")).toHaveLength(0);
    expect(rows.map((row) => row.className)).toEqual([
      "wiv2-diff-line wiv2-diff-hunk",
      "wiv2-diff-line wiv2-diff-del",
      "wiv2-diff-line wiv2-diff-add",
    ]);
    expect(screen.getByText("-hello")).toBeTruthy();
    expect(screen.getByText("+world")).toBeTruthy();
  });
});
