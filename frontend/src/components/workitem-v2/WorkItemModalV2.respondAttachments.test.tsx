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

function makeParkedWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
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
    ...overrides,
  };
}

/**
 * The metadata the work item carries once a file has been stored on it, as a
 * read of the item returns it. The note an answer carries is reconciled against
 * this, so a stand-in item has to hold what its uploads put there.
 */
function stored(fileName: string): WorkItemAttachment {
  return {
    id: `att-${fileName}`,
    fileName,
    contentType: "application/octet-stream",
    sizeBytes: 10,
  };
}

/** A file of an arbitrary reported size, without holding that many bytes. */
function fileOfSize(name: string, bytes: number, type = "application/octet-stream"): File {
  const file = new File(["x"], name, { type });
  Object.defineProperty(file, "size", { value: bytes });
  return file;
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
  vi.spyOn(authServices.workItemService, "getPrComments").mockResolvedValue([]);
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

async function renderDialog(workItem: WorkItem) {
  render(
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>,
  );
  await act(async () => {
    await Promise.resolve();
  });
}

const feedback = () => document.querySelector(".wiv2-feedback") as HTMLElement;
const feedbackFileInput = () => feedback().querySelector('input[type="file"]') as HTMLInputElement;
const feedbackTextarea = () => feedback().querySelector("textarea") as HTMLTextAreaElement;

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

async function stage(...files: File[]) {
  await act(async () => {
    fireEvent.change(feedbackFileInput(), { target: { files } });
    await Promise.resolve();
  });
}

async function type(text: string) {
  await act(async () => {
    fireEvent.change(feedbackTextarea(), { target: { value: text } });
    await Promise.resolve();
  });
}

async function waitForLimits() {
  await waitFor(() => expect(feedback().textContent).toContain("25 MB"));
}

describe("answering a run that waits for human input", () => {
  test("the feedback pane offers staging only while a run waits for input", async () => {
    mockServices();
    await renderDialog(makeParkedWorkItem());
    expect(feedbackFileInput()).not.toBeNull();

    cleanup();
    await renderDialog(makeParkedWorkItem({ humanFeedbackReason: "PR Awaiting Merge" }));
    // A PR park's actions include a real remote merge; attaching belongs to
    // answering a run that asked for input.
    expect(feedbackFileInput()).toBeNull();
  });

  test("approving uploads the staged files first, then answers with a note naming them", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    // A read of the item after the uploads finds both files on it.
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeParkedWorkItem({ attachments: [stored("a.png"), stored("b.pdf")] }),
    );
    await renderDialog(makeParkedWorkItem());
    await waitForLimits();

    await stage(fileOfSize("a.png", 10, "image/png"), fileOfSize("b.pdf", 20, "application/pdf"));
    await type("Looks good");
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(upload.mock.calls.map((c) => [c[0], (c[1] as File).name])).toEqual([
      ["wi-1", "a.png"],
      ["wi-1", "b.pdf"],
    ]);
    // The note tells the agent what is on the item, so the files have to be
    // there before the answer goes in.
    expect(upload.mock.invocationCallOrder[1]).toBeLessThan(answer.mock.invocationCallOrder[0]);
    expect(answer).toHaveBeenCalledWith("wi-1", "Looks good\n\nAttached files: a.png, b.pdf");
  });

  test("answering with nothing staged submits exactly what the human typed", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(makeParkedWorkItem());
    await renderDialog(makeParkedWorkItem());

    await type("Looks good");
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledWith("wi-1", "Looks good"));
    expect(upload).not.toHaveBeenCalled();
  });

  test("a rejection and a named edge carry the same note", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const reject = vi
      .spyOn(authServices.workItemService, "humanFeedbackReject")
      .mockResolvedValue(undefined);
    const edge = vi
      .spyOn(authServices.workItemService, "humanFeedbackEdge")
      .mockResolvedValue(undefined);
    // Both answers store a file, so a read of the item finds both.
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeParkedWorkItem({ attachments: [stored("a.png"), stored("b.pdf")] }),
    );
    await renderDialog(
      makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,Needs work,OnFailure" }),
    );
    await waitForLimits();

    await stage(fileOfSize("a.png", 10, "image/png"));
    await type("Have a look");
    await click(screen.getByRole("button", { name: "Needs work" }));

    await waitFor(() => expect(edge).toHaveBeenCalledTimes(1));
    expect(edge).toHaveBeenCalledWith("wi-1", "Needs work", "Have a look\n\nAttached files: a.png");

    await stage(fileOfSize("b.pdf", 10, "application/pdf"));
    await type("Not yet");
    await click(screen.getByRole("button", { name: "Reject" }));

    await waitFor(() => expect(reject).toHaveBeenCalledTimes(1));
    expect(String(reject.mock.calls[0][1])).toContain("Not yet");
    expect(String(reject.mock.calls[0][1])).toContain("b.pdf");
  });
});

