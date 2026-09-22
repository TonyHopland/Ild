import { useState } from "react";
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { WorkItem, WorkItemAttachment, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function attachment(id: string, fileName: string): WorkItemAttachment {
  return { id, fileName, contentType: "image/png", sizeBytes: 10 };
}

function makeParkedWorkItem(attachments: WorkItemAttachment[] = []): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "",
    status: WorkItemStatus.HumanFeedback,
    priority: WorkItemPriority.Medium,
    tags: [],
    conversation: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: "Human Input Needed",
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: "run-1",
    dependencyIds: [],
    dependentIds: [],
    attachments,
  };
}

function mockServices() {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
    hasMore: false,
  });
  vi.spyOn(authServices.loopRunService, "getById").mockRejectedValue({
    status: 404,
    message: "no run",
  });
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
}

/**
 * The board around the dialog: it holds the work item and takes the dialog's
 * refreshed copy back, which is what makes a removal visible to everything else
 * still on screen.
 */
function renderBoard(initial: WorkItem) {
  let showRefreshedItem!: (workItem: WorkItem) => void;
  function Board() {
    const [item, setItem] = useState(initial);
    showRefreshedItem = setItem;
    return (
      <MemoryRouter>
        <WorkItemModalV2 workItem={item} onClose={vi.fn()} onSave={setItem} />
      </MemoryRouter>
    );
  }
  render(<Board />);
  return { showRefreshedItem: (workItem: WorkItem) => showRefreshedItem(workItem) };
}

const feedback = () => document.querySelector(".wiv2-feedback") as HTMLElement;

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

describe("telling two attachments apart", () => {
  test("names the one the item still holds when both were called the same", async () => {
    mockServices();
    let held = [attachment("att-1", "shot.png"), attachment("att-2", "shot.png")];
    let next = 0;
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockImplementation(() =>
      Promise.resolve([held[next++]]),
    );
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() =>
      Promise.resolve(makeParkedWorkItem(held)),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input must be 8192 characters or fewer." })
      .mockResolvedValue(undefined);

    renderBoard(makeParkedWorkItem());
    await act(async () => {
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback().textContent).toContain("25 MB"));

    // Two pastes of the same screenshot: the names are identical, only the
    // attachments they became are not.
    await act(async () => {
      fireEvent.change(feedback().querySelector('input[type="file"]') as HTMLInputElement, {
        target: {
          files: [
            new File(["one"], "shot.png", { type: "image/png" }),
            new File(["two"], "shot.png", { type: "image/png" }),
          ],
        },
      });
      fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });
    await click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );

    // One of the two is removed outside this dialog before the retry.
    held = [attachment("att-2", "shot.png")];
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    expect(answer.mock.calls[1][1]).toBe("Looks good\n\nAttached files: shot.png");
  });

  test("names a file by what the work item stored it as", async () => {
    mockServices();
    // The server trims the name it is given before storing it.
    const held = [attachment("att-1", "report.txt")];
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue(held);
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() =>
      Promise.resolve(makeParkedWorkItem(held)),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);

    renderBoard(makeParkedWorkItem());
    await act(async () => {
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback().textContent).toContain("25 MB"));

    await act(async () => {
      fireEvent.change(feedback().querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [new File(["x"], "  report.txt  ", { type: "text/plain" })] },
      });
      fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    // Naming the browser's version would both misname it and, once the item is
    // read back, drop it from the note altogether.
    expect(answer.mock.calls[0][1]).toBe("Looks good\n\nAttached files: report.txt");
  });
});

describe("a file removed from the work item after it was uploaded", () => {
  test("is not named in the answer, and stops showing as attached", async () => {
    mockServices();
    // What the server holds, as the dialog would find it on a fresh read.
    let held = [attachment("att-1", "a.png")];
    const upload = vi
      .spyOn(authServices.workItemService, "uploadAttachment")
      .mockResolvedValue([attachment("att-1", "a.png")]);
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() =>
      Promise.resolve(makeParkedWorkItem(held)),
    );
    const remove = vi
      .spyOn(authServices.workItemService, "deleteAttachment")
      .mockImplementation(() => {
        held = [];
        return Promise.resolve();
      });
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input must be 8192 characters or fewer." })
      .mockResolvedValue(undefined);

    const board = renderBoard(makeParkedWorkItem());
    await act(async () => {
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback().textContent).toContain("25 MB"));

    await act(async () => {
      fireEvent.change(feedback().querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [new File(["x"], "a.png", { type: "image/png" })] },
      });
      fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });
    await click(screen.getByRole("button", { name: "Approve" }));

    // The file is on the item; only the answer was refused, so it stays staged
    // for the retry and shows as attached.
    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );
    expect(feedback().textContent).toContain("Attached");

    // The board catches up with the upload, and the human removes the file from
    // the work item's own list before answering again.
    await act(async () => {
      board.showRefreshedItem(makeParkedWorkItem(held));
      await Promise.resolve();
    });
    await click(screen.getByRole("tab", { name: "Overview" }));
    await click(screen.getByRole("button", { name: "Remove attachment a.png" }));
    await waitFor(() => expect(remove).toHaveBeenCalledTimes(1));

    await click(screen.getByRole("tab", { name: /^Action/ }));
    await waitFor(() => expect(feedback().textContent).not.toContain("Attached"));

    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    // The file is gone, so the answer must not tell the agent to look for it.
    expect(answer.mock.calls[1][1]).toBe("Looks good");
    expect(upload).toHaveBeenCalledTimes(1);
  });

  test("is not named when it went from the item behind the dialog's back", async () => {
    mockServices();
    // Removed by an agent, or by the same human in another tab: this dialog is
    // never told, so only reading the item back can catch it.
    let held = [attachment("att-1", "a.png")];
    const upload = vi
      .spyOn(authServices.workItemService, "uploadAttachment")
      .mockResolvedValue([attachment("att-1", "a.png")]);
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() =>
      Promise.resolve(makeParkedWorkItem(held)),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input must be 8192 characters or fewer." })
      .mockResolvedValue(undefined);

    renderBoard(makeParkedWorkItem());
    await act(async () => {
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback().textContent).toContain("25 MB"));

    await act(async () => {
      fireEvent.change(feedback().querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [new File(["x"], "a.png", { type: "image/png" })] },
      });
      fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });
    await click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );

    held = [];
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    expect(answer.mock.calls[1][1]).toBe("Looks good");
    expect(upload).toHaveBeenCalledTimes(1);
  });

  test("a file the item still holds is named as before", async () => {
    mockServices();
    const held = [attachment("att-1", "a.png")];
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([
      attachment("att-1", "a.png"),
    ]);
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() =>
      Promise.resolve(makeParkedWorkItem(held)),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);

    renderBoard(makeParkedWorkItem());
    await act(async () => {
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback().textContent).toContain("25 MB"));

    await act(async () => {
      fireEvent.change(feedback().querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [new File(["x"], "a.png", { type: "image/png" })] },
      });
      fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });
    await click(screen.getByRole("button", { name: "Approve" }));

    // The dialog's own copy of the item still predates the upload, so naming the
    // file can only come from reading the item back.
    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer.mock.calls[0][1]).toBe("Looks good\n\nAttached files: a.png");
  });
});
