import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";
import { useAttachmentStaging } from "./useAttachmentStaging";
import { AttachmentLimits } from "../../types";
import { workItemService } from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

/** What the dialog read once and hands to every staging list in it. */
const LIMITS: AttachmentLimits = {
  maxBytesPerFile: 25 * 1024 * 1024,
  maxFilesPerRequest: 10,
  maxTotalBytesPerWorkItem: 250 * 1024 * 1024,
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

/** A file of an arbitrary reported size, without holding that many bytes. */
function fileOfSize(name: string, bytes: number, type = "application/octet-stream"): File {
  const file = new File(["x"], name, { type });
  Object.defineProperty(file, "size", { value: bytes });
  return file;
}

describe("the work item a staged file belongs to", () => {
  test("a batch stops when the dialog moves on, and never sends the next item's file", async () => {
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result, rerender } = renderHook(
      ({ id }: { id: string }) => useAttachmentStaging(id, LIMITS),
      {
        initialProps: { id: "wi-1" },
      },
    );
    act(() => result.current.add([new File(["a"], "first.png", { type: "image/png" })]));

    let running!: ReturnType<typeof result.current.uploadAll>;
    await act(async () => {
      running = result.current.uploadAll("wi-1");
      await Promise.resolve();
    });
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));

    // The human opens another work item and stages a file for it while the
    // first item's batch is still out.
    await act(async () => {
      rerender({ id: "wi-2" });
      await Promise.resolve();
    });
    expect(result.current.staged).toEqual([]);
    act(() => result.current.add([new File(["b"], "second.png", { type: "image/png" })]));

    let outcome!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    await act(async () => {
      first.resolve([]);
      outcome = await running;
    });

    expect(upload).toHaveBeenCalledTimes(1);
    expect(upload.mock.calls.map((call) => (call[1] as File).name)).toEqual(["first.png"]);
    expect(outcome.abandoned).toBe(true);
    expect(outcome.ok).toBe(false);
    expect(result.current.staged.map((entry) => entry.file.name)).toEqual(["second.png"]);
  });
});

describe("one batch at a time owns the staging list", () => {
  test("a second upload joins the batch already running instead of sending again", async () => {
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result } = renderHook(() => useAttachmentStaging("wi-1", LIMITS));
    act(() => result.current.add([new File(["a"], "a.png", { type: "image/png" })]));

    let running!: ReturnType<typeof result.current.uploadAll>;
    await act(async () => {
      running = result.current.uploadAll("wi-1");
      await Promise.resolve();
    });
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));

    // The edit form saves while the answer's batch is still out.
    let joinedOutcome!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    let outcome!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    await act(async () => {
      const joined = result.current.uploadAll("wi-1");
      first.resolve([]);
      [outcome, joinedOutcome] = await Promise.all([running, joined]);
    });

    expect(upload).toHaveBeenCalledTimes(1);
    expect(joinedOutcome).toBe(outcome);
    expect(outcome.ok).toBe(true);
    expect(result.current.uploading).toBe(false);
  });

  test("a save for another work item waits for the running batch, it does not take its outcome", async () => {
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result, rerender } = renderHook(
      ({ id }: { id: string }) => useAttachmentStaging(id, LIMITS),
      {
        initialProps: { id: "wi-a" },
      },
    );
    act(() => result.current.add([new File(["a"], "for-a.png", { type: "image/png" })]));

    let forA!: ReturnType<typeof result.current.uploadAll>;
    await act(async () => {
      forA = result.current.uploadAll("wi-a");
      await Promise.resolve();
    });
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));

    // The dialog moves to another work item and that item's save starts while
    // the first batch is still settling.
    await act(async () => {
      rerender({ id: "wi-b" });
      await Promise.resolve();
    });
    act(() => result.current.add([new File(["b"], "for-b.png", { type: "image/png" })]));

    let forB!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    let outcomeA!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    await act(async () => {
      const queued = result.current.uploadAll("wi-b");
      first.resolve([]);
      [outcomeA, forB] = await Promise.all([forA, queued]);
    });

    // B's save must do B's work, not inherit the abandoned batch A left behind.
    expect(outcomeA.abandoned).toBe(true);
    expect(forB.abandoned).toBe(false);
    expect(forB.ok).toBe(true);
    expect(forB.storedNames).toEqual(["for-b.png"]);
    expect(upload.mock.calls.map((call) => [call[0], (call[1] as File).name])).toEqual([
      ["wi-a", "for-a.png"],
      ["wi-b", "for-b.png"],
    ]);
  });

  test("emptying the list under a batch abandons it rather than reporting success", async () => {
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result } = renderHook(() => useAttachmentStaging("wi-1", LIMITS));
    act(() =>
      result.current.add([
        new File(["a"], "a.png", { type: "image/png" }),
        new File(["b"], "b.pdf", { type: "application/pdf" }),
      ]),
    );

    let running!: ReturnType<typeof result.current.uploadAll>;
    await act(async () => {
      running = result.current.uploadAll("wi-1");
      await Promise.resolve();
    });
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));

    // Cancelling an edit wipes the list while b.pdf has not been sent yet.
    let outcome!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    await act(async () => {
      result.current.clear();
      first.resolve([]);
      outcome = await running;
    });

    // Reporting ok here would answer the run naming none of the files, having
    // sent only some of them.
    expect(outcome.abandoned).toBe(true);
    expect(outcome.ok).toBe(false);
    expect(upload).toHaveBeenCalledTimes(1);
  });
});

