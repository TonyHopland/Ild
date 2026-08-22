import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act } from "@testing-library/react";
import FilesPanel from "./FilesPanel";
import { WorkItem, WorkItemStatus, WorkItemPriority, WorktreeFileContent } from "../../types";
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
      imageMimeType: null,
      imageBase64: null,
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

  test("draws an inlined image, and keeps the diff view winning over it", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "logo.png", changeStatus: "added" }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockResolvedValue({
      path: "logo.png",
      changeStatus: "added",
      content: null,
      diff: "Binary files differ",
      isBinary: true,
      imageMimeType: "image/png",
      imageBase64: "AAAB",
    });

    await renderPanel(makeWorkItem());
    await act(async () => {
      fireEvent.click(screen.getByText("logo.png"));
      await Promise.resolve();
    });

    const img = screen.getByAltText(/logo\.png/) as HTMLImageElement;
    expect(img.getAttribute("src")).toBe("data:image/png;base64,AAAB");
    expect(screen.queryByText(/Binary file/)).toBeNull();

    // Diff is still the ranking branch — the image doesn't intercept it.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Diff" }));
      await Promise.resolve();
    });
    expect(screen.queryByAltText(/logo\.png/)).toBeNull();
    expect(screen.getByText("Binary files differ")).toBeTruthy();
  });

  test("still shows the fallback message for a binary the viewer can't draw", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "blob.bin", changeStatus: "added" }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockResolvedValue({
      path: "blob.bin",
      changeStatus: "added",
      content: null,
      diff: null,
      isBinary: true,
      imageMimeType: null,
      imageBase64: null,
    });

    await renderPanel(makeWorkItem());
    await act(async () => {
      fireEvent.click(screen.getByText("blob.bin"));
      await Promise.resolve();
    });

    expect(screen.getByText(/Binary file/)).toBeTruthy();
    expect(document.querySelector(".wiv2-file-image")).toBeNull();
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
        imageMimeType: null,
        imageBase64: null,
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
      imageMimeType: null,
      imageBase64: null,
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

