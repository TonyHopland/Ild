import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { useState } from "react";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { pressEscapeUntil } from "../../test-support";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { stageParkedNode } from "../../test-support.parkedNode";

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
    latestLoopRunId: "run-1",
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
  vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(makeParkedWorkItem());
  vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation(async (runId) => ({
    runId,
    messages: [],
    lastEventId: null,
  }));
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
    hasMore: false,
  });
  stageParkedNode();
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

async function type(text: string) {
  await act(async () => {
    fireEvent.change(feedback().querySelector("textarea") as HTMLTextAreaElement, {
      target: { value: text },
    });
    await Promise.resolve();
  });
}

/** Lets the dialog's first reads land, so the answer buttons are wired up. */
async function settle() {
  await waitFor(() => expect(authServices.settingsService.getAttachmentLimits).toHaveBeenCalled());
  await act(async () => {
    await Promise.resolve();
  });
  await screen.findByRole("button", { name: "Approve" });
}

describe("an answer that has been submitted", () => {
  test("holds its controls until the dialog is showing what the run did next", async () => {
    mockServices();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    // The item is reread once the answer is in; until it arrives the dialog is
    // still showing a run that is waiting for input.
    const refreshed = deferred<WorkItem>();
    vi.spyOn(authServices.workItemService, "getById").mockReturnValue(refreshed.promise);
    await renderDialog();
    await settle();

    const approve = screen.getByRole("button", { name: "Approve" });
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect((approve as HTMLButtonElement).disabled).toBe(true);

    // A second press here would answer a question that has already been
    // answered, because the pane still shows the parked item.
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });
    expect(answer).toHaveBeenCalledTimes(1);

    await act(async () => {
      refreshed.resolve(makeParkedWorkItem());
      await Promise.resolve();
    });
    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(false));
    expect(answer).toHaveBeenCalledTimes(1);
  });
});

