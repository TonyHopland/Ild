import { describe, expect, test } from "vite-plus/test";
import { act, renderHook } from "@testing-library/react";
import { WorkItemStatus } from "../types";
import {
  PreferenceKeys,
  clearPreferences,
  getPreference,
  seedPreferences,
} from "../utils/userPreferences";
import { useFoldedColumns } from "./useFoldedColumns";

describe("useFoldedColumns", () => {
  test("is not loaded until the preferences are, then holds the user's choices", () => {
    clearPreferences();
    const { result } = renderHook(() => useFoldedColumns());
    expect(result.current.loaded).toBe(false);

    act(() =>
      seedPreferences({
        [PreferenceKeys.TaskboardFolded]: JSON.stringify({ Done: false, Backlog: true }),
      }),
    );
    expect(result.current.loaded).toBe(true);
    expect(result.current.choices).toEqual({ Done: false, Backlog: true });
  });

  test("keeps only real statuses with a true or false, and reads a broken value as no choices", () => {
    seedPreferences({
      [PreferenceKeys.TaskboardFolded]: JSON.stringify({ Done: true, Nope: true, Ready: "yes" }),
    });
    expect(renderHook(() => useFoldedColumns()).result.current.choices).toEqual({ Done: true });

    seedPreferences({ [PreferenceKeys.TaskboardFolded]: "{not json" });
    expect(renderHook(() => useFoldedColumns()).result.current.choices).toEqual({});
  });

  test("a choice is kept with the earlier ones", () => {
    seedPreferences({ [PreferenceKeys.TaskboardFolded]: JSON.stringify({ Backlog: true }) });
    const { result } = renderHook(() => useFoldedColumns());

    act(() => result.current.setFolded(WorkItemStatus.Done, false));

    expect(result.current.choices).toEqual({ Backlog: true, Done: false });
    expect(JSON.parse(getPreference(PreferenceKeys.TaskboardFolded)!)).toEqual({
      Backlog: true,
      Done: false,
    });
  });
});
