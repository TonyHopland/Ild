import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, renderHook } from "@testing-library/react";
import * as authServices from "../../services/auth";
import {
  PreferenceKeys,
  clearPreferences,
  getPreference,
  loadPreferences,
  preferencesLoaded,
  setPreference,
  usePreference,
  usePreferencesLoaded,
} from "../userPreferences";

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe("user preferences", () => {
  test("are not loaded until the server answers, and then hold what it holds", async () => {
    clearPreferences();
    let answer!: (all: Record<string, string>) => void;
    vi.spyOn(authServices.preferencesService, "getAll").mockReturnValue(
      new Promise((resolve) => (answer = resolve)),
    );
    const loaded = renderHook(() => usePreferencesLoaded());
    const chat = renderHook(() => usePreference(PreferenceKeys.ChatEnabled));

    const loading = loadPreferences();
    expect(loaded.result.current).toBe(false);
    await act(async () => {
      answer({ [PreferenceKeys.ChatEnabled]: "false" });
      await loading;
    });

    expect(loaded.result.current).toBe(true);
    expect(chat.result.current).toBe("false");
  });

  test("a read that fails still loads, with every setting at its default", async () => {
    clearPreferences();
    vi.spyOn(authServices.preferencesService, "getAll").mockRejectedValue(new Error("offline"));
    vi.spyOn(console, "error").mockImplementation(() => {});

    await loadPreferences();

    expect(preferencesLoaded()).toBe(true);
    expect(getPreference(PreferenceKeys.ChatEnabled)).toBeUndefined();
  });

  test("saves each change, one at a time per key and in order, and skips a value it already has", async () => {
    clearPreferences();
    vi.spyOn(authServices.preferencesService, "getAll").mockResolvedValue({});
    await loadPreferences();
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

    setPreference(PreferenceKeys.ChatEnabled, "false");
    setPreference(PreferenceKeys.ChatEnabled, "true");
    setPreference(PreferenceKeys.ChatEnabled, "true");
    expect(getPreference(PreferenceKeys.ChatEnabled)).toBe("true");

    await vi.waitFor(() => expect(sent).toEqual(["false"]));
    release();
    await vi.waitFor(() => expect(sent).toEqual(["false", "true"]));
    expect(put).toHaveBeenCalledTimes(2);
  });

  test("carries a setting kept in this browser over once, unless the server already has one, and removes it here", async () => {
    clearPreferences();
    localStorage.setItem("ild_chat_enabled", "false");
    localStorage.setItem("ild_notifications_enabled", "false");
    localStorage.setItem("auth_token", "kept");
    vi.spyOn(authServices.preferencesService, "getAll").mockResolvedValue({
      [PreferenceKeys.NotificationsEnabled]: "true",
    });
    const put = vi.spyOn(authServices.preferencesService, "put").mockResolvedValue();

    await loadPreferences();

    expect(getPreference(PreferenceKeys.ChatEnabled)).toBe("false");
    expect(getPreference(PreferenceKeys.NotificationsEnabled)).toBe("true");
    await vi.waitFor(() => expect(put).toHaveBeenCalledWith(PreferenceKeys.ChatEnabled, "false"));
    expect(put).toHaveBeenCalledTimes(1);
    expect(localStorage.getItem("ild_chat_enabled")).toBeNull();
    expect(localStorage.getItem("ild_notifications_enabled")).toBeNull();
    // Only settings move: the sign-in itself stays in the browser.
    expect(localStorage.getItem("auth_token")).toBe("kept");
  });

  test("sign-out forgets them, and a load still out for the earlier sign-in installs nothing", async () => {
    clearPreferences();
    let answer!: (all: Record<string, string>) => void;
    vi.spyOn(authServices.preferencesService, "getAll").mockReturnValue(
      new Promise((resolve) => (answer = resolve)),
    );
    const loading = loadPreferences();

    clearPreferences();
    answer({ [PreferenceKeys.ChatEnabled]: "false" });
    await loading;

    expect(preferencesLoaded()).toBe(false);
    expect(getPreference(PreferenceKeys.ChatEnabled)).toBeUndefined();
  });
});
