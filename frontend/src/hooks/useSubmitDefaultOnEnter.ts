import {
  PreferenceKeys,
  getPreference,
  setPreference,
  usePreference,
} from "../utils/userPreferences";

/**
 * Whether Enter in a run's feedback box takes the parked node's default output:
 * one of the user's preferences, off unless they turned it on.
 */
export function isSubmitDefaultOnEnter(): boolean {
  return getPreference(PreferenceKeys.SubmitDefaultOnEnter) === "true";
}

export function setSubmitDefaultOnEnter(enabled: boolean): void {
  setPreference(PreferenceKeys.SubmitDefaultOnEnter, enabled ? "true" : "false");
}

export function useSubmitDefaultOnEnter(): boolean {
  return usePreference(PreferenceKeys.SubmitDefaultOnEnter) === "true";
}
