// @vitest-environment jsdom
import { describe, expect, test } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";
import {
  isSubmitDefaultOnEnter,
  setSubmitDefaultOnEnter,
  useSubmitDefaultOnEnter,
} from "./useSubmitDefaultOnEnter";

import { PreferenceKeys, getPreference, seedPreferences } from "../utils/userPreferences";

const KEY = PreferenceKeys.SubmitDefaultOnEnter;

describe("useSubmitDefaultOnEnter", () => {
  test("is off when the user has not chosen", () => {
    expect(isSubmitDefaultOnEnter()).toBe(false);
    const { result } = renderHook(() => useSubmitDefaultOnEnter());
    expect(result.current).toBe(false);
  });

  test.each([
    ["true", true],
    ["false", false],
    ["", false],
    ["TRUE", false],
    ["1", false],
    ["yes", false],
  ])("a stored %j counts as on only when it is exactly true", (stored, on) => {
    seedPreferences({ [KEY]: stored });
    expect(isSubmitDefaultOnEnter()).toBe(on);
    const { result } = renderHook(() => useSubmitDefaultOnEnter());
    expect(result.current).toBe(on);
  });

  test("setting it keeps true or false as a preference and updates a live hook", () => {
    const { result } = renderHook(() => useSubmitDefaultOnEnter());

    act(() => setSubmitDefaultOnEnter(true));
    expect(getPreference(KEY)).toBe("true");
    expect(result.current).toBe(true);

    act(() => setSubmitDefaultOnEnter(false));
    expect(getPreference(KEY)).toBe("false");
    expect(result.current).toBe(false);
  });
});
