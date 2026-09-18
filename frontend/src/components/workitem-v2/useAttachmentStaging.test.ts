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
