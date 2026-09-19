import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import Taskboard from "./index";
import { WorkItem, WorkItemAttachment, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeItem(id: string, title: string, overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id,
    title,
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

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function mockServices(items: WorkItem[]) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue(items);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
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
  vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
    key: "scheduler.isPaused",
    value: "false",
  });
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
}

function renderBoard(openItemId: string) {
  render(
    <MemoryRouter initialEntries={[`/taskboard/${openItemId}`]}>
      <Routes>
        <Route path="/taskboard" element={<Taskboard />} />
        <Route path="/taskboard/:workItemId" element={<Taskboard />} />
      </Routes>
    </MemoryRouter>,
  );
}

const dialog = () => screen.getByRole("dialog");
const openTitle = () => within(dialog()).getByRole("heading", { level: 2 }).textContent;

/**
 * A node that appears in a render later than the one already waited for. The
 * assertion is what makes this wait: waitFor retries only while its callback
 * throws, so a callback that merely returns the query's result settles on the
 * first try, null and all.
 */
async function waitForNode<T extends Element>(find: () => T | null): Promise<T> {
  return await waitFor(() => {
    const node = find();
    expect(node).not.toBeNull();
    return node as T;
  });
}

/**
 * A dialog resets its own view state — edit mode, the open tab — in the effect
 * that runs when it mounts. Letting that effect run before driving the dialog
 * keeps it from landing on top of the first click.
 */
async function dialogSettled(title: string) {
  await waitFor(() => expect(openTitle()).toBe(title));
  await act(async () => {
    await Promise.resolve();
  });
}

/**
 * A control inside whichever dialog is open. Waiting for it rather than reading
 * it once covers the beat in which a switch has torn one dialog down and not yet
 * put the next one up.
 */
async function dialogButton(name: string) {
  return await waitFor(() => within(dialog()).getByRole("button", { name }));
}

async function openCard(title: string) {
  const card = await waitForNode(() =>
    document.querySelector<HTMLElement>(`.work-item-card[aria-label^="${title},"]`),
  );
  await act(async () => {
    fireEvent.click(card);
    await Promise.resolve();
  });
  await dialogSettled(title);
}

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

async function stageInEditForm(fileName: string) {
  await click(await dialogButton("Edit"));
  const form = await waitForNode(() => dialog().querySelector("form"));
  await act(async () => {
    fireEvent.change(form.querySelector('input[type="file"]') as HTMLInputElement, {
      target: { files: [new File(["x"], fileName, { type: "text/plain" })] },
    });
    await Promise.resolve();
  });
}

describe("a dialog belongs to the work item it was opened for", () => {
  test("opening others while an upload is in flight sends each file to its own item", async () => {
    const items = [
      makeItem("wi-a", "Item A"),
      makeItem("wi-b", "Item B"),
      makeItem("wi-c", "Item C"),
    ];
    mockServices(items);
    const slowUpload = deferred<WorkItemAttachment[]>();
    const upload = vi
      .spyOn(authServices.workItemService, "uploadAttachment")
      .mockImplementation((id: string) =>
        id === "wi-a" ? slowUpload.promise : Promise.resolve([]),
      );
    vi.spyOn(authServices.workItemService, "update").mockImplementation((id: string) =>
      Promise.resolve(items.find((item) => item.id === id) as WorkItem),
    );
    vi.spyOn(authServices.workItemService, "getById").mockImplementation((id: string) =>
      Promise.resolve(items.find((item) => item.id === id) as WorkItem),
    );

    renderBoard("wi-a");
    await dialogSettled("Item A");

    await stageInEditForm("a.txt");
    await click(await dialogButton("Update"));
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));

    // Item A's upload is still out while the human moves on, stages on B and
    // leaves without saving, then stages on C and saves.
    await openCard("Item B");
    await stageInEditForm("b.txt");
    await openCard("Item C");
    await stageInEditForm("c.txt");
    await click(await dialogButton("Update"));

    await waitFor(() => expect(upload).toHaveBeenCalledTimes(2));
    await act(async () => {
      slowUpload.resolve([]);
      await Promise.resolve();
    });

    // Each file went to the item whose form staged it; the file staged on B and
    // left behind went nowhere, and nothing of C's reached B.
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(2));
    expect(upload.mock.calls.map((call) => [call[0], (call[1] as File).name])).toEqual([
      ["wi-a", "a.txt"],
      ["wi-c", "c.txt"],
    ]);
  });

  test("an answer still being composed cannot reach the item opened next", async () => {
    const parked = makeItem("wi-a", "Item A", {
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      currentLoopRunId: "run-1",
    });
    // Item B waits on a human too, so it has a feedback pane of its own — the
    // place another item's refusal would surface if the two shared one dialog.
    const items = [
      parked,
      makeItem("wi-b", "Item B", {
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
        currentLoopRunId: "run-2",
      }),
    ];
    mockServices(items);
    vi.spyOn(authServices.loopRunService, "getById").mockRejectedValue({ status: 404 });
    vi.spyOn(authServices.workItemService, "uploadAttachment").mockResolvedValue([
      { id: "att-1", fileName: "a.txt", contentType: "text/plain", sizeBytes: 1 },
    ]);
    // The answer is refused, so the dialog that sent it has something to report
    // — which must be reported by that dialog, not by whatever is open later.
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockRejectedValue({ status: 400, message: "Input must be 8192 characters or fewer." });
    // The read that reconciles the note against the item, held open.
    const reconcile = deferred<WorkItem>();
    vi.spyOn(authServices.workItemService, "getById").mockReturnValue(reconcile.promise);

    renderBoard("wi-a");
    await dialogSettled("Item A");
    const feedback = await waitForNode(() => dialog().querySelector<HTMLElement>(".wiv2-feedback"));
    await waitFor(() => expect(feedback.textContent).toContain("25 MB"));

    await act(async () => {
      fireEvent.change(feedback.querySelector('input[type="file"]') as HTMLInputElement, {
        target: { files: [new File(["x"], "a.txt", { type: "text/plain" })] },
      });
      fireEvent.change(feedback.querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Looks good" },
      });
      await Promise.resolve();
    });
    await click(within(feedback).getByRole("button", { name: "Approve" }));

    // The human opens another item while the note is still being reconciled.
    await openCard("Item B");
    await act(async () => {
      reconcile.resolve(parked);
      await Promise.resolve();
    });

    // Item A's answer was sent for item A and refused there. Item B's dialog is
    // its own: it carries no staged file, says nothing about the other item's
    // refusal, and was never answered in its place.
    await waitFor(() => expect(openTitle()).toBe("Item B"));
    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer.mock.calls[0][0]).toBe("wi-a");
    expect(dialog().querySelectorAll(".wiv2-attach-row")).toHaveLength(0);
    expect(dialog().textContent).not.toContain("Looks good");
    expect(dialog().textContent).not.toContain("Input must be 8192 characters or fewer.");
  });
});