describe("FilesPanel markdown preview", () => {
  function mockFiles(paths: string[], content: string) {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: paths.map((path) => ({ path, changeStatus: "modified" as const })),
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      async (_id: string, path: string) => ({
        path,
        changeStatus: "modified" as const,
        content,
        diff: null,
        isBinary: false,
        imageMimeType: null,
        imageBase64: null,
      }),
    );
  }

  async function open(name: string) {
    await act(async () => {
      fireEvent.click(screen.getByText(name));
      await Promise.resolve();
    });
  }

  async function click(name: string) {
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name }));
      await Promise.resolve();
    });
  }

  test("offers Preview for markdown only, and renders the document when chosen", async () => {
    mockFiles(["notes.md", "a.ts"], "# Title\n\nbody text");
    await renderPanel(makeWorkItem());

    await open("notes.md");
    expect(screen.getByRole("button", { name: "Preview" })).toBeTruthy();
    // Code stays the default — a reviewer opening a file is reading changes,
    // not a rendered document.
    expect(document.querySelector(".wiv2-code")).toBeTruthy();
    expect(document.querySelector(".markdown-body")).toBeNull();

    await click("Preview");
    const heading = screen.getByRole("heading", { name: "Title" });
    expect(heading.tagName).toBe("H1");
    expect(document.querySelector(".wiv2-code")).toBeNull();

    // A non-markdown file has no Preview to offer.
    await open("a.ts");
    expect(screen.queryByRole("button", { name: "Preview" })).toBeNull();
  });

  test("falls back to Code when a preview-mode selection lands on a non-markdown file", async () => {
    mockFiles(["notes.md", "a.ts"], "# Title");
    await renderPanel(makeWorkItem());

    await open("notes.md");
    await click("Preview");
    expect(document.querySelector(".markdown-body")).toBeTruthy();

    // Selecting a file with no preview must not strand the viewer: it shows the
    // code and marks Code as the active toggle.
    await open("a.ts");
    expect(document.querySelector(".markdown-body")).toBeNull();
    expect(document.querySelector(".wiv2-code")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Code" }).getAttribute("aria-pressed")).toBe("true");

    // Going back to markdown restores the preview the user asked for.
    await open("notes.md");
    expect(document.querySelector(".markdown-body")).toBeTruthy();
  });

  test("previews an empty markdown file as an empty document, not as code", async () => {
    mockFiles(["empty.md"], "");
    await renderPanel(makeWorkItem());

    await open("empty.md");
    await click("Preview");

    // The toolbar says Preview, so the pane must not quietly show the code view.
    expect(screen.getByRole("button", { name: "Preview" }).getAttribute("aria-pressed")).toBe(
      "true",
    );
    expect(document.querySelector(".wiv2-file-markdown")).toBeTruthy();
    expect(document.querySelector(".wiv2-code")).toBeNull();
  });

  test("shows the binary notice for a markdown-suffixed binary rather than previewing it", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "odd.md", changeStatus: "added" }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockResolvedValue({
      path: "odd.md",
      changeStatus: "added",
      // A markdown suffix over bytes the server could not hand back as text.
      content: "\u0000# not really markdown",
      diff: null,
      isBinary: true,
      imageMimeType: null,
      imageBase64: null,
    });

    await renderPanel(makeWorkItem());
    await open("odd.md");
    await click("Preview");

    expect(screen.getByText(/Binary file/)).toBeTruthy();
    expect(document.querySelector(".wiv2-file-markdown")).toBeNull();
  });

  test("keeps the Diff mode as the user clicks through files", async () => {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [
        { path: "a.ts", changeStatus: "modified" },
        { path: "b.ts", changeStatus: "modified" },
      ],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      async (_id: string, path: string) => ({
        path,
        changeStatus: "modified" as const,
        content: "unused",
        diff: `@@ -1 +1 @@\n+from ${path}`,
        isBinary: false,
        imageMimeType: null,
        imageBase64: null,
      }),
    );

    await renderPanel(makeWorkItem());
    await open("a.ts");
    await click("Diff");
    expect(screen.getByText("+from a.ts")).toBeTruthy();

    await open("b.ts");
    expect(screen.getByText("+from b.ts")).toBeTruthy();
  });
});