describe("staging before the limits have arrived", () => {
  test("a file the limits turn out to rule out is dropped when they land", async () => {
    const upload = vi.spyOn(workItemService, "uploadAttachment").mockResolvedValue([]);

    // The dialog reads the limits once and hands them down; until that read
    // answers, the list is holding files nothing has weighed.
    const { result, rerender } = renderHook(
      ({ limits }: { limits: AttachmentLimits | null }) => useAttachmentStaging("wi-1", limits),
      { initialProps: { limits: null as AttachmentLimits | null } },
    );
    act(() => result.current.add([fileOfSize("big.bin", 30 * 1024 * 1024)]));
    act(() => result.current.add([fileOfSize("small.txt", 12, "text/plain")]));
    expect(result.current.staged.map((entry) => entry.file.name)).toEqual(["big.bin", "small.txt"]);

    await act(async () => {
      rerender({ limits: LIMITS });
      await Promise.resolve();
    });

    // The oversize file never becomes a request: it leaves the staging list the
    // moment there is a limit to weigh it against.
    expect(result.current.staged.map((entry) => entry.file.name)).toEqual(["small.txt"]);
    expect(result.current.stagingError).toBe(
      "'big.bin' is larger than the 25 MB allowed per file.",
    );
    await act(async () => {
      await result.current.uploadAll("wi-1");
    });
    expect(upload.mock.calls.map((call) => (call[1] as File).name)).toEqual(["small.txt"]);
  });
});

describe("useAttachmentStaging", () => {
  test("a file staged while the batch is in flight is uploaded by that same save", async () => {
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result } = renderHook(() => useAttachmentStaging("wi-1", LIMITS));

    act(() => result.current.add([new File(["a"], "a.png", { type: "image/png" })]));

    let outcome!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    const running = act(async () => {
      outcome = await result.current.uploadAll("wi-1");
    });

    // The human drops a second file while the first one is still going up.
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
    act(() => result.current.add([new File(["b"], "b.pdf", { type: "application/pdf" })]));
    first.resolve([]);
    await running;

    expect(upload.mock.calls.map((call) => (call[1] as File).name)).toEqual(["a.png", "b.pdf"]);
    expect(outcome.ok).toBe(true);
    expect(outcome.storedNames).toEqual(["a.png", "b.pdf"]);
    expect(outcome.errors).toEqual([]);
  });

  test("a file the server refuses is attempted once per save", async () => {
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockRejectedValue({ message: "Attachments for this work item exceed the total allowed." });

    const { result } = renderHook(() => useAttachmentStaging("wi-1", LIMITS));

    act(() => result.current.add([new File(["a"], "a.png", { type: "image/png" })]));

    let outcome!: Awaited<ReturnType<typeof result.current.uploadAll>>;
    await act(async () => {
      outcome = await result.current.uploadAll("wi-1");
    });

    expect(upload).toHaveBeenCalledTimes(1);
    expect(outcome.ok).toBe(false);
    expect(outcome.errors).toEqual(["Attachments for this work item exceed the total allowed."]);
    expect(result.current.staged[0].status).toBe("pending");
  });
});
