import { describe, expect, test } from "vite-plus/test";
import { act, renderHook } from "@testing-library/react";
import { WorkItemStatus } from "../types";
import { clearPreferences, getPreference, seedPreferences } from "../utils/userPreferences";
import { foldedColumnKey, useFoldedColumns } from "./useFoldedColumns";

describe("useFoldedColumns", () => {
  test("is not loaded until the preferences are, then holds the user's choices", () => {
    clearPreferences();
    const { result } = renderHook(() => useFoldedColumns());
    expect(result.current.loaded).toBe(false);

    act(() =>
      seedPreferences({
        [foldedColumnKey(WorkItemStatus.Done)]: "false",
        [foldedColumnKey(WorkItemStatus.Backlog)]: "true",
      }),
    );
    expect(result.current.loaded).toBe(true);
    expect(result.current.choices).toEqual({ Done: false, Backlog: true });
  });

  test("reads only a true or a false as a choice", () => {
    seedPreferences({
      [foldedColumnKey(WorkItemStatus.Done)]: "true",
      [foldedColumnKey(WorkItemStatus.Ready)]: "yes",
    });
    expect(renderHook(() => useFoldedColumns()).result.current.choices).toEqual({ Done: true });
  });

  test("a fold writes its own column alone, so another column's choice made elsewhere is never sent back", () => {
    seedPreferences({ [foldedColumnKey(WorkItemStatus.Backlog)]: "true" });
    const { result } = renderHook(() => useFoldedColumns());

    act(() => result.current.setFolded(WorkItemStatus.Done, false));

    expect(result.current.choices).toEqual({ Backlog: true, Done: false });
    expect(getPreference(foldedColumnKey(WorkItemStatus.Done))).toBe("false");
    expect(getPreference(foldedColumnKey(WorkItemStatus.Backlog))).toBe("true");
  });
});
