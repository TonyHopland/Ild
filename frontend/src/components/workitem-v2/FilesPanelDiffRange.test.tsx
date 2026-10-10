import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act } from "@testing-library/react";
import FilesPanel from "./FilesPanel";
import {
  WorkItem,
  WorkItemPriority,
  WorkItemStatus,
  WorktreeCommits,
  WorktreeDiffRange,
  WorktreeFileContent,
} from "../../types";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const BASE = "ba5e".repeat(10);
const C1 = "c1".repeat(20);
const C2 = "c2".repeat(20);
const C3 = "c3".repeat(20);
const COMMITS: WorktreeCommits = {
  baseSha: BASE,
  commits: [
    { sha: C3, parentSha: C2, subject: "Third change" },
    { sha: C2, parentSha: C1, subject: "Second change" },
    { sha: C1, parentSha: BASE, subject: "First change" },
  ],
};
/** The same branch after its newest commit was rewritten away. */
const WITHOUT_THIRD: WorktreeCommits = { baseSha: BASE, commits: COMMITS.commits.slice(1) };

function makeWorkItem(): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.HumanFeedback,
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
    worktreePath: "/tmp/wt",
    branchName: "ild/wi-1",
  };
}

/** a.ts as read under `range`: its content names the range, so a stale read is visible. */
function fileUnder(range?: WorktreeDiffRange): WorktreeFileContent {
  return {
    path: "a.ts",
    changeStatus: "modified",
    content: range ? `read under ${range.from}..${range.to ?? ""}` : "read under all",
    diff: null,
    isBinary: false,
    imageMimeType: null,
    imageBase64: null,
  };
}

