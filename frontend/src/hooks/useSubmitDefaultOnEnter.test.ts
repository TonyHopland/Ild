import { afterEach, describe, expect, test } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";
import {
  isSubmitDefaultOnEnter,
  setSubmitDefaultOnEnter,
  useSubmitDefaultOnEnter,
} from "./useSubmitDefaultOnEnter";

const KEY = "ild_submit_default_on_enter";

afterEach(() => {
  localStorage.clear();
});

describe("useSubmitDefaultOnEnter", () => {
  test("is off when nothing is stored", () => {
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
    localStorage.setItem(KEY, stored);
    expect(isSubmitDefaultOnEnter()).toBe(on);
    const { result } = renderHook(() => useSubmitDefaultOnEnter());
    expect(result.current).toBe(on);
  });

  test("setting it stores true or false and updates a live hook in the same tab", () => {
    const { result } = renderHook(() => useSubmitDefaultOnEnter());

    act(() => setSubmitDefaultOnEnter(true));
    expect(localStorage.getItem(KEY)).toBe("true");
    expect(result.current).toBe(true);

    act(() => setSubmitDefaultOnEnter(false));
    expect(localStorage.getItem(KEY)).toBe("false");
    expect(result.current).toBe(false);
  });

  test("follows a change made in another tab", () => {
    const { result } = renderHook(() => useSubmitDefaultOnEnter());

    act(() => {
      localStorage.setItem(KEY, "true");
      window.dispatchEvent(new Event("storage"));
    });
    expect(result.current).toBe(true);

    act(() => {
      localStorage.removeItem(KEY);
      window.dispatchEvent(new Event("storage"));
    });
    expect(result.current).toBe(false);
  });
});
