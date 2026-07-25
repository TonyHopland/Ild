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
 * The diff viewer shades a changed line lightly and the words that actually
 * differ from its counterpart strongly, the same two-tier treatment the Loop
 * Editor's save-time review gives its own diff. What follows asserts the pairing
 * that decides which words those are — including the cases where there is no
 * counterpart, or none worth segmenting, and the line keeps the light tier alone.
 */
describe("FilesPanel diff view", () => {
  /** Every rendered diff row: its shading class, its full text, its strong runs. */
  function rows() {
    return Array.from(document.querySelectorAll(".wiv2-diff-line")).map((row) => ({
      className: row.className,
      text: row.textContent ?? "",
      changed: Array.from(row.querySelectorAll(".wiv2-diff-seg-add, .wiv2-diff-seg-del")).map(
        (seg) => seg.textContent ?? "",
      ),
    }));
  }

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
    return rows();
  }

  test("emphasises the words that differ between a paired del/add line", async () => {
    const diff = ["@@ -1 +1 @@", "-const timeout = 30;", "+const timeout = 60;"].join("\n");

    const [hunk, del, add] = await showDiff(diff);

    expect(del.changed).toEqual(["30"]);
    expect(add.changed).toEqual(["60"]);
    // Splitting the line into spans must not disturb what it reads as: the
    // marker stays put and the payload is reproduced in full.
    expect(del.text).toBe("-const timeout = 30;");
    expect(add.text).toBe("+const timeout = 60;");
    expect(del.className).toContain("wiv2-diff-del");
    expect(add.className).toContain("wiv2-diff-add");
    expect(hunk.className).toContain("wiv2-diff-hunk");
  });

  test("leaves an unpaired insertion or deletion to the whole-line tier", async () => {
    // Neither hunk has a counterpart to compare against, and borrowing one from
    // the context around it would emphasise words nobody edited.
    const diff = [
      "@@ -1,2 +1,1 @@",
      " keep this",
      "-dropped entirely",
      "@@ -10,1 +10,2 @@",
      " keep this",
      "+added entirely",
    ].join("\n");

    const rendered = await showDiff(diff);

    expect(rendered.every((row) => row.changed.length === 0)).toBeTruthy();
    expect(screen.getByText("-dropped entirely")).toBeTruthy();
    expect(screen.getByText("+added entirely")).toBeTruthy();
  });

  test("falls back to the whole line when the pair is not worth segmenting", async () => {
    // Two lines sharing nothing: computeWordDiff declines them, which is a
    // normal result and not an error — the line still shades, just in one tier.
    const diff = ["@@ -1 +1 @@", "-hello", "+world"].join("\n");

    const [, del, add] = await showDiff(diff);

    expect(del.changed).toEqual([]);
    expect(add.changed).toEqual([]);
    // No spans at all, so the raw line is still one text node.
    expect(screen.getByText("-hello")).toBeTruthy();
    expect(screen.getByText("+world")).toBeTruthy();
  });

  test("pairs index-wise and stops at the shorter run", async () => {
    const diff = [
      "@@ -1,3 +1,1 @@",
      "-step one alpha",
      "-step two beta",
      "-step three gamma",
      "+step one omega",
    ].join("\n");

    const [, first, second, third, add] = await showDiff(diff);

    expect(first.changed).toEqual(["alpha"]);
    expect(add.changed).toEqual(["omega"]);
    // Removed lines past the end of the added run have no counterpart.
    expect(second.changed).toEqual([]);
    expect(third.changed).toEqual([]);
  });

  test("pairs the same way when the added run is the longer one", async () => {
    const diff = [
      "@@ -1,1 +1,3 @@",
      "-step one alpha",
      "+step one omega",
      "+step two beta",
      "+step three gamma",
    ].join("\n");

    const [, del, first, second, third] = await showDiff(diff);

    expect(del.changed).toEqual(["alpha"]);
    expect(first.changed).toEqual(["omega"]);
    expect(second.changed).toEqual([]);
    expect(third.changed).toEqual([]);
  });

  test("segments each del/add run of a multi-hunk patch independently", async () => {
    const diff = [
      "@@ -1,2 +1,2 @@",
      "-const timeout = 30;",
      "-const retries = 1;",
      "+const timeout = 60;",
      "+const retries = 5;",
      "@@ -20,1 +20,1 @@",
      " untouched",
      "-const backoff = 100;",
      "+const backoff = 250;",
    ].join("\n");

    const rendered = await showDiff(diff);

    expect(rendered.filter((row) => row.changed.length > 0).map((row) => row.changed)).toEqual([
      ["30"],
      ["1"],
      ["60"],
      ["5"],
      ["100"],
      ["250"],
    ]);
  });

  test("does not pair the patch preamble's --- / +++ headers", async () => {
    // They are prefixed like a del/add pair, so the pairing has to key off the
    // hunk header rather than the prefix — diffing "a/a.ts" against "b/a.ts"
    // would emphasise the two letters that are supposed to differ.
    const diff = [
      "diff --git a/a.ts b/a.ts",
      "index 1111111..2222222 100644",
      "--- a/a.ts",
      "+++ b/a.ts",
      "@@ -1 +1 @@",
      "-const timeout = 30;",
      "+const timeout = 60;",
    ].join("\n");

    const rendered = await showDiff(diff);
    const [, , minus, plus, , del, add] = rendered;

    expect(minus.text).toBe("--- a/a.ts");
    expect(plus.text).toBe("+++ b/a.ts");
    expect(minus.changed).toEqual([]);
    expect(plus.changed).toEqual([]);
    // Header shading is longstanding behaviour, asserted so a change to it is a
    // deliberate one rather than a side effect.
    expect(minus.className).toContain("wiv2-diff-del");
    expect(plus.className).toContain("wiv2-diff-add");
    // The content pair below them still segments.
    expect(del.changed).toEqual(["30"]);
    expect(add.changed).toEqual(["60"]);
  });

  test("segments across a no-newline-at-end-of-file marker", async () => {
    // git puts the marker between the removed and added sides of the last line,
    // which is exactly the pair worth segmenting — it annotates the line before
    // it rather than ending the run.
    const diff = [
      "@@ -1 +1 @@",
      "-const timeout = 30;",
      "\\ No newline at end of file",
      "+const timeout = 60;",
      "\\ No newline at end of file",
    ].join("\n");

    const [, del, marker, add] = await showDiff(diff);

    expect(del.changed).toEqual(["30"]);
    expect(add.changed).toEqual(["60"]);
    expect(marker.text).toBe("\\ No newline at end of file");
    expect(marker.className).toContain("wiv2-diff-ctx");
  });

  test("treats a deleted line that itself starts with -- as content", async () => {
    // "-- note" arrives with its marker as "--- note", indistinguishable from a
    // file header by prefix alone — inside a hunk it is an ordinary removal.
    const diff = ["@@ -1 +1 @@", "--- keep the first note", "+-- keep the second note"].join("\n");

    const [, del, add] = await showDiff(diff);

    expect(del.changed).toEqual(["first"]);
    expect(add.changed).toEqual(["second"]);
    expect(del.text).toBe("--- keep the first note");
    expect(add.text).toBe("+-- keep the second note");
  });
});
