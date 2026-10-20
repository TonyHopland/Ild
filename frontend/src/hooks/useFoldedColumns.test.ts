import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, renderHook, waitFor } from "@testing-library/react";
import * as authServices from "../services/auth";
import { WorkItemStatus } from "../types";
import { FOLDED_COLUMNS_PREFERENCE, useFoldedColumns } from "./useFoldedColumns";

afterEach(() => {
  vi.restoreAllMocks();
});

describe("useFoldedColumns", () => {
  test("is not loaded until the preferences answer, then holds the user's choices", async () => {
    let answer!: (all: Record<string, string>) => void;
    vi.spyOn(authServices.preferencesService, "getAll").mockReturnValue(
      new Promise((resolve) => (answer = resolve)),
    );
    const { result } = renderHook(() => useFoldedColumns());
    expect(result.current.loaded).toBe(false);

    await act(async () => {
      answer({ [FOLDED_COLUMNS_PREFERENCE]: JSON.stringify({ Done: false, Backlog: true }) });
    });

    expect(result.current.loaded).toBe(true);
    expect(result.current.choices).toEqual({ Done: false, Backlog: true });
  });

  test("keeps only real statuses with a true or false, and reads a broken value as no choices", async () => {
    const getAll = vi.spyOn(authServices.preferencesService, "getAll");
    getAll.mockResolvedValue({
      [FOLDED_COLUMNS_PREFERENCE]: JSON.stringify({ Done: true, Nope: true, Ready: "yes" }),
    });
    const first = renderHook(() => useFoldedColumns());
    await waitFor(() => expect(first.result.current.loaded).toBe(true));
    expect(first.result.current.choices).toEqual({ Done: true });

    getAll.mockResolvedValue({ [FOLDED_COLUMNS_PREFERENCE]: "{not json" });
    const second = renderHook(() => useFoldedColumns());
    await waitFor(() => expect(second.result.current.loaded).toBe(true));
    expect(second.result.current.choices).toEqual({});
  });

  test("a read that fails still loads, leaving every column to its own rule", async () => {
    vi.spyOn(authServices.preferencesService, "getAll").mockRejectedValue(new Error("offline"));
    const { result } = renderHook(() => useFoldedColumns());

    await waitFor(() => expect(result.current.loaded).toBe(true));
    expect(result.current.choices).toEqual({});
  });

  test("saves every choice made, one at a time and in order, each carrying the earlier ones", async () => {
    vi.spyOn(authServices.preferencesService, "getAll").mockResolvedValue({
      [FOLDED_COLUMNS_PREFERENCE]: JSON.stringify({ Backlog: true }),
    });
    const sent: string[] = [];
    let release!: () => void;
    const put = vi
      .spyOn(authServices.preferencesService, "put")
      .mockImplementationOnce(async (_key, value) => {
        sent.push(value);
        await new Promise<void>((resolve) => (release = resolve));
      })
      .mockImplementation(async (_key, value) => {
        sent.push(value);
      });
    const { result } = renderHook(() => useFoldedColumns());
    await waitFor(() => expect(result.current.loaded).toBe(true));

    act(() => {
      result.current.setFolded(WorkItemStatus.Done, false);
      result.current.setFolded(WorkItemStatus.Done, true);
    });
    expect(result.current.choices).toEqual({ Backlog: true, Done: true });

    // The second save waits for the first, so the last choice is the one kept.
    await waitFor(() => expect(sent).toHaveLength(1));
    await act(async () => release());
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(put).toHaveBeenCalledWith(FOLDED_COLUMNS_PREFERENCE, expect.any(String));
    expect(sent.map((v) => JSON.parse(v))).toEqual([
      { Backlog: true, Done: false },
      { Backlog: true, Done: true },
    ]);
  });
});
