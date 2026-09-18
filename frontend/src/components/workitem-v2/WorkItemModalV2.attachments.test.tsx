import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import { formatBytes } from "../../utils/attachments";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
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
    ...overrides,
  };
}

/** A file of an arbitrary reported size, without holding that many bytes. */
function fileOfSize(name: string, bytes: number, type = "application/octet-stream"): File {
  const file = new File(["x"], name, { type });
  Object.defineProperty(file, "size", { value: bytes });
  return file;
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
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
}

async function renderDialog(
  workItem: WorkItem | null,
  props: Partial<React.ComponentProps<typeof WorkItemModalV2>> = {},
) {
  const result = render(
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} {...props} />
    </MemoryRouter>,
  );
  await act(async () => {
    await Promise.resolve();
  });
  return result;
}

const form = () => document.querySelector("form") as HTMLFormElement;
const fileInput = () => document.querySelector('input[type="file"]') as HTMLInputElement;
const dropArea = () => document.querySelector(".wiv2-attach-drop") as HTMLElement;

/** The picker only knows the maximum once the limits endpoint has answered. */
async function waitForLimits() {
  await waitFor(() => expect(form().textContent).toContain("25 MB"));
}

async function pick(...files: File[]) {
  await act(async () => {
    fireEvent.change(fileInput(), { target: { files } });
    await Promise.resolve();
  });
}

async function drop(...files: File[]) {
  await act(async () => {
    fireEvent.drop(dropArea(), { dataTransfer: { files, types: ["Files"] } });
    await Promise.resolve();
  });
}

async function paste(target: Element, ...files: File[]) {
  await act(async () => {
    fireEvent.paste(target, { clipboardData: { files, items: [], types: ["Files"] } });
    await Promise.resolve();
  });
}

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

async function openEdit() {
  await click(screen.getByRole("button", { name: "Edit" }));
}

async function fillCreateForm() {
  await screen.findByRole("option", { name: "my-repo" });
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Fresh item" } });
    fireEvent.change(screen.getByLabelText("Repository"), { target: { value: "repo-1" } });
    await Promise.resolve();
  });
}

describe("staging files on the work item form", () => {
  test("stages files from the picker, a drop and a paste, each with its size", async () => {
    mockServices();
    await renderDialog(null);
    await waitForLimits();

    const picked = fileOfSize("picked.png", 2048, "image/png");
    const dropped = fileOfSize("dropped.pdf", 4096, "application/pdf");
    const pasted = fileOfSize("pasted.png", 1024, "image/png");

    await pick(picked);
    await drop(dropped);
    await paste(screen.getByLabelText("Description"), pasted);

    for (const file of [picked, dropped, pasted]) {
      expect(form().textContent).toContain(file.name);
      expect(form().textContent).toContain(formatBytes(file.size));
      expect(screen.getByRole("button", { name: `Remove ${file.name}` })).toBeTruthy();
    }
  });

  test("a staged file can be dropped again before anything is uploaded", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const created = makeWorkItem({ id: "wi-new" });
    vi.spyOn(authServices.workItemService, "create").mockResolvedValue(created);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(created);
    await renderDialog(null);
    await waitForLimits();
    await fillCreateForm();

    await pick(fileOfSize("keep.png", 10, "image/png"), fileOfSize("drop-me.png", 10, "image/png"));
    await click(screen.getByRole("button", { name: "Remove drop-me.png" }));

    expect(form().textContent).not.toContain("drop-me.png");

    await click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
    expect((upload.mock.calls[0][1] as File).name).toBe("keep.png");
  });

  test("shows the configured per-file maximum", async () => {
    mockServices();
    await renderDialog(null);

    await waitFor(() => expect(form().textContent).toContain("25 MB"));
  });

  test("refuses a file over the maximum, naming the limit, and never uploads it", async () => {
    mockServices();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const created = makeWorkItem({ id: "wi-new" });
    vi.spyOn(authServices.workItemService, "create").mockResolvedValue(created);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(created);
    await renderDialog(null);
    await waitForLimits();
    await fillCreateForm();

    await pick(fileOfSize("big.bin", 30 * MB), fileOfSize("small.png", 10, "image/png"));

    expect(form().textContent).toContain("big.bin");
    expect(form().textContent).toContain("25 MB");
    expect(screen.queryByRole("button", { name: "Remove big.bin" })).toBeNull();

    await click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
    expect(upload.mock.calls.map((c) => (c[1] as File).name)).toEqual(["small.png"]);
  });
});

