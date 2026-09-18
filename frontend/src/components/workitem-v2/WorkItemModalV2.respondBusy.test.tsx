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
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
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
