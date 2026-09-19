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
  return { id, fileName, contentType: "image/png", sizeBytes: 2048 };
}

function makeWorkItem(): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "",
    status: WorkItemStatus.Ready,
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
    attachments: [attachment("att-1", "first.png"), attachment("att-2", "second.png")],
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
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
  vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(makeWorkItem());
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
}

async function renderDialog() {
  render(
    <MemoryRouter>
      <WorkItemModalV2 workItem={makeWorkItem()} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>,
  );
  await act(async () => {
    await Promise.resolve();
  });
}

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

const button = (name: string) => screen.getByRole("button", { name }) as HTMLButtonElement;

describe("each attachment row answers for itself", () => {
  test("a row stays held until its own request settles, not the other's", async () => {
    mockServices();
    const first = deferred<Blob>();
    const second = deferred<Blob>();
    vi.spyOn(authServices.workItemService, "downloadAttachment").mockImplementation(
      (_id: string, attachmentId: string) =>
        attachmentId === "att-1" ? first.promise : second.promise,
    );
    await renderDialog();

    await click(button("Download first.png"));
    await click(button("Download second.png"));
    expect(button("Download first.png").disabled).toBe(true);
    expect(button("Download second.png").disabled).toBe(true);

    await act(async () => {
      first.resolve(new Blob(["one"]));
      await Promise.resolve();
    });

    // The first row is free again; the second is still waiting on its own bytes,
    // and clicking it now would save the same file twice.
    await waitFor(() => expect(button("Download first.png").disabled).toBe(false));
    expect(button("Download second.png").disabled).toBe(true);
    expect(button("Remove attachment second.png").disabled).toBe(true);

    await act(async () => {
      second.resolve(new Blob(["two"]));
      await Promise.resolve();
    });
    await waitFor(() => expect(button("Download second.png").disabled).toBe(false));
  });

  test("a row's failure is its own, and another row's action does not wipe it", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "deleteAttachment").mockRejectedValue({
      status: 503,
      message: "WorkItemServer unreachable",
    });
    const download = deferred<Blob>();
    vi.spyOn(authServices.workItemService, "downloadAttachment").mockReturnValue(download.promise);
    await renderDialog();

    await click(button("Remove attachment first.png"));
    await waitFor(() => expect(screen.getByText("WorkItemServer unreachable")).toBeTruthy());

    await click(button("Download second.png"));
    await act(async () => {
      download.resolve(new Blob(["two"]));
      await Promise.resolve();
    });

    // The removal that failed still says so.
    expect(screen.getByText("WorkItemServer unreachable")).toBeTruthy();
  });
});