describe("an answer whose refresh never arrives", () => {
  test("says so and keeps the controls held rather than offering to answer again", async () => {
    mockServices();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    // The answer is in, but the item cannot be read back, so this view is still
    // showing a run that is waiting for input.
    vi.spyOn(authServices.workItemService, "getById").mockRejectedValue({
      status: 503,
      message: "WorkItemServer unreachable",
    });
    await renderDialog();
    await settle();

    const approve = screen.getByRole("button", { name: "Approve" });
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(feedback().textContent).toContain("could not be refreshed"));
    expect((approve as HTMLButtonElement).disabled).toBe(true);

    // A second press must not answer again: the run already has its answer.
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });
    expect(answer).toHaveBeenCalledTimes(1);
    expect((approve as HTMLButtonElement).disabled).toBe(true);
  });
});
describe("answering while an answer is already in flight", () => {
  test("the answer buttons are held until the answer is done, and a second press sends nothing", async () => {
    mockServices();
    const submitted = deferred<void>();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockReturnValue(submitted.promise);
    await renderDialog();
    await settle();

    const approve = screen.getByRole("button", { name: "Approve" });
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });

    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(true));
    expect((screen.getByRole("button", { name: "Reject" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });
    expect(answer).toHaveBeenCalledTimes(1);

    await act(async () => {
      submitted.resolve();
      await Promise.resolve();
    });

    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(false));
    expect(answer).toHaveBeenCalledTimes(1);
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
    await settle();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      await Promise.resolve();
    });
    await waitFor(() =>
      expect((document.querySelector("form") as HTMLFormElement).textContent).toContain("25 MB"),
    );
    await act(async () => {
      fireEvent.change(document.querySelector('form input[type="file"]') as HTMLInputElement, {
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

  test("nor the answer's submit while it is in flight", async () => {
    mockServices();
    const submitted = deferred<void>();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockReturnValue(submitted.promise);
    const onClose = vi.fn();
    await renderDialog({ onClose });
    await settle();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    await act(async () => {
      fireEvent.keyDown(document, { key: "Escape" });
      for (const close of screen.getAllByRole("button", { name: "Close" })) fireEvent.click(close);
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

describe("an answer the server refuses", () => {
  test("shows the server's message and releases the buttons for a retry", async () => {
    mockServices();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input must be 8192 characters or fewer." })
      .mockResolvedValue(undefined);
    await renderDialog();
    await settle();
    await type("Looks good");

    const approve = screen.getByRole("button", { name: "Approve" });
    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });
    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );
    expect((approve as HTMLButtonElement).disabled).toBe(false);

    await act(async () => {
      fireEvent.click(approve);
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    expect(answer.mock.calls[1]).toEqual(["wi-1", "run-1", "Looks good"]);
  });
});

describe("an answer waiting to be retried", () => {
  test("survives an edit opened and cancelled beside it", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValueOnce({ status: 400, message: "Input must be 8192 characters or fewer." })
      .mockResolvedValue(undefined);
    await renderDialog();
    await settle();
    await type("Looks good");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });
    await waitFor(() =>
      expect(feedback().textContent).toContain("Input must be 8192 characters or fewer."),
    );

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(feedback()).toBeTruthy());
    expect((feedback().querySelector("textarea") as HTMLTextAreaElement).value).toBe("Looks good");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(2));
    expect(answer.mock.calls[1]).toEqual(["wi-1", "run-1", "Looks good"]);
    expect(upload).not.toHaveBeenCalled();
  });
});

describe("closing with feedback typed in the pane", () => {
  test("Escape asks before discarding it", async () => {
    mockServices();
    const onClose = vi.fn();
    await renderDialog({ onClose });
    await settle();
    await type("Half an answer");

    await pressEscapeUntil(() => {
      expect(screen.getByText(/Discard unsaved changes/)).toBeTruthy();
    });
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe("an answer sent after the run has moved on", () => {
  const TYPED = "Please also fix the flaky test";
  const MOVED_ON = { status: 409, message: "The run is not waiting for an answer." };

  function mockAnswerServices() {
    mockServices();
    vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
  }

  /** The dialog as the board hosts it: keyed by the item, and what onSave hands back is what it shows next. */
  async function renderHosted(initial: WorkItem) {
    let show!: (workItem: WorkItem) => void;
    function Host() {
      const [workItem, setWorkItem] = useState(initial);
      show = setWorkItem;
      return (
        <WorkItemModalV2
          key={workItem.id}
          workItem={workItem}
          onClose={vi.fn()}
          onSave={setWorkItem}
        />
      );
    }
    render(
      <MemoryRouter>
        <Host />
      </MemoryRouter>,
    );
    await act(async () => {
      await Promise.resolve();
    });
    return { show: (workItem: WorkItem) => act(async () => show(workItem)) };
  }

  const notice = () => screen.queryByText(/moved on/i);

  async function answerRefusedWithRunNow(now: WorkItem) {
    mockAnswerServices();
    vi.spyOn(authServices.workItemService, "humanFeedbackInput").mockRejectedValue(MOVED_ON);
    const hosted = await renderHosted(makeParkedWorkItem());
    await settle();
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(now);
    await type(TYPED);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });
    await waitFor(() => expect(notice()).toBeTruthy());
    return hosted;
  }

  const runningOn = (runId: string): WorkItem => ({
    ...makeParkedWorkItem(),
    status: WorkItemStatus.Running,
    humanFeedbackReason: null,
    currentLoopRunId: runId,
    latestLoopRunId: runId,
  });

  test("says it was not sent, quotes the text outside the box, and the box is gone once the item is re-read", async () => {
    await answerRefusedWithRunNow(runningOn("run-1"));

    await waitFor(() => expect(feedback()).toBeNull());
    expect(notice()!.textContent).toMatch(/\bnot\b[^.]*\b(sent|delivered)\b/i);
    const quoted = screen.getByText(TYPED);
    expect(["TEXTAREA", "INPUT"]).not.toContain(quoted.tagName);
    expect(quoted.closest(".wiv2-feedback")).toBeNull();
    expect(notice()!.closest(".wiv2-feedback")).toBeNull();
  });

  test("stays when the re-read item's latest run is a different run", async () => {
    await answerRefusedWithRunNow(runningOn("run-2"));

    await waitFor(() => expect(feedback()).toBeNull());
    expect(notice()).toBeTruthy();
    expect(screen.getByText(TYPED)).toBeTruthy();
  });

  test("Dismiss removes the notice and its text", async () => {
    await answerRefusedWithRunNow(runningOn("run-1"));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
      await Promise.resolve();
    });

    expect(notice()).toBeNull();
    expect(screen.queryByText(TYPED)).toBeNull();
  });

  test("does not carry over to another work item", async () => {
    const { show } = await answerRefusedWithRunNow(runningOn("run-1"));

    await show({ ...runningOn("run-9"), id: "wi-2", title: "Another item" });

    await waitFor(() => expect(screen.getByRole("heading", { name: "Another item" })).toBeTruthy());
    expect(notice()).toBeNull();
    expect(screen.queryByText(TYPED)).toBeNull();
  });

  test("the run parking for an answer again opens an empty box", async () => {
    const { show } = await answerRefusedWithRunNow(runningOn("run-1"));
    await waitFor(() => expect(feedback()).toBeNull());

    await show(makeParkedWorkItem());

    await waitFor(() => expect(feedback()).toBeTruthy());
    expect((feedback().querySelector("textarea") as HTMLTextAreaElement).value).toBe("");
  });

  test("a successful answer leaves no notice", async () => {
    mockAnswerServices();
    vi.spyOn(authServices.workItemService, "humanFeedbackInput").mockResolvedValue(undefined);
    await renderHosted(makeParkedWorkItem());
    await settle();
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(runningOn("run-1"));
    await type(TYPED);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Approve" }));
      await Promise.resolve();
    });

    await waitFor(() => expect(feedback()).toBeNull());
    expect(notice()).toBeNull();
    expect(screen.queryByText(TYPED)).toBeNull();
  });
});