describe("FilesPanel syntax highlighting", () => {
  async function showCode(path: string, content: string) {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path, changeStatus: "modified" }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockResolvedValue({
      path,
      changeStatus: "modified",
      content,
      diff: null,
      isBinary: false,
      imageMimeType: null,
      imageBase64: null,
    });

    await renderPanel(makeWorkItem());
    // Folders start collapsed, so walk down to the file one segment at a time.
    for (const segment of path.split("/")) {
      await act(async () => {
        fireEvent.click(screen.getByText(segment));
        await Promise.resolve();
      });
    }
    return Array.from(document.querySelectorAll(".wiv2-code-line"));
  }

  test("colours a file by the language its path implies", async () => {
    const [line] = await showCode("src/a.ts", "const answer = 42;");

    const classes = Array.from(line.querySelectorAll("span[class^='hljs']")).map((span) => [
      span.className,
      span.textContent,
    ]);
    expect(classes).toContainEqual(["hljs-keyword", "const"]);
    expect(classes).toContainEqual(["hljs-number", "42"]);
    // Splitting a line into spans must not change what it reads or copies as.
    expect(line.textContent).toBe("1const answer = 42;");
  });

  test("keeps a multi-line token coloured on every line it spans", async () => {
    // The whole file is highlighted once and the tokens are then cut at
    // newlines; tokenizing line by line would end the comment at the first
    // newline and leave the rest of it plain.
    const lines = await showCode("src/a.ts", "/* one\n   two */\nlet x;");

    const comment = (row: Element) =>
      Array.from(row.querySelectorAll(".hljs-comment")).map((span) => span.textContent);
    expect(comment(lines[0])).toEqual(["/* one"]);
    expect(comment(lines[1])).toEqual(["   two */"]);
    expect(comment(lines[2])).toEqual([]);
  });

  test("renders an unmapped extension as plain numbered lines", async () => {
    const lines = await showCode("notes.wat", "some prose\nmore prose");

    expect(document.querySelectorAll("[class^='hljs']")).toHaveLength(0);
    expect(lines.map((line) => line.querySelector(".wiv2-code-text")?.textContent)).toEqual([
      "some prose",
      "more prose",
    ]);
    expect(lines.map((line) => line.querySelector(".wiv2-code-gutter")?.textContent)).toEqual([
      "1",
      "2",
    ]);
  });

  test("leaves a file too large to highlight as plain numbered lines", async () => {
    // Highlighting is one synchronous pass over the whole file, so past the size
    // cap it is skipped rather than stalling the dialog on a generated blob.
    const code = `const a = ${"1".repeat(300_001)};`;
    const [line] = await showCode("big.ts", code);

    expect(line.querySelectorAll("[class^='hljs']")).toHaveLength(0);
    // The file is still shown in full, just uncoloured.
    expect(line.querySelector(".wiv2-code-text")?.textContent).toHaveLength(code.length);
  });

  test("numbers every line of a file that ends in a newline without adding one", async () => {
    const lines = await showCode("src/a.ts", "let a;\nlet b;\n");
    expect(lines).toHaveLength(2);
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
      imageMimeType: null,
      imageBase64: null,
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

describe("FilesPanel SVG preview", () => {
  function mockFiles(paths: string[], content: string) {
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: paths.map((path) => ({ path, changeStatus: "modified" as const })),
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      async (_id: string, path: string) => ({
        path,
        changeStatus: "modified" as const,
        content,
        diff: null,
        // The server serves SVG as text, deliberately — never as an inline
        // image — so these stay null, as they do for any other text file.
        isBinary: false,
        imageMimeType: null,
        imageBase64: null,
      }),
    );
  }

  async function open(name: string) {
    await act(async () => {
      fireEvent.click(screen.getByText(name));
      await Promise.resolve();
    });
  }

  async function click(name: string) {
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name }));
      await Promise.resolve();
    });
  }

  const CIRCLE = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 8 8"><circle r="4"/></svg>';

  test("offers Preview for an SVG and draws it as an image, leaving Code intact", async () => {
    mockFiles(["logo.svg", "a.ts"], CIRCLE);
    await renderPanel(makeWorkItem());

    await open("logo.svg");
    expect(screen.getByRole("button", { name: "Preview" })).toBeTruthy();
    // Code stays the default, syntax-coloured as any other markup.
    expect(document.querySelector(".wiv2-code")).toBeTruthy();

    await click("Preview");
    const img = screen.getByRole("img") as HTMLImageElement;
    expect(img.getAttribute("alt")).toBe("Rendered contents of SVG file logo.svg");
    expect(img.getAttribute("src")).toBe(
      `data:image/svg+xml;charset=utf-8,${encodeURIComponent(CIRCLE)}`,
    );
    // Drawn as a picture, not spliced into the document as live markup.
    expect(document.querySelector(".wiv2-files-content svg")).toBeNull();
    expect(document.querySelector(".wiv2-code")).toBeNull();

    // Back to Code, and the file reads as its own source again.
    await click("Code");
    expect(document.querySelector(".wiv2-code")).toBeTruthy();
    expect(screen.queryByRole("img")).toBeNull();
    expect(screen.getByText("circle")).toBeTruthy();

    // A file with no preview still has none to offer.
    await open("a.ts");
    expect(screen.queryByRole("button", { name: "Preview" })).toBeNull();
  });

  test("keeps a script-bearing SVG inert: no element, no inline markup, no execution", async () => {
    const hostile =
      '<svg xmlns="http://www.w3.org/2000/svg"><script>window.__pwned = true;</script></svg>';
    mockFiles(["hostile.svg"], hostile);
    await renderPanel(makeWorkItem());

    await open("hostile.svg");
    await click("Preview");

    // The markup only ever reaches the DOM percent-encoded inside an <img>
    // src, which no browser executes; nothing is parsed into the document.
    const img = screen.getByRole("img") as HTMLImageElement;
    expect(img.getAttribute("src")).toBe(
      `data:image/svg+xml;charset=utf-8,${encodeURIComponent(hostile)}`,
    );
    expect(document.querySelector(".wiv2-files-content svg")).toBeNull();
    expect(document.querySelector(".wiv2-files-content script")).toBeNull();
    expect((window as unknown as { __pwned?: boolean }).__pwned).toBeUndefined();
  });

  test("says an empty SVG is empty rather than drawing a broken image", async () => {
    mockFiles(["empty.svg"], "");
    await renderPanel(makeWorkItem());

    await open("empty.svg");
    await click("Preview");

    // The toolbar says Preview, so the pane must not quietly show the code view.
    expect(screen.getByRole("button", { name: "Preview" }).getAttribute("aria-pressed")).toBe(
      "true",
    );
    expect(screen.getByText(/This file is empty/)).toBeTruthy();
    expect(screen.queryByRole("img")).toBeNull();
    expect(document.querySelector(".wiv2-code")).toBeNull();
  });

  test("says so when the markup will not draw, and recovers on the next file", async () => {
    mockFiles(["broken.svg", "logo.svg"], "");
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      async (_id: string, path: string) => ({
        path,
        changeStatus: "modified" as const,
        content: path === "broken.svg" ? "<svg><not closed" : CIRCLE,
        diff: null,
        isBinary: false,
        imageMimeType: null,
        imageBase64: null,
      }),
    );
    await renderPanel(makeWorkItem());

    await open("broken.svg");
    await click("Preview");
    await act(async () => {
      fireEvent.error(screen.getByRole("img"));
      await Promise.resolve();
    });
    expect(screen.getByText(/could not be drawn/)).toBeTruthy();

    // The failure belongs to that file, not to the viewer: the next SVG draws.
    await open("logo.svg");
    expect(screen.queryByText(/could not be drawn/)).toBeNull();
    expect(screen.getByRole("img")).toBeTruthy();
  });

  test("redraws in place when a refresh corrects markup the browser refused", async () => {
    let markup = "<svg><not closed";
    vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "fixing.svg", changeStatus: "modified" as const }],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      async (_id: string, path: string) => ({
        path,
        changeStatus: "modified" as const,
        content: markup,
        diff: null,
        isBinary: false,
        imageMimeType: null,
        imageBase64: null,
      }),
    );

    const workItem = makeWorkItem();
    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={workItem} />);
      await Promise.resolve();
    });
    await open("fixing.svg");
    await click("Preview");
    await act(async () => {
      fireEvent.error(screen.getByRole("img"));
      await Promise.resolve();
    });
    expect(screen.getByText(/could not be drawn/)).toBeTruthy();

    // The run fixes the file. The background refresh is silent — it brings the
    // new bytes in without unmounting the viewer — so recovering here is the
    // component's own doing, where the case above recovers by being remounted.
    markup = CIRCLE;
    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem }} />);
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(screen.queryByText(/could not be drawn/)).toBeNull();
    expect(screen.getByRole("img")).toBeTruthy();
  });

  test("falls back to Code when a preview-mode selection lands on a plain file", async () => {
    mockFiles(["logo.svg", "a.ts"], CIRCLE);
    await renderPanel(makeWorkItem());

    await open("logo.svg");
    await click("Preview");
    expect(screen.getByRole("img")).toBeTruthy();

    await open("a.ts");
    expect(screen.queryByRole("img")).toBeNull();
    expect(screen.getByRole("button", { name: "Code" }).getAttribute("aria-pressed")).toBe("true");

    // Going back restores the preview the user asked for.
    await open("logo.svg");
    expect(screen.getByRole("img")).toBeTruthy();
  });
});

