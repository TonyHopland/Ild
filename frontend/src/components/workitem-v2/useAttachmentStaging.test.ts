import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";
import { useAttachmentStaging } from "./useAttachmentStaging";
import { settingsService, workItemService } from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function stubLimits() {
  vi.spyOn(settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * 1024 * 1024,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * 1024 * 1024,
  });
}

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
    stubLimits();
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result, rerender } = renderHook(({ id }: { id: string }) => useAttachmentStaging(id), {
      initialProps: { id: "wi-1" },
    });
    await waitFor(() => expect(result.current.limits).not.toBeNull());
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

describe("staging before the limits have arrived", () => {
  test("a file the limits turn out to rule out is dropped when they land", async () => {
    const limits = deferred<{
      maxBytesPerFile: number;
      maxFilesPerRequest: number;
      maxTotalBytesPerWorkItem: number;
    }>();
    vi.spyOn(settingsService, "getAttachmentLimits").mockReturnValue(limits.promise);
    const upload = vi.spyOn(workItemService, "uploadAttachment").mockResolvedValue([]);

    const { result } = renderHook(() => useAttachmentStaging("wi-1"));
    act(() => result.current.add([fileOfSize("big.bin", 30 * 1024 * 1024)]));
    act(() => result.current.add([fileOfSize("small.txt", 12, "text/plain")]));
    expect(result.current.staged.map((entry) => entry.file.name)).toEqual(["big.bin", "small.txt"]);

    await act(async () => {
      limits.resolve({
        maxBytesPerFile: 25 * 1024 * 1024,
        maxFilesPerRequest: 10,
        maxTotalBytesPerWorkItem: 250 * 1024 * 1024,
      });
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
    stubLimits();
    const first = deferred<never[]>();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue([]);

    const { result } = renderHook(() => useAttachmentStaging("wi-1"));
    await waitFor(() => expect(result.current.limits).not.toBeNull());

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
    stubLimits();
    const upload = vi
      .spyOn(workItemService, "uploadAttachment")
      .mockRejectedValue({ message: "Attachments for this work item exceed the total allowed." });

    const { result } = renderHook(() => useAttachmentStaging("wi-1"));
    await waitFor(() => expect(result.current.limits).not.toBeNull());

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
