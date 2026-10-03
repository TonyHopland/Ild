import { useEffect, useState } from "react";

// Whether Enter in a run's feedback box takes the parked node's default output.
// Stored per-browser in localStorage (mirroring `ild_chat_enabled`) and
// defaulting to off: only an exact "true" turns it on.
export const SUBMIT_DEFAULT_ON_ENTER_KEY = "ild_submit_default_on_enter";

// Fired on the same tab when the preference changes, since the native `storage`
// event only reaches *other* tabs. Lets an open feedback box react without a reload.
export const SUBMIT_DEFAULT_ON_ENTER_EVENT = "ild-submit-default-on-enter-changed";

export function isSubmitDefaultOnEnter(): boolean {
  return localStorage.getItem(SUBMIT_DEFAULT_ON_ENTER_KEY) === "true";
}

export function setSubmitDefaultOnEnter(enabled: boolean): void {
  localStorage.setItem(SUBMIT_DEFAULT_ON_ENTER_KEY, enabled ? "true" : "false");
  window.dispatchEvent(new Event(SUBMIT_DEFAULT_ON_ENTER_EVENT));
}

export function useSubmitDefaultOnEnter(): boolean {
  const [enabled, setEnabled] = useState(isSubmitDefaultOnEnter);

  useEffect(() => {
    const sync = () => setEnabled(isSubmitDefaultOnEnter());
    window.addEventListener(SUBMIT_DEFAULT_ON_ENTER_EVENT, sync);
    window.addEventListener("storage", sync);
    return () => {
      window.removeEventListener(SUBMIT_DEFAULT_ON_ENTER_EVENT, sync);
      window.removeEventListener("storage", sync);
    };
  }, []);

  return enabled;
}
