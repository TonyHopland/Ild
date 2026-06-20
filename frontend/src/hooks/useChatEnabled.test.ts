import { afterEach, describe, expect, test } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";
import { CHAT_ENABLED_KEY, isChatEnabled, setChatEnabled, useChatEnabled } from "./useChatEnabled";

afterEach(() => {
  localStorage.clear();
});

describe("useChatEnabled", () => {
  test("defaults to enabled when nothing is stored", () => {
    expect(isChatEnabled()).toBe(true);
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(true);
  });

  test("reads a stored disabled preference on mount", () => {
    localStorage.setItem(CHAT_ENABLED_KEY, "false");
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(false);
  });

  test("setChatEnabled persists the value and updates a live hook", () => {
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(true);

    act(() => setChatEnabled(false));
    expect(localStorage.getItem(CHAT_ENABLED_KEY)).toBe("false");
    expect(result.current).toBe(false);

    act(() => setChatEnabled(true));
    expect(localStorage.getItem(CHAT_ENABLED_KEY)).toBe("true");
    expect(result.current).toBe(true);
  });

  test("reacts to the storage event from another tab", () => {
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(true);

    act(() => {
      localStorage.setItem(CHAT_ENABLED_KEY, "false");
      window.dispatchEvent(new Event("storage"));
    });
    expect(result.current).toBe(false);
  });
});
