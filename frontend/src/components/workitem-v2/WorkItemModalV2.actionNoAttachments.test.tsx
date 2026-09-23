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

function mockServices(item: WorkItem) {
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
  vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(item);
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
    hasMore: false,
  });
  vi.spyOn(authServices.loopRunService, "getById").mockRejectedValue({
    status: 404,
    message: "no run",
  });
  const limits = vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
  const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
  return { limits, upload };
}

async function renderDialog(workItem: WorkItem, onClose = vi.fn()) {
  render(
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={onClose} onSave={vi.fn()} />
    </MemoryRouter>,
  );
  await act(async () => {
    await Promise.resolve();
  });
}

const feedback = () => document.querySelector(".wiv2-feedback") as HTMLElement;
const feedbackTextarea = () => feedback().querySelector("textarea") as HTMLTextAreaElement;

/** Lets the attachment limits land, so a picker that depends on them would have rendered. */
async function settle(limits: ReturnType<typeof mockServices>["limits"]) {
  await waitFor(() => expect(limits).toHaveBeenCalled());
  await act(async () => {
    await Promise.resolve();
  });
}

function expectNoAttachmentControl() {
  const pane = feedback();
  expect(pane).not.toBeNull();
  expect(pane.querySelector('input[type="file"]')).toBeNull();
  expect(pane.querySelector(".wiv2-attach-drop")).toBeNull();
  expect(pane.querySelector(".wiv2-attach-row")).toBeNull();
  expect(pane.textContent).not.toMatch(/Attachments/);
  expect(pane.textContent).not.toMatch(/Drop files here or paste a screenshot/);
}

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

async function type(text: string) {
  await act(async () => {
    fireEvent.change(feedbackTextarea(), { target: { value: text } });
    await Promise.resolve();
  });
}

async function pasteFile(target: Element, file: File) {
  await act(async () => {
    fireEvent.paste(target, { clipboardData: { files: [file], items: [], types: ["Files"] } });
    await Promise.resolve();
  });
}

describe("the Action tab's feedback pane carries no attachment control", () => {
  const NO_ATTACHMENT_CASES: Array<{
    name: string;
    item: Partial<WorkItem>;
    textarea: boolean;
    buttons: string[];
  }> = [
    {
      name: "a run waiting for human input shows the prompt, textarea and buttons, but no attachments",
      item: { humanFeedbackActions: "OnSuccess,Needs work,OnFailure" },
      textarea: true,
      buttons: ["Approve", "Reject", "Needs work"],
    },
    {
      name: "a PR awaiting merge has no attachment control either",
      item: { humanFeedbackReason: "PR Awaiting Merge", prUrl: "https://example.test/pr/1" },
      textarea: true,
      buttons: [],
    },
    {
      name: "any other feedback reason has no attachment control",
      item: { humanFeedbackReason: "Loop Failed" },
      textarea: false,
      buttons: [],
    },
  ];

  test.each(NO_ATTACHMENT_CASES)("$name", async ({ item: overrides, textarea, buttons }) => {
    const item = makeParkedWorkItem(overrides);
    const { limits } = mockServices(item);
    await renderDialog(item);
    await settle(limits);

    expectNoAttachmentControl();
    if (textarea) expect(feedbackTextarea()).not.toBeNull();
    for (const name of buttons) expect(screen.getByRole("button", { name })).toBeTruthy();
  });
});

describe("pasting a file into the Action tab's feedback pane", () => {
  test("stages nothing, and answering afterwards uploads nothing", async () => {
    const item = makeParkedWorkItem();
    const { limits, upload } = mockServices(item);
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockResolvedValue(undefined);
    await renderDialog(item);
    await settle(limits);

    await pasteFile(feedbackTextarea(), new File(["x"], "shot.png", { type: "image/png" }));
    await pasteFile(feedback(), new File(["y"], "other.png", { type: "image/png" }));

    expect(feedback().textContent).not.toContain("shot.png");
    expect(feedback().textContent).not.toContain("other.png");
    expectNoAttachmentControl();

    await type("Looks good");
    await click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer).toHaveBeenCalledWith("wi-1", "Looks good");
    expect(upload).not.toHaveBeenCalled();
  });

  test("leaves nothing that makes closing ask about unsaved changes", async () => {
    const item = makeParkedWorkItem();
    const { limits } = mockServices(item);
    const onClose = vi.fn();
    await renderDialog(item, onClose);
    await settle(limits);

    await pasteFile(feedbackTextarea(), new File(["x"], "shot.png", { type: "image/png" }));

    await pressEscapeUntil(() => {
      expect(onClose).toHaveBeenCalledTimes(1);
    });
    expect(screen.queryByText(/Discard unsaved changes/)).toBeNull();
  });
});

describe("answering from the Action tab submits exactly what was typed", () => {
  const ANSWER_CASES: Array<{
    name: string;
    actions: string | null;
    service: "humanFeedbackInput" | "humanFeedbackReject" | "humanFeedbackEdge";
    typed: string | null;
    button: string;
    sent: unknown[];
  }> = [
    {
      name: "approving sends the typed text and uploads nothing",
      actions: null,
      service: "humanFeedbackInput",
      typed: "Looks good",
      button: "Approve",
      sent: ["wi-1", "Looks good"],
    },
    {
      name: "approving with nothing typed sends empty text and uploads nothing",
      actions: null,
      service: "humanFeedbackInput",
      typed: null,
      button: "Approve",
      sent: ["wi-1", ""],
    },
    {
      name: "rejecting sends the typed text and uploads nothing",
      actions: null,
      service: "humanFeedbackReject",
      typed: "Not yet",
      button: "Reject",
      sent: ["wi-1", "Not yet"],
    },
    {
      name: "rejecting with nothing typed sends no reason and uploads nothing",
      actions: null,
      service: "humanFeedbackReject",
      typed: null,
      button: "Reject",
      sent: ["wi-1", undefined],
    },
    {
      name: "taking a named edge sends the typed text and uploads nothing",
      actions: "OnSuccess,Needs work,OnFailure",
      service: "humanFeedbackEdge",
      typed: "Have a look",
      button: "Needs work",
      sent: ["wi-1", "Needs work", "Have a look"],
    },
    {
      name: "taking a named edge with nothing typed sends empty text and uploads nothing",
      actions: "OnSuccess,Needs work,OnFailure",
      service: "humanFeedbackEdge",
      typed: null,
      button: "Needs work",
      sent: ["wi-1", "Needs work", ""],
    },
  ];

  test.each(ANSWER_CASES)("$name", async ({ actions, service, typed, button, sent }) => {
    const item = makeParkedWorkItem({ humanFeedbackActions: actions });
    const { limits, upload } = mockServices(item);
    const answer = vi.spyOn(authServices.workItemService, service).mockResolvedValue(undefined);
    await renderDialog(item);
    await settle(limits);

    if (typed !== null) await type(typed);
    await click(screen.getByRole("button", { name: button }));

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer).toHaveBeenCalledWith(...sent);
    expect(JSON.stringify(answer.mock.calls[0])).not.toContain("Attached files");
    expect(upload).not.toHaveBeenCalled();
  });
});
