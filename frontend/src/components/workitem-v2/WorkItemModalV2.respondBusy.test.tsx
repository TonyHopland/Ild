import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { pressEscapeUntil } from "../../test-support";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeParkedWorkItem(): WorkItem {
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
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function mockServices() {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  // The edit form's repository select is required, so the item's repository has
  // to be among the options or the browser refuses to submit the form.
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
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getPrComments").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(makeParkedWorkItem());
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

async function renderDialog(props: Partial<React.ComponentProps<typeof WorkItemModalV2>> = {}) {
  render(
    <MemoryRouter>
      <WorkItemModalV2
        workItem={makeParkedWorkItem()}
        onClose={vi.fn()}
        onSave={vi.fn()}
        {...props}
      />
    </MemoryRouter>,
  );
  await act(async () => {
    await Promise.resolve();
  });
}

const feedback = () => document.querySelector(".wiv2-feedback") as HTMLElement;

async function stage(...files: File[]) {
  await act(async () => {
    fireEvent.change(feedback().querySelector('input[type="file"]') as HTMLInputElement, {
      target: { files },
    });
    await Promise.resolve();
  });
}

async function waitForLimits() {
  await waitFor(() => expect(feedback().textContent).toContain("25 MB"));
}

async function type(text: string) {
  await act(async () => {
    fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
      target: { value: text },
    });
    await Promise.resolve();
  });
}

describe("the note an answer carries", () => {
  test("a retry after a refused answer still names the files already stored", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input is too long." })
      .mockResolvedValue(undefined);
    await renderDialog();
    await waitForLimits();
    await stage(new File(["x"], "a.png", { type: "image/png" }));
    await type("Looks good");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback().textContent).toContain("Input is too long."));

    // The file landed on the first attempt, so the retry has nothing to send —
    // but the note still has to tell the agent the file is there.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    expect(answer.mock.calls[1][1]).toBe("Looks good\n\nAttached files: a.png");
    expect(upload).toHaveBeenCalledTimes(1);
  });

  test("carries what the human had typed by the time the uploads finished", async () => {
    mockServices();
    const upload = deferred<never[]>();
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockReturnValue(upload.promise);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    await renderDialog();
    await waitForLimits();
    await stage(new File(["x"], "a.png", { type: "image/png" }));
    await type("half");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    // The upload is slow and the human finishes the sentence while it runs.
    await type("half a sentence, now finished");
    await act(async () => {
      upload.resolve([]);
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer.mock.calls[0][1]).toBe("half a sentence, now finished\n\nAttached files: a.png");
  });
});

describe("answering while an answer is already in flight", () => {
  test("the answer buttons are held until the upload and the answer are done", async () => {
    mockServices();
    const upload = deferred<never[]>();
    const uploading = vi
      .spyOn(authServices.workItemService, "uploadAttachment")
      .mockReturnValue(upload.promise);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    await renderDialog();
    await waitForLimits();
    await stage(new File(["x"], "shot.png", { type: "image/png" }));

    const approve = screen.getByRole("button", { name: "Approve" });
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });

    // The upload is still going: pressing again must not send a second copy.
    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(true));
    expect((screen.getByRole("button", { name: "Reject" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });
    expect(uploading).toHaveBeenCalledTimes(1);
    expect(answer).not.toHaveBeenCalled();

    await act(async () => {
      upload.resolve([]);
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(uploading).toHaveBeenCalledTimes(1);
    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(false));
  });
});

describe("editing while an answer is uploading", () => {
  test("is not offered until the batch is done", async () => {
    mockServices();
    const upload = deferred<never[]>();
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockReturnValue(upload.promise);
    vi.spyOn(authServices.workItemService, "humanFeedbackInput").mockResolvedValue(undefined);
    await renderDialog();
    await waitForLimits();
    await stage(new File(["x"], "shot.png", { type: "image/png" }));

    const edit = screen.getByRole("button", { name: "Edit" });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    // The edit form saves from the same staging list, and leaving it empties
    // that list — neither belongs on top of a batch still going up.
    await waitFor(() => expect((edit as HTMLButtonElement).disabled).toBe(true));

    await act(async () => {
      upload.resolve([]);
      await Promise.resolve();
    });
    await waitFor(() => expect((edit as HTMLButtonElement).disabled).toBe(false));
  });
});