describe("failures while answering with files", () => {
  test("a failed upload blocks the answer, and the retry names every file now stored", async () => {
    mockServices();
    let failFirstB = true;
    const upload = vi
      .spyOn(authServices.workItemService, "uploadAttachment")
      .mockImplementation((_id: string, file: File) => {
        if (file.name === "b.pdf" && failFirstB) {
          failFirstB = false;
          return Promise.reject({ status: 503, message: "WorkItemServer unreachable" });
        }
        return Promise.resolve([]);
      });
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    // a.png lands on the first attempt and b.pdf on the retry, so a read of the
    // item finds both by the time the answer is composed.
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeParkedWorkItem({ attachments: [stored("a.png"), stored("b.pdf")] }),
    );
    await renderDialog(makeParkedWorkItem());
    await waitForLimits();

    await stage(fileOfSize("a.png", 10, "image/png"), fileOfSize("b.pdf", 20, "application/pdf"));
    await type("Looks good");
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(feedback().textContent).toContain("WorkItemServer unreachable"));
    expect(answer).not.toHaveBeenCalled();
    expect(feedback().textContent).toContain("a.png");
    expect(feedback().textContent).toContain("b.pdf");

    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    // a.png landed on the first attempt, so only b.pdf is sent again — and the
    // note still names both, because both are now on the item.
    expect(upload.mock.calls.map((c) => (c[1] as File).name)).toEqual(["a.png", "b.pdf", "b.pdf"]);
    expect(answer).toHaveBeenCalledWith("wi-1", "Looks good\n\nAttached files: a.png, b.pdf");
  });

  test("an answer the server refuses is shown and re-sends nothing already stored", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValue({ status: 400, message: "Input must be 8192 characters or fewer." });
    // The upload landed even though the answer was refused, so a read of the
    // item finds the file there for the retry.
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeParkedWorkItem({ attachments: [stored("a.png")] }),
    );
    await renderDialog(makeParkedWorkItem());
    await waitForLimits();

    await stage(fileOfSize("a.png", 10, "image/png"));
    await type("Looks good");
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );
    expect(feedback().textContent).toContain("a.png");

    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    // The file is already stored; a retry of the answer must not upload it twice.
    expect(upload).toHaveBeenCalledTimes(1);
  });

  test("a file staged in the edit form and discarded with it is never answered with", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(makeParkedWorkItem());
    await renderDialog(makeParkedWorkItem());

    await click(screen.getByRole("button", { name: "Edit" }));
    const editForm = document.querySelector("form") as HTMLFormElement;
    await waitFor(() => expect(editForm.textContent).toContain("25 MB"));
    await act(async () => {
      fireEvent.change(editForm.querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [fileOfSize("discarded.png", 10, "image/png")] },
      });
      await Promise.resolve();
    });
    await click(screen.getByRole("button", { name: "Cancel" }));

    await type("Looks good");
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledWith("wi-1", "Looks good"));
    expect(upload).not.toHaveBeenCalled();
  });
});
