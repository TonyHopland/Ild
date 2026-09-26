import { useState } from "react";
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { WorkItem, WorkItemAttachment, WorkItemStatus, WorkItemPriority } from "../../types";
import { formatBytes } from "../../utils/attachments";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeAttachment(overrides: Partial<WorkItemAttachment> = {}): WorkItemAttachment {
  return {
    id: "att-1",
    fileName: "shot.png",
    contentType: "image/png",
    sizeBytes: 2048,
    createdAt: "2026-09-18T10:00:00Z",
    ...overrides,
  };
}

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
    attachments: [makeAttachment()],
    ...overrides,
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
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
}

/**
 * Mirrors the taskboard: the dialog's work item is state the parent replaces
 * whenever the dialog reports a saved item, so a removal that refetches the
 * item shows up in the open dialog.
 */
function Harness({ initial }: { initial: WorkItem }) {
  const [item, setItem] = useState<WorkItem>(initial);
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={item} onClose={vi.fn()} onSave={(wi) => setItem(wi)} />
    </MemoryRouter>
  );
}

async function renderDialog(workItem: WorkItem) {
  render(<Harness initial={workItem} />);
  await act(async () => {
    await Promise.resolve();
  });
}

const overview = () => document.getElementById("wiv2-panel-overview") as HTMLElement;

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
    await Promise.resolve();
  });
}

describe("the overview's attachment list", () => {
  test("lists every attachment the item carries, without a second fetch", async () => {
    mockServices();
    const fetchSpy = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(new Response("[]", { status: 200 }));
    await renderDialog(
      makeWorkItem({
        attachments: [
          makeAttachment(),
          makeAttachment({ id: "att-2", fileName: "notes.pdf", sizeBytes: 4096 }),
        ],
      }),
    );

    expect(overview().textContent).toContain("Attachments");
    expect(overview().textContent).toContain("shot.png");
    expect(overview().textContent).toContain(formatBytes(2048));
    expect(overview().textContent).toContain("notes.pdf");
    expect(overview().textContent).toContain(formatBytes(4096));
    expect(screen.getByRole("button", { name: "Download shot.png" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Remove attachment shot.png" })).toBeTruthy();

    // The metadata already rides on the work item, so opening the dialog asks
    // the attachment routes for nothing.
    const requested = fetchSpy.mock.calls.map(([input]) =>
      typeof input === "string" ? input : "",
    );
    expect(requested.filter((url) => url.includes("/attachments"))).toEqual([]);
  });

  test("says so when the item carries no attachment", async () => {
    mockServices();
    await renderDialog(makeWorkItem({ attachments: [] }));

    expect(overview().textContent).toMatch(/no attachments/i);
    expect(screen.queryByRole("button", { name: /^Download / })).toBeNull();
  });

  test("download saves the bytes under the attachment's own name", async () => {
    mockServices();
    const blob = new Blob(["PNGBYTES"], { type: "image/png" });
    const download = vi
      .spyOn(authServices.workItemService, "downloadAttachment")
      .mockResolvedValue(blob);
    // jsdom only turns its own Blob implementation into an object URL, so the
    // URL is stubbed: what is under test is that the saved link carries it.
    const objectUrl = "blob:http://localhost/attachment";
    const createObjectURL = vi.spyOn(URL, "createObjectURL").mockReturnValue(objectUrl);
    const revokeObjectURL = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    const clicks: { download: string; href: string }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicks.push({ download: this.download, href: this.href });
    });
    await renderDialog(makeWorkItem());

    await click(screen.getByRole("button", { name: "Download shot.png" }));

    await waitFor(() => expect(clicks).toHaveLength(1));
    // Fetched through the API client — which carries the token in a header —
    // and then saved, never opened in a tab.
    expect(download).toHaveBeenCalledWith("wi-1", "att-1");
    expect(createObjectURL).toHaveBeenCalledWith(blob);
    expect(clicks[0].download).toBe("shot.png");
    expect(clicks[0].href.startsWith("blob:")).toBe(true);
    expect(clicks[0].href).toBe(objectUrl);
    expect(revokeObjectURL).toHaveBeenCalledWith(objectUrl);
    expect(open).not.toHaveBeenCalled();
  });

  test("removing an attachment deletes it and the list stops showing it", async () => {
    mockServices();
    const remove = vi
      .spyOn(authServices.workItemService, "deleteAttachment")
      .mockResolvedValue(undefined);
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeWorkItem({ attachments: [] }),
    );
    await renderDialog(makeWorkItem());

    await click(screen.getByRole("button", { name: "Remove attachment shot.png" }));

    expect(remove).toHaveBeenCalledWith("wi-1", "att-1");
    await waitFor(() => expect(overview().textContent).not.toContain("shot.png"));
  });
});

describe("attachment failures are visible", () => {
  const FAILURE_CASES: Array<{
    name: string;
    service: "downloadAttachment" | "deleteAttachment";
    button: string;
  }> = [
    {
      name: "a download that fails shows the server's message",
      service: "downloadAttachment",
      button: "Download shot.png",
    },
    {
      name: "a removal that fails shows the server's message and keeps the attachment",
      service: "deleteAttachment",
      button: "Remove attachment shot.png",
    },
  ];

  test.each(FAILURE_CASES)("$name", async ({ service, button }) => {
    mockServices();
    vi.spyOn(authServices.workItemService, service).mockRejectedValue({
      status: 503,
      message: "WorkItemServer unreachable",
    });
    await renderDialog(makeWorkItem());

    await click(screen.getByRole("button", { name: button }));

    await waitFor(() => expect(overview().textContent).toContain("WorkItemServer unreachable"));
    expect(screen.getByRole("button", { name: "Download shot.png" })).toBeTruthy();
  });
});