describe("saving uploads the staged files", () => {
  test("creating uploads each staged file on its own request against the new item", async () => {
    mockServices();
    const created = makeWorkItem({ id: "wi-new", title: "Fresh item" });
    const create = vi.spyOn(authServices.workItemService, "create").mockResolvedValue(created);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(created);
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    const onClose = vi.fn();
    await renderDialog(null, { onClose });
    await waitForLimits();
    await fillCreateForm();

    await pick(fileOfSize("a.png", 10, "image/png"), fileOfSize("b.pdf", 20, "application/pdf"));
    await click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(create).toHaveBeenCalledTimes(1);
    expect(upload.mock.calls.map((c) => [c[0], (c[1] as File).name])).toEqual([
      ["wi-new", "a.png"],
      ["wi-new", "b.pdf"],
    ]);
  });

  test("the create form stays open until the uploads have been attempted", async () => {
    mockServices();
    const created = makeWorkItem({ id: "wi-new" });
    vi.spyOn(authServices.workItemService, "create").mockResolvedValue(created);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(created);
    const pending = deferred<never[]>();
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockReturnValue(pending.promise);
    const onClose = vi.fn();
    await renderDialog(null, { onClose });
    await waitForLimits();
    await fillCreateForm();

    await pick(fileOfSize("a.png", 10, "image/png"));
    await click(screen.getByRole("button", { name: "Create" }));

    expect(onClose).not.toHaveBeenCalled();

    await act(async () => {
      pending.resolve([]);
      await Promise.resolve();
    });

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });

  test("saving an edit uploads the staged files against the item being edited", async () => {
    mockServices();
    const item = makeWorkItem();
    vi.spyOn(authServices.workItemService, "update").mockResolvedValue(item);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(item);
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    await renderDialog(item);
    await openEdit();
    await waitForLimits();

    await pick(fileOfSize("note.txt", 12, "text/plain"));
    await click(screen.getByRole("button", { name: "Update" }));

    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
    expect(upload.mock.calls[0][0]).toBe("wi-1");
    expect((upload.mock.calls[0][1] as File).name).toBe("note.txt");
    // The edit view only closes once the save and the uploads are done.
    await waitFor(() => expect(screen.queryByRole("button", { name: "Update" })).toBeNull());
  });
});

describe("a half-failed upload batch", () => {
  test("reports the failure, keeps the form open and re-sends only what did not land", async () => {
    mockServices();
    const item = makeWorkItem();
    vi.spyOn(authServices.workItemService, "update").mockResolvedValue(item);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(item);
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
    await renderDialog(item);
    await openEdit();
    await waitForLimits();

    await pick(fileOfSize("a.png", 10, "image/png"), fileOfSize("b.pdf", 10, "application/pdf"));
    await click(screen.getByRole("button", { name: "Update" }));

    await waitFor(() => expect(form().textContent).toContain("WorkItemServer unreachable"));
    expect(screen.getByRole("button", { name: "Update" })).toBeTruthy();

    await click(screen.getByRole("button", { name: "Update" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Update" })).toBeNull());
    expect(upload.mock.calls.map((c) => (c[1] as File).name)).toEqual(["a.png", "b.pdf", "b.pdf"]);
  });

  test("a retry in create mode saves onto the item already created instead of a second one", async () => {
    mockServices();
    const created = makeWorkItem({ id: "wi-new", title: "Fresh item" });
    const create = vi.spyOn(authServices.workItemService, "create").mockResolvedValue(created);
    const update = vi.spyOn(authServices.workItemService, "update").mockResolvedValue(created);
    const transition = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(created);
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
    const onClose = vi.fn();
    await renderDialog(null, { onClose });
    await waitForLimits();
    await fillCreateForm();

    await pick(fileOfSize("a.png", 10, "image/png"), fileOfSize("b.pdf", 10, "application/pdf"));
    await click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(form().textContent).toContain("WorkItemServer unreachable"));
    expect(onClose).not.toHaveBeenCalled();

    await click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    // One work item, saved twice: the second press carries the form values onto
    // the id the first press created.
    expect(create).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][0]).toBe("wi-new");
    expect(transition).not.toHaveBeenCalled();
    // Exactly one copy of each file on the item.
    expect(upload.mock.calls.map((c) => (c[1] as File).name)).toEqual(["a.png", "b.pdf", "b.pdf"]);
  });
});

describe("staged files belong to one work item and one act", () => {
  test("cancelling an edit discards the files staged in it", async () => {
    mockServices();
    const item = makeWorkItem();
    const upload = vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([]);
    await renderDialog(item);
    await openEdit();
    await waitForLimits();

    await pick(fileOfSize("discarded.png", 10, "image/png"));
    await click(screen.getByRole("button", { name: "Cancel" }));
    await openEdit();

    expect(form().textContent).not.toContain("discarded.png");
    expect(upload).not.toHaveBeenCalled();
  });

  test("files staged on an item are not carried into the create form", async () => {
    mockServices();
    const item = makeWorkItem();
    const { rerender } = await renderDialog(item);
    await openEdit();
    await waitForLimits();
    await pick(fileOfSize("staged-on-wi-1.png", 10, "image/png"));
    expect(form().textContent).toContain("staged-on-wi-1.png");

    await act(async () => {
      rerender(
        <MemoryRouter>
          <WorkItemModalV2 workItem={null} onClose={vi.fn()} onSave={vi.fn()} />
        </MemoryRouter>,
      );
      await Promise.resolve();
    });

    expect(screen.getByText("New Work Item")).toBeTruthy();
    expect(form().textContent).not.toContain("staged-on-wi-1.png");
  });
});