async function settle() {
  for (let i = 0; i < 4; i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

async function mount() {
  const workItem = makeWorkItem();
  let view!: ReturnType<typeof render>;
  await act(async () => {
    view = render(<FilesPanel workItem={workItem} />);
  });
  await settle();
  return async () => {
    await act(async () => {
      view.rerender(<FilesPanel workItem={{ ...workItem }} />);
    });
    await settle();
  };
}

async function click(element: HTMLElement) {
  await act(async () => {
    fireEvent.click(element);
  });
  await settle();
}

const commitBox = (subject: RegExp) =>
  screen.getByRole("checkbox", { name: subject }) as HTMLInputElement;

/** Third change alone, with a.ts open in the editor. */
async function editUnderThird() {
  await click(commitBox(/Third change/));
  await click(screen.getByText("a.ts"));
  await click(screen.getByRole("button", { name: "Edit" }));
}

function mockFiles() {
  vi.spyOn(authServices.workItemService, "getFiles").mockResolvedValue({
    worktreePath: "/tmp/wt",
    files: [{ path: "a.ts", changeStatus: "modified" }],
  });
}

describe("FilesPanel diff range moved by the commit list", () => {
  test("a draft keeps its file while the range moves, and Cancel reads it again under the new one", async () => {
    let commits = COMMITS;
    vi.spyOn(authServices.workItemService, "getFileCommits").mockImplementation(
      async () => commits,
    );
    mockFiles();
    const getFileContent = vi
      .spyOn(authServices.workItemService, "getFileContent")
      .mockImplementation(async (_id: string, _path: string, range?: WorktreeDiffRange) =>
        fileUnder(range),
      );
    const refresh = await mount();
    await editUnderThird();
    const readsBefore = getFileContent.mock.calls.length;

    commits = WITHOUT_THIRD;
    await refresh();
    expect(getFileContent.mock.calls.length).toBe(readsBefore);
    expect(screen.getByLabelText("Contents of a.ts")).toBeTruthy();

    await click(screen.getByRole("button", { name: "Cancel" }));
    expect(getFileContent.mock.lastCall).toEqual(["wi-1", "a.ts"]);
    expect(screen.getByText("read under all")).toBeTruthy();
  });

  test("a save answered after the range moved shows the file read under the new range", async () => {
    let commits = COMMITS;
    vi.spyOn(authServices.workItemService, "getFileCommits").mockImplementation(
      async () => commits,
    );
    mockFiles();
    const getFileContent = vi
      .spyOn(authServices.workItemService, "getFileContent")
      .mockImplementation(async (_id: string, _path: string, range?: WorktreeDiffRange) =>
        fileUnder(range),
      );
    let answerSave!: (file: WorktreeFileContent) => void;
    vi.spyOn(authServices.workItemService, "saveFileContent").mockImplementation(
      () =>
        new Promise((resolve) => {
          answerSave = resolve;
        }),
    );
    const refresh = await mount();
    await editUnderThird();
    await click(screen.getByRole("button", { name: "Save" }));

    commits = WITHOUT_THIRD;
    await refresh();
    await act(async () => {
      answerSave({ ...fileUnder({ from: C2, to: C3 }), content: "saved under the old range" });
    });
    await settle();

    expect(screen.queryByText("saved under the old range")).toBeNull();
    expect(getFileContent.mock.lastCall).toEqual(["wi-1", "a.ts"]);
    expect(screen.getByText("read under all")).toBeTruthy();
  });

  test("an older commit list answering last does not undo a newer one", async () => {
    const answers: ((list: WorktreeCommits) => void)[] = [];
    vi.spyOn(authServices.workItemService, "getFileCommits").mockImplementation(
      () =>
        new Promise((resolve) => {
          answers.push(resolve);
        }),
    );
    mockFiles();
    const refresh = await mount();
    await act(async () => answers[0](COMMITS));
    await settle();
    await click(commitBox(/Third change/));

    await refresh();
    await refresh();
    await act(async () => answers[2](COMMITS));
    await settle();
    await act(async () => answers[1](WITHOUT_THIRD));
    await settle();

    expect(commitBox(/Third change/).checked).toBe(true);
  });
});

describe("FilesPanel reads of the open file", () => {
  test("an older read of the open file answering last does not replace a newer one", async () => {
    vi.spyOn(authServices.workItemService, "getFileCommits").mockResolvedValue(COMMITS);
    mockFiles();
    const answers: ((file: WorktreeFileContent) => void)[] = [];
    vi.spyOn(authServices.workItemService, "getFileContent").mockImplementation(
      () =>
        new Promise((resolve) => {
          answers.push(resolve);
        }),
    );
    const refresh = await mount();

    await click(screen.getByText("a.ts"));
    // The run advances while the file is still loading: the same file is read again.
    await refresh();
    expect(answers).toHaveLength(2);

    await act(async () => answers[1]({ ...fileUnder(), content: "the newer read" }));
    await settle();
    await act(async () => answers[0]({ ...fileUnder(), content: "the older read" }));
    await settle();

    expect(screen.queryByText("the older read")).toBeNull();
    expect(screen.getByText("the newer read")).toBeTruthy();
  });

  test("a read started before a save answers after it and does not undo the save", async () => {
    vi.spyOn(authServices.workItemService, "getFileCommits").mockResolvedValue(COMMITS);
    mockFiles();
    const answers: ((file: WorktreeFileContent) => void)[] = [];
    const getFileContent = vi
      .spyOn(authServices.workItemService, "getFileContent")
      .mockResolvedValue({ ...fileUnder(), content: "before" });
    vi.spyOn(authServices.workItemService, "saveFileContent").mockResolvedValue({
      ...fileUnder(),
      content: "saved",
    });
    const refresh = await mount();
    await click(screen.getByText("a.ts"));

    // A background read is out when the user starts editing and saves.
    getFileContent.mockImplementation(
      () =>
        new Promise((resolve) => {
          answers.push(resolve);
        }),
    );
    await refresh();
    await click(screen.getByRole("button", { name: "Edit" }));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Contents of a.ts"), { target: { value: "saved" } });
    });
    await click(screen.getByRole("button", { name: "Save" }));
    expect(screen.getByText("saved")).toBeTruthy();

    await act(async () => answers[0]({ ...fileUnder(), content: "read before the save" }));
    await settle();

    expect(screen.queryByText("read before the save")).toBeNull();
    expect(screen.getByText("saved")).toBeTruthy();
  });
});
