import { describe, expect, test } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";
import { isChatEnabled, setChatEnabled, useChatEnabled } from "./useChatEnabled";
import { PreferenceKeys, getPreference, seedPreferences } from "../utils/userPreferences";

describe("useChatEnabled", () => {
  test("defaults to enabled when the user has not chosen", () => {
    expect(isChatEnabled()).toBe(true);
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(true);
  });

  test("reads the user's disabled preference", () => {
    seedPreferences({ [PreferenceKeys.ChatEnabled]: "false" });
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(false);
  });

  test("setChatEnabled keeps the choice as a preference and updates a live hook", () => {
    const { result } = renderHook(() => useChatEnabled());
    expect(result.current).toBe(true);

    act(() => setChatEnabled(false));
    expect(getPreference(PreferenceKeys.ChatEnabled)).toBe("false");
    expect(result.current).toBe(false);

    act(() => setChatEnabled(true));
    expect(getPreference(PreferenceKeys.ChatEnabled)).toBe("true");
    expect(result.current).toBe(true);
  });
});
