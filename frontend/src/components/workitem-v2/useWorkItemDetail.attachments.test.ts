import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";
import { useWorkItemDetail } from "./useWorkItemDetail";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import {
  repositoryService,
  loopTemplateService,
  workItemService,
  aiProviderService,
  settingsService,
} from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Running,
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
    worktreePath: null,
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

function stubServices(
  limits: { maxBytesPerFile: number } | "unavailable" = { maxBytesPerFile: 1000 },
) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn().mockResolvedValue(undefined),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
  if (limits === "unavailable") {
    vi.spyOn(settingsService, "getAttachmentLimits").mockRejectedValue({
      status: 503,
      message: "Settings unavailable",
    });
  } else {
    vi.spyOn(settingsService, "getAttachmentLimits").mockResolvedValue({
      maxBytesPerFile: limits.maxBytesPerFile,
      maxFilesPerRequest: 10,
      maxTotalBytesPerWorkItem: 10 * limits.maxBytesPerFile,
    });
  }
}

function renderDetail(workItem: WorkItem) {
  return renderHook(({ wi }: { wi: WorkItem | null }) => useWorkItemDetail(wi, vi.fn()), {
    initialProps: { wi: workItem as WorkItem | null },
  });
}

const stagedNames = (detail: { attachments: { staged: { file: File }[] } }) =>
  detail.attachments.staged.map((s) => s.file.name);

describe("staging files on a work item", () => {
  test("stages picked files and drops one again on request", async () => {
    stubServices();
    const { result } = renderDetail(makeWorkItem());
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());

    await act(async () => {
      result.current.attachments.add([fileOfSize("a.png", 10), fileOfSize("b.pdf", 20)]);
    });
    expect(stagedNames(result.current)).toEqual(["a.png", "b.pdf"]);

    await act(async () => {
      result.current.attachments.remove(result.current.attachments.staged[0].key);
    });
    expect(stagedNames(result.current)).toEqual(["b.pdf"]);
  });

  test("refuses a file over the configured maximum and never uploads it", async () => {
    stubServices({ maxBytesPerFile: 1000 });
    const upload = vi.spyOn(workItemService, "uploadAttachment").mockResolvedValue([]);
    const { result } = renderDetail(makeWorkItem());
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());

    await act(async () => {
      result.current.attachments.add([fileOfSize("big.bin", 1200), fileOfSize("small.txt", 10)]);
    });

    expect(stagedNames(result.current)).toEqual(["small.txt"]);
    expect(result.current.attachments.stagingError).toContain("big.bin");

    await act(async () => {
      await result.current.attachments.uploadAll("wi-1");
    });
    expect(upload).toHaveBeenCalledTimes(1);
    expect(upload.mock.calls[0][1].name).toBe("small.txt");
  });

  // Without the limits the client has nothing to enforce; inventing one would
  // refuse files an instance configured higher accepts, so the server stays the
  // only enforcer and staging carries on.
  test("stages anything when the limits cannot be read", async () => {
    stubServices("unavailable");
    const { result } = renderDetail(makeWorkItem());
    await waitFor(() => expect(settingsService.getAttachmentLimits).toHaveBeenCalled());

    await act(async () => {
      result.current.attachments.add([fileOfSize("huge.bin", 500 * 1024 * 1024)]);
    });

    expect(stagedNames(result.current)).toEqual(["huge.bin"]);
    expect(result.current.attachments.stagingError).toBeFalsy();
    expect(result.current.attachments.limits).toBeNull();
  });
});