describe("staging a file while the answer is being submitted", () => {
  test("keeps it staged instead of clearing it away with the answer", async () => {
    mockServices();
    const submitted = deferred<void>();
    vi.spyOn(authServices.workItemService, "humanFeedbackInput").mockReturnValue(submitted.promise);
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    await renderDialog();
    await waitForLimits();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    // The answer is on its way with nothing attached; the file dropped now
    // belongs to whatever the human does next, not to the answer just sent.
    await stage(new File(["x"], "late.png", { type: "image/png" }));
    await act(async () => {
      submitted.resolve();
      await Promise.resolve();
    });

    await waitFor(() => expect(feedback().textContent).toContain("late.png"));
    expect(upload).not.toHaveBeenCalled();
  });
});

describe("closing while the dialog is in the middle of something", () => {
  test("neither Escape nor Close dismisses the save request that precedes the uploads", async () => {
    mockServices();
    const saving = deferred<WorkItem>();
    vi.spyOn(authServices.workItemService, "update").mockReturnValue(saving.promise);
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const onClose = vi.fn();
    await renderDialog({ onClose });
    await waitForLimits();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.change(document.querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [new File(["x"], "shot.png", { type: "image/png" })] },
      });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Update" }));
      await Promise.resolve();
    });

    // The save request is out and nothing has been uploaded yet: this is the
    // window in which a dismissal used to be accepted, leaving the save to
    // upload files the human had just discarded.
    expect(upload).not.toHaveBeenCalled();
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      await Promise.resolve();
    });
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.queryByText(/Discard unsaved changes/)).toBeNull();

    await act(async () => {
      saving.resolve(makeParkedWorkItem());
      await Promise.resolve();
    });
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
  });

  test("nor the answer's submit, after its uploads are done", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const submitted = deferred<void>();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockReturnValue(submitted.promise);
    const onClose = vi.fn();
    await renderDialog({ onClose });
    await waitForLimits();
    await stage(new File(["x"], "shot.png", { type: "image/png" }));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    // Waiting for the submit to be out puts the dialog past its uploads: the
    // only thing still running is the answer itself.
    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      await Promise.resolve();
    });
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.queryByText(/Discard unsaved changes/)).toBeNull();

    await act(async () => {
      submitted.resolve();
      await Promise.resolve();
    });
    await waitFor(() => expect(feedback()).toBeTruthy());
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      await Promise.resolve();
    });
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});

describe("closing while an upload is on its way", () => {
  test("waits for the batch instead of discarding over it", async () => {
    mockServices();
    const upload = deferred<never[]>();
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockReturnValue(upload.promise);
    vi.spyOn(authServices.workItemService, "humanFeedbackInput").mockResolvedValue(undefined);
    const onClose = vi.fn();
    await renderDialog({ onClose });
    await waitForLimits();
    await stage(new File(["x"], "shot.png", { type: "image/png" }));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    // The file is in the air: it cannot be called back, so the dialog neither
    // closes nor offers to throw it away.
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      await Promise.resolve();
    });
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.queryByText(/Discard unsaved changes/)).toBeNull();

    await act(async () => {
      upload.resolve([]);
      await Promise.resolve();
    });

    // Once the batch is done the dialog answers Escape again.
    await waitFor(() => expect(feedback()).toBeTruthy());
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      await Promise.resolve();
    });
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});

describe("an answer waiting to be retried", () => {
  test("survives an edit opened and cancelled beside it", async () => {
    mockServices();
    const upload = vi
      .spyOn(authServices.workItemService, "uploadAttachment")
      .mockResolvedValue([
        { id: "att-1", fileName: "a.txt", contentType: "text/plain", sizeBytes: 1 },
      ]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input must be 8192 characters or fewer." })
      .mockResolvedValue(undefined);
    await renderDialog();
    await waitForLimits();
    await stage(new File(["x"], "a.txt", { type: "text/plain" }));
    await act(async () => {
      fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });
    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );

    // The file is on the item and the answer is waiting to be retried. Opening
    // an edit and cancelling it discards what was staged in the edit — nothing
    // else.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(feedback().textContent).toContain("a.txt"));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    expect(answer.mock.calls[1][1]).toBe("Looks good\n\nAttached files: a.txt");
    expect(upload).toHaveBeenCalledTimes(1);
  });
});

describe("closing with a file staged in the feedback pane", () => {
  test("Escape asks before discarding it", async () => {
    mockServices();
    const onClose = vi.fn();
    await renderDialog({ onClose });
    await waitForLimits();
    await stage(new File(["x"], "shot.png", { type: "image/png" }));

    // A staged screenshot is unsaved work exactly as typed feedback is.
    await pressEscapeUntil(() => {
      expect(screen.getByText(/Discard unsaved changes/)).toBeTruthy();
    });
    expect(onClose).not.toHaveBeenCalled();
  });
});
