import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";
import EditPanel from "./EditPanel";
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

const detail = {
  repositories: [{ id: "repo-1", name: "ild" }],
  templates: [],
  aiProviders: [],
} as unknown as WorkItemDetail;

function renderPanel(item: WorkItem | null) {
  const onSave = vi.fn();
  const onDone = vi.fn();
  render(<EditPanel workItem={item} detail={detail} onSave={onSave} onDone={onDone} />);
  return { onSave, onDone };
}

const png = () => new File(["pixels"], "sketch.png", { type: "image/png" });

describe("EditPanel attachments", () => {
  test("a file picked while editing is uploaded on save, not on pick", async () => {
    const item = workItem();
    vi.spyOn(workItemService, "update").mockResolvedValue(item);
    vi.spyOn(workItemService, "transition").mockResolvedValue(undefined as never);
    const upload = vi.spyOn(workItemService, "uploadAttachment").mockResolvedValue({
      id: "a1",
      fileName: "sketch.png",
      contentType: "image/png",
      sizeBytes: 6,
    });
    const reread = vi.spyOn(workItemService, "getById").mockResolvedValue(item);

    const { onSave } = renderPanel(item);
    const file = png();
    fireEvent.change(screen.getByLabelText("Attachments"), { target: { files: [file] } });

    // Staged, but nothing has been sent yet — a cancelled edit must upload nothing.
    expect(await screen.findByLabelText("Remove sketch.png")).toBeTruthy();
    expect(upload).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => expect(upload).toHaveBeenCalledWith("wi-1", file));
    // Attachments live on the server-held item, so the saved copy is re-read.
    await waitFor(() => expect(reread).toHaveBeenCalledWith("wi-1"));
    await waitFor(() => expect(onSave).toHaveBeenCalled());
  });

  test("a file staged on the create form is uploaded against the new item's id", async () => {
    const created = workItem({ id: "wi-new" });
    vi.spyOn(workItemService, "create").mockResolvedValue(created);
    const upload = vi.spyOn(workItemService, "uploadAttachment").mockResolvedValue({
      id: "a1",
      fileName: "sketch.png",
      contentType: "image/png",
      sizeBytes: 6,
    });
    vi.spyOn(workItemService, "getById").mockResolvedValue(created);

    renderPanel(null);
    const file = png();
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "New item" } });
    // Both are required by the form, so the submit would never fire without them.
    fireEvent.change(screen.getByLabelText("Repository"), { target: { value: "repo-1" } });
    fireEvent.change(screen.getByLabelText("Attachments"), { target: { files: [file] } });
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => expect(upload).toHaveBeenCalledWith("wi-new", file));
  });

  test("removing an existing attachment deletes it on save", async () => {
    const item = workItem({
      attachments: [{ id: "a1", fileName: "old.png", contentType: "image/png", sizeBytes: 10 }],
    });
    vi.spyOn(workItemService, "update").mockResolvedValue(item);
    vi.spyOn(workItemService, "getById").mockResolvedValue(item);
    const remove = vi.spyOn(workItemService, "deleteAttachment").mockResolvedValue(undefined);

    renderPanel(item);
    fireEvent.click(screen.getByLabelText("Remove old.png"));
    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => expect(remove).toHaveBeenCalledWith("wi-1", "a1"));
  });

  test("a save that touches no attachment neither uploads nor re-reads", async () => {
    const item = workItem();
    vi.spyOn(workItemService, "update").mockResolvedValue(item);
    const upload = vi.spyOn(workItemService, "uploadAttachment");
    const reread = vi.spyOn(workItemService, "getById");

    renderPanel(item);
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Renamed" } });
    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => expect(workItemService.update).toHaveBeenCalled());
    expect(upload).not.toHaveBeenCalled();
    expect(reread).not.toHaveBeenCalled();
  });

  test("an oversized file is refused before it is staged", async () => {
    const item = workItem();
    const upload = vi.spyOn(workItemService, "uploadAttachment");

    renderPanel(item);
    const huge = new File(["x"], "huge.bin");
    Object.defineProperty(huge, "size", { value: 26 * 1024 * 1024 });
    fireEvent.change(screen.getByLabelText("Attachments"), { target: { files: [huge] } });

    expect(await screen.findByText(/exceeds the 25 MB limit/)).toBeTruthy();
    expect(screen.queryByLabelText("Remove huge.bin")).toBeNull();
    expect(upload).not.toHaveBeenCalled();
  });
});
