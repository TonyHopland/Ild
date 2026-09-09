import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { FeedbackBanner, MetaPanel } from "./panels";
import { workItemService } from "../../services/auth";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import type { WorkItemDetail } from "./useWorkItemDetail";

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

function detailStub(overrides: Partial<WorkItemDetail> = {}): WorkItemDetail {
  return {
    repositories: [{ id: "repo-1", name: "ild" }],
    templates: [],
    aiProviders: [],
    dependencies: [],
    allWorkItems: [],
    currentRun: null,
    feedbackInput: "",
    setFeedbackInput: vi.fn(),
    feedbackFiles: [],
    addFeedbackFiles: vi.fn(),
    removeFeedbackFile: vi.fn(),
    feedbackError: null,
    prCommentsLoading: false,
    handleApprove: vi.fn(),
    handleReject: vi.fn(),
    handleEdge: vi.fn(),
    handleMerge: vi.fn(),
    ...overrides,
  } as unknown as WorkItemDetail;
}

const attachment = (id: string, fileName: string, sizeBytes: number) => ({
  id,
  fileName,
  contentType: "image/png",
  sizeBytes,
});

function renderMeta(item: WorkItem) {
  render(
    <MemoryRouter>
      <MetaPanel workItem={item} detail={detailStub()} />
    </MemoryRouter>,
  );
}

describe("MetaPanel attachments", () => {
  test("the overview lists what is attached, with sizes", () => {
    renderMeta(
      workItem({
        attachments: [attachment("a1", "sketch.png", 2048), attachment("a2", "run.log", 12)],
      }),
    );

    expect(screen.getByText(/sketch\.png/)).toBeTruthy();
    expect(screen.getByText("2 KB")).toBeTruthy();
    expect(screen.getByText(/run\.log/)).toBeTruthy();
    expect(screen.getByText("12 B")).toBeTruthy();
  });

  test("an item with nothing attached says so rather than showing an empty row", () => {
    renderMeta(workItem());

    const label = screen.getByText("Attachments");
    expect(within(label.parentElement!).getByText("None")).toBeTruthy();
  });

  test("clicking an attachment fetches its bytes for download", async () => {
    const get = vi
      .spyOn(workItemService, "getAttachment")
      .mockResolvedValue(new Blob(["pixels"], { type: "image/png" }));
    // jsdom implements neither, and the util under test uses both.
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();

    renderMeta(workItem({ attachments: [attachment("a1", "sketch.png", 6)] }));
    fireEvent.click(screen.getByTitle("Download sketch.png"));

    await waitFor(() => expect(get).toHaveBeenCalledWith("wi-1", "a1"));
  });
});

describe("FeedbackBanner attachments", () => {
  const parked = workItem({
    status: WorkItemStatus.HumanFeedback,
    humanFeedbackReason: "Human Input Needed",
  });

  function renderBanner(overrides: Partial<WorkItemDetail> = {}) {
    render(<FeedbackBanner workItem={parked} detail={detailStub(overrides)} prompt={null} />);
  }

  test("a human parked for input can attach files alongside their answer", () => {
    const addFeedbackFiles = vi.fn();
    renderBanner({ addFeedbackFiles });

    const file = new File(["pixels"], "sketch.png", { type: "image/png" });
    fireEvent.change(screen.getByLabelText("Attach files to your response"), {
      target: { files: [file] },
    });

    expect(addFeedbackFiles).toHaveBeenCalled();
    // The response box is still there — files supplement the text, not replace it.
    expect(screen.getByPlaceholderText("Optional input or context...")).toBeTruthy();
  });

  test("staged files are listed as pending until the human responds", () => {
    renderBanner({ feedbackFiles: [new File(["pixels"], "sketch.png", { type: "image/png" })] });

    expect(screen.getByText(/sketch\.png/)).toBeTruthy();
    expect(screen.getByText(/on respond/)).toBeTruthy();
  });

  test("a staged file can be dropped again", () => {
    const removeFeedbackFile = vi.fn();
    renderBanner({
      feedbackFiles: [new File(["pixels"], "sketch.png", { type: "image/png" })],
      removeFeedbackFile,
    });

    fireEvent.click(screen.getByLabelText("Remove sketch.png"));

    expect(removeFeedbackFile).toHaveBeenCalledWith(0);
  });

  test("an upload problem is surfaced rather than swallowed", () => {
    renderBanner({ feedbackError: "sketch.png exceeds the 25 MB limit." });

    expect(screen.getByRole("alert").textContent).toContain("25 MB");
  });
});
