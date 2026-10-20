import {
  PreferenceKeys,
  getPreference,
  setPreference,
  usePreference,
} from "../utils/userPreferences";

/**
 * Whether the floating chat bubble is drawn at all: one of the user's
 * preferences, on unless they turned it off.
 */
export function isChatEnabled(): boolean {
  return getPreference(PreferenceKeys.ChatEnabled) !== "false";
}

export function setChatEnabled(enabled: boolean): void {
  setPreference(PreferenceKeys.ChatEnabled, enabled ? "true" : "false");
}

export function useChatEnabled(): boolean {
  return usePreference(PreferenceKeys.ChatEnabled) !== "false";
}