describe("uploading a staged batch", () => {
  test("attempts every file even after one is refused", async () => {
    stubServices();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementation((_id: string, file: File) =>
        file.name === "b.pdf"
          ? Promise.reject({ status: 400, message: "This item is already full." })
          : Promise.resolve([]),
      );
    const { result } = renderDetail(makeWorkItem());
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());
    await act(async () => {
      result.current.attachments.add([
        fileOfSize("a.png", 10),
        fileOfSize("b.pdf", 10),
        fileOfSize("c.txt", 10),
      ]);
    });

    let outcome: { ok: boolean; storedNames: string[] } | undefined;
    await act(async () => {
      outcome = await result.current.attachments.uploadAll("wi-1");
    });

    expect(upload.mock.calls.map((c) => (c[1] as File).name)).toEqual(["a.png", "b.pdf", "c.txt"]);
    expect(outcome).toMatchObject({ ok: false, storedNames: ["a.png", "c.txt"] });
    expect(result.current.attachments.staged.map((s) => s.status)).toEqual([
      "uploaded",
      "pending",
      "uploaded",
    ]);
  });

  test("a retry re-sends only what did not land, and reports every file now stored", async () => {
    stubServices();
    let failFirstB = true;
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementation((_id: string, file: File) => {
        if (file.name === "b.pdf" && failFirstB) {
          failFirstB = false;
          return Promise.reject({ status: 503, message: "WorkItemServer unreachable" });
        }
        return Promise.resolve([]);
      });
    const { result } = renderDetail(makeWorkItem());
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());
    await act(async () => {
      result.current.attachments.add([fileOfSize("a.png", 10), fileOfSize("b.pdf", 10)]);
    });

    let first: { ok: boolean; storedNames: string[] } | undefined;
    await act(async () => {
      first = await result.current.attachments.uploadAll("wi-1");
    });
    expect(first).toMatchObject({ ok: false, storedNames: ["a.png"] });

    let second: { ok: boolean; storedNames: string[] } | undefined;
    await act(async () => {
      second = await result.current.attachments.uploadAll("wi-1");
    });

    // a.png landed the first time round, so the retry carries b.pdf alone —
    // leaving exactly one copy of each on the item.
    expect(upload.mock.calls.map((c) => (c[1] as File).name)).toEqual(["a.png", "b.pdf", "b.pdf"]);
    // Every file now stored, not just the one this attempt happened to send.
    expect(second).toMatchObject({ ok: true, storedNames: ["a.png", "b.pdf"] });
  });
});

describe("staged files belong to one work item", () => {
  test("switching to another work item clears the staged files and the staging error", async () => {
    stubServices({ maxBytesPerFile: 1000 });
    const { result, rerender } = renderDetail(makeWorkItem({ id: "wi-1" }));
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());
    await act(async () => {
      result.current.attachments.add([fileOfSize("a.png", 10), fileOfSize("big.bin", 1200)]);
    });
    expect(stagedNames(result.current)).toEqual(["a.png"]);
    expect(result.current.attachments.stagingError).toBeTruthy();

    await act(async () => {
      rerender({ wi: makeWorkItem({ id: "wi-2" }) });
    });

    expect(result.current.attachments.staged).toEqual([]);
    expect(result.current.attachments.stagingError).toBeFalsy();
  });

  test("staged files survive a status change on the same item", async () => {
    // Being answered is exactly what moves a parked item's status, so a status
    // change must not wipe the files staged to answer with.
    stubServices();
    const { result, rerender } = renderDetail(makeWorkItem({ id: "wi-1" }));
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());
    await act(async () => {
      result.current.attachments.add([fileOfSize("a.png", 10)]);
    });

    await act(async () => {
      rerender({
        wi: makeWorkItem({
          id: "wi-1",
          status: WorkItemStatus.HumanFeedback,
          humanFeedbackReason: "Human Input Needed",
        }),
      });
    });

    expect(stagedNames(result.current)).toEqual(["a.png"]);
  });

  test("clear() empties the staged list", async () => {
    stubServices();
    const { result } = renderDetail(makeWorkItem());
    await waitFor(() => expect(result.current.attachments.limits).not.toBeNull());
    await act(async () => {
      result.current.attachments.add([fileOfSize("a.png", 10)]);
    });

    await act(async () => {
      result.current.attachments.clear();
    });

    expect(result.current.attachments.staged).toEqual([]);
  });
});