describe("FilesPanel editing", () => {
  /** A work item parked on a human — the only state a file is editable in. */
  function parked(overrides: Partial<WorkItem> = {}): WorkItem {
    return makeWorkItem({ status: WorkItemStatus.HumanFeedback, ...overrides });
  }

  const TEXT_FILE: WorktreeFileContent = {
    path: "a.ts",
    changeStatus: "none",
    content: "before",
    diff: null,
    isBinary: false,
    imageMimeType: null,
    imageBase64: null,
  };

  function mockOneFile(content: Partial<WorktreeFileContent> = {}) {
    const getFiles = vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [{ path: "a.ts", changeStatus: "none" }],
    });
    const getFileContent = vi
      .spyOn(authServices.workItemService, "getFileContent")
      .mockResolvedValue({ ...TEXT_FILE, ...content });
    return { getFiles, getFileContent };
  }

  async function open(name: string) {
    await act(async () => {
      fireEvent.click(screen.getByText(name));
      await Promise.resolve();
    });
  }

  async function click(name: string | RegExp) {
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name }));
      await Promise.resolve();
    });
  }

  async function type(text: string, path = "a.ts") {
    await act(async () => {
      fireEvent.change(screen.getByLabelText(`Contents of ${path}`), { target: { value: text } });
      await Promise.resolve();
    });
  }

  /** Two files and a save that only resolves when the test says so. */
  function mockTwoFilesWithHeldSave() {
    const getFiles = vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
      worktreePath: "/tmp/wt",
      files: [
        { path: "a.ts", changeStatus: "none" },
        { path: "b.ts", changeStatus: "none" },
      ],
    });
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      async (_id: string, path: string) => ({
        ...TEXT_FILE,
        path,
        content: path === "a.ts" ? "before" : "the other file",
      }),
    );
    let finishSave!: (saved: WorktreeFileContent) => void;
    vi.spyOn(authServices.workItemService, "saveFileContent").mockReturnValue(
      new Promise<WorktreeFileContent>((resolve) => {
        finishSave = resolve;
      }),
    );
    return { getFiles, finishSave: (saved: WorktreeFileContent) => finishSave(saved) };
  }

  test("writes the edited file back and redraws from what was saved", async () => {
    const { getFiles } = mockOneFile();
    const save = vi.spyOn(authServices.workItemService, "saveFileContent").mockResolvedValue({
      ...TEXT_FILE,
      changeStatus: "modified",
      content: "after",
      diff: "@@ -1 +1 @@\n-before\n+after",
    });

    await renderPanel(parked());
    await open("a.ts");
    await click("Edit");
    await type("after");
    await click("Save");

    expect(save).toHaveBeenCalledWith("wi-1", "a.ts", "after");
    // The editor closes onto the saved file, and the answer the save gave —
    // not the content the editor started from — is what the viewer now shows.
    expect(screen.queryByLabelText("Contents of a.ts")).toBeNull();
    expect(screen.getByText("after")).toBeTruthy();
    await click("Diff");
    expect(screen.getByText("+after")).toBeTruthy();
    // The tree is re-pulled too: this file's badge just went none → M.
    expect(getFiles).toHaveBeenCalledTimes(2);
  });

  test("cancel discards the edit and restores the loaded file", async () => {
    mockOneFile();
    const save = vi.spyOn(authServices.workItemService, "saveFileContent");

    await renderPanel(parked());
    await open("a.ts");
    await click("Edit");
    await type("scribbled over");
    await click("Cancel");

    expect(save).not.toHaveBeenCalled();
    expect(screen.queryByText("scribbled over")).toBeNull();
    expect(screen.getByText("before")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
  });

  test("a refused save keeps the draft rather than losing the user's text", async () => {
    mockOneFile();
    // The file was deleted, made binary, or the worktree went away under the
    // editor — every one of these comes back as a refusal, and the text the
    // user typed exists nowhere but this textarea.
    vi.spyOn(authServices.workItemService, "saveFileContent").mockRejectedValue({
      message: "File is not an editable text file in this worktree.",
    });

    await renderPanel(parked());
    await open("a.ts");
    await click("Edit");
    await type("still mine");
    await click("Save");

    expect(screen.getByText(/not an editable text file/)).toBeTruthy();
    expect((screen.getByLabelText("Contents of a.ts") as HTMLTextAreaElement).value).toBe(
      "still mine",
    );
    // The editor stays open on a write that never happened, so Save can be
    // tried again — it does not close as though it had gone through.
    expect(screen.getByRole("button", { name: "Save" })).toBeTruthy();
  });

  test("a save that lands after the user moved on leaves the new file alone", async () => {
    const { finishSave } = mockTwoFilesWithHeldSave();

    await renderPanel(parked());
    await open("a.ts");
    await click("Edit");
    await type("saved text");
    await click("Save");

    // The tree is not disabled while the save is in flight, and the file the
    // user clicks loads its own content straight away.
    await open("b.ts");
    expect(screen.getByText("the other file")).toBeTruthy();

    await act(async () => {
      finishSave({ ...TEXT_FILE, path: "a.ts", changeStatus: "modified", content: "saved text" });
      await Promise.resolve();
      await Promise.resolve();
    });

    // The answer describes a.ts, so it must not land on b.ts — drawn there it
    // would show one file's text under another's path, and the next save would
    // write it into that file.
    expect(screen.getByText("the other file")).toBeTruthy();
    expect(screen.queryByText("saved text")).toBeNull();
  });

  test("a save in flight on one file leaves the next file's editor writable", async () => {
    mockTwoFilesWithHeldSave();

    await renderPanel(parked());
    await open("a.ts");
    await click("Edit");
    await type("saved text");
    await click("Save");

    // The save for a.ts is still out. Editing b.ts is a different file's edit,
    // so it must not be held read-only waiting on it.
    await open("b.ts");
    await click("Edit");
    const box = screen.getByLabelText("Contents of b.ts") as HTMLTextAreaElement;
    expect(box.readOnly).toBe(false);

    await type("typed while a.ts saves", "b.ts");
    expect((screen.getByLabelText("Contents of b.ts") as HTMLTextAreaElement).value).toBe(
      "typed while a.ts saves",
    );
    expect(screen.getByRole("button", { name: "Save" }).hasAttribute("disabled")).toBe(false);
  });

  test("a save that lands after the work item changed reaches none of the new one", async () => {
    const { getFiles, finishSave } = mockTwoFilesWithHeldSave();

    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={parked()} />);
      await Promise.resolve();
    });
    await open("a.ts");
    await click("Edit");
    await type("saved text");
    await click("Save");

    // The dialog is handed a different work item — a different worktree, a
    // different set of files — while the save is still out.
    await act(async () => {
      view.rerender(<FilesPanel workItem={parked({ id: "wi-2", worktreePath: "/tmp/wt-2" })} />);
      await Promise.resolve();
      await Promise.resolve();
    });
    // Nothing of the previous item is left on screen to be written back.
    expect(screen.getByText(/Select a file to view its contents/)).toBeTruthy();

    const refreshesBefore = getFiles.mock.calls.length;
    await act(async () => {
      finishSave({ ...TEXT_FILE, path: "a.ts", changeStatus: "modified", content: "saved text" });
      await Promise.resolve();
      await Promise.resolve();
    });

    // The answer describes a file in the worktree the panel has left. Drawing
    // it here would put one item's file under another item's tree, and the next
    // save would write it into that item.
    expect(screen.queryByText("saved text")).toBeNull();
    expect(screen.getByText(/Select a file to view its contents/)).toBeTruthy();
    // Its list refresh is the previous item's too, and would have replaced this
    // item's tree with that one's files.
    expect(getFiles.mock.calls.length).toBe(refreshesBefore);
  });

  test("moving to another work item empties the viewer rather than keeping its file", async () => {
    mockOneFile();

    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={parked()} />);
      await Promise.resolve();
    });
    await open("a.ts");
    expect(screen.getByText("before")).toBeTruthy();

    await act(async () => {
      view.rerender(<FilesPanel workItem={parked({ id: "wi-2", worktreePath: "/tmp/wt-2" })} />);
      await Promise.resolve();
      await Promise.resolve();
    });

    // The path belonged to the item before it, and may not exist in this one.
    expect(screen.queryByText("before")).toBeNull();
    expect(screen.getByText("No file selected")).toBeTruthy();
  });

  test("offers no edit while the run still owns the worktree", async () => {
    mockOneFile();

    // Running, not parked: the agent is the one writing in there.
    await renderPanel(makeWorkItem({ status: WorkItemStatus.Running }));
    await open("a.ts");

    expect(screen.getByText("before")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
  });

  test("a run that starts again mid-edit withdraws Save but keeps the text", async () => {
    mockOneFile();

    const workItem = parked();
    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={workItem} />);
      await Promise.resolve();
    });
    await open("a.ts");
    await click("Edit");
    await type("half-finished");

    // The item leaves human feedback while the editor is open.
    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem, status: WorkItemStatus.Running }} />);
      await Promise.resolve();
      await Promise.resolve();
    });

    // The draft is the only copy of this text, so it is not thrown away — but
    // it can no longer be written into a worktree the agent has taken back.
    expect((screen.getByLabelText("Contents of a.ts") as HTMLTextAreaElement).value).toBe(
      "half-finished",
    );
    expect(screen.getByText(/can no longer be saved/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save" }).hasAttribute("disabled")).toBe(true);
    // Cancel still works, or the editor would be a trap.
    expect(screen.getByRole("button", { name: "Cancel" }).hasAttribute("disabled")).toBe(false);

    await click("Cancel");
    expect(screen.queryByLabelText("Contents of a.ts")).toBeNull();
    expect(screen.getByText("before")).toBeTruthy();
  });

  test("a background refresh leaves an open editor's unsaved text alone", async () => {
    const { getFileContent } = mockOneFile();

    const workItem = parked();
    let view!: ReturnType<typeof render>;
    await act(async () => {
      view = render(<FilesPanel workItem={workItem} />);
      await Promise.resolve();
    });
    await open("a.ts");
    await click("Edit");
    await type("unsaved work");

    // The run advances and the parent hands down a fresh work item while the
    // editor is open. The silent re-pull that keeps the viewer current would
    // take the draft with it, so it must not run for the file being edited.
    getFileContent.mockResolvedValue({ ...TEXT_FILE, content: "refreshed by the run" });
    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem }} />);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect((screen.getByLabelText("Contents of a.ts") as HTMLTextAreaElement).value).toBe(
      "unsaved work",
    );
    expect(getFileContent).toHaveBeenCalledTimes(1);

    // Once the edit is out of the way the viewer catches up again.
    await click("Cancel");
    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem }} />);
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(screen.getByText("refreshed by the run")).toBeTruthy();
  });

  test("offers no edit for a binary, an image or a file that is not on disk", async () => {
    const cases: {
      label: string;
      content: Partial<WorktreeFileContent>;
      shown: () => unknown;
    }[] = [
      {
        label: "binary",
        content: { content: null, isBinary: true },
        shown: () => screen.getByText(/Binary file/),
      },
      {
        label: "image",
        content: {
          content: null,
          isBinary: true,
          imageMimeType: "image/png",
          imageBase64: "iVBORw0KGgo=",
        },
        shown: () => screen.getByRole("img"),
      },
      {
        label: "deleted",
        content: { content: null, changeStatus: "deleted" },
        shown: () => screen.getByText(/no content to display/),
      },
    ];

    for (const { label, content, shown } of cases) {
      mockOneFile(content);
      await renderPanel(parked());
      await open("a.ts");
      // The file did arrive — it is drawn as the viewer's non-text case, which
      // is what has no Edit to offer.
      expect(shown(), label).toBeTruthy();
      expect(screen.queryByRole("button", { name: "Edit" }), label).toBeNull();
      cleanup();
      vi.restoreAllMocks();
    }
  });

  test("offers no edit outside the code view", async () => {
    mockOneFile({ changeStatus: "modified", diff: "@@ -1 +1 @@\n-was\n+before" });

    await renderPanel(parked());
    await open("a.ts");
    expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();

    await click("Diff");
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
  });
});
