import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { repositoryService } from "../services/auth";
import { PREVIEW_ENV_LOAD_ERROR, useStoredPreviewEnv } from "./useStoredPreviewEnv";

afterEach(() => {
  vi.restoreAllMocks();
});

function mockRead(impl: (id: string) => Promise<string>) {
  return vi.spyOn(repositoryService, "getPreviewEnv").mockImplementation(impl);
}

describe("useStoredPreviewEnv", () => {
  test("fetches the stored text once the editor is enabled, not before", async () => {
    const read = mockRead(() => Promise.resolve("STORED=1"));

    const { result, rerender } = renderHook(
      ({ enabled }) => useStoredPreviewEnv("repo-1", enabled),
      { initialProps: { enabled: false } },
    );
    expect(read).not.toHaveBeenCalled();

    rerender({ enabled: true });
    await waitFor(() => {
      expect(result.current.value).toBe("STORED=1");
    });
    expect(read).toHaveBeenCalledWith("repo-1");
    expect(result.current.dirty).toBe(false);
    expect(result.current.pendingWrite).toBeNull();
  });

  test("a different repository drops the previous plaintext and re-fetches", async () => {
    const read = mockRead((id) => Promise.resolve(id === "repo-1" ? "FIRST=1" : "SECOND=2"));

    const { result, rerender } = renderHook(({ id }) => useStoredPreviewEnv(id), {
      initialProps: { id: "repo-1" },
    });
    await waitFor(() => {
      expect(result.current.value).toBe("FIRST=1");
    });

    rerender({ id: "repo-2" });
    // Never the previous repository's secret, not even for a frame.
    expect(result.current.value).toBe("");
    await waitFor(() => {
      expect(result.current.value).toBe("SECOND=2");
    });
    expect(read).toHaveBeenCalledTimes(2);
  });

  test("a response that arrives after the repository changed is discarded", async () => {
    let releaseFirst: (text: string) => void = () => {};
    mockRead((id) =>
      id === "repo-1"
        ? new Promise<string>((resolve) => {
            releaseFirst = resolve;
          })
        : Promise.resolve("SECOND=2"),
    );

    const { result, rerender } = renderHook(({ id }) => useStoredPreviewEnv(id), {
      initialProps: { id: "repo-1" },
    });
    rerender({ id: "repo-2" });
    await waitFor(() => {
      expect(result.current.value).toBe("SECOND=2");
    });

    await act(async () => {
      releaseFirst("FIRST=1");
      await Promise.resolve();
    });
    expect(result.current.value).toBe("SECOND=2");
  });

  test("a failed read surfaces an error and can never be read as a removal", async () => {
    mockRead(() => Promise.reject(new Error("boom")));

    const { result } = renderHook(() => useStoredPreviewEnv("repo-1"));
    await waitFor(() => {
      expect(result.current.loadError).toBe(PREVIEW_ENV_LOAD_ERROR);
    });

    // An unknown baseline: a blank field is untouched, not a removal…
    expect(result.current.value).toBe("");
    expect(result.current.dirty).toBe(false);
    expect(result.current.removing).toBe(false);
    expect(result.current.pendingWrite).toBeNull();

    // …and typing a replacement still saves.
    act(() => result.current.setValue("NEW=1"));
    expect(result.current.removing).toBe(false);
    expect(result.current.pendingWrite).toEqual({ previewEnv: "NEW=1" });
  });

  test("emptying a prefilled field is a removal, and committing it clears the stored value", async () => {
    mockRead(() => Promise.resolve("STORED=1"));
    const clear = vi
      .spyOn(repositoryService, "clearPreviewEnv")
      .mockResolvedValue({} as unknown as never);

    const { result } = renderHook(() => useStoredPreviewEnv("repo-1"));
    await waitFor(() => {
      expect(result.current.value).toBe("STORED=1");
    });

    act(() => result.current.setValue("  "));
    expect(result.current.dirty).toBe(true);
    expect(result.current.removing).toBe(true);
    // The removal cannot ride in a repository payload — the update endpoint reads a
    // blank .env as "keep what is stored".
    expect(result.current.pendingWrite).toBeNull();

    await act(async () => {
      await result.current.commit();
    });
    expect(clear).toHaveBeenCalledWith("repo-1");
    expect(result.current.value).toBe("");
    expect(result.current.dirty).toBe(false);
  });

  test("committing a written value re-baselines it, so a second save sends nothing", async () => {
    mockRead(() => Promise.resolve("STORED=1"));
    const clear = vi.spyOn(repositoryService, "clearPreviewEnv");

    const { result } = renderHook(() => useStoredPreviewEnv("repo-1"));
    await waitFor(() => {
      expect(result.current.value).toBe("STORED=1");
    });

    act(() => result.current.setValue("STORED=2"));
    expect(result.current.pendingWrite).toEqual({ previewEnv: "STORED=2" });

    await act(async () => {
      await result.current.commit();
    });
    expect(clear).not.toHaveBeenCalled();
    expect(result.current.dirty).toBe(false);
    expect(result.current.pendingWrite).toBeNull();
  });
});
