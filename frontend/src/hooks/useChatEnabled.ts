import { useEffect, useState } from "react";

// Whether the floating chat bubble is rendered at all. Stored per-browser in
// localStorage (mirroring `ild_notifications_enabled`) and defaulting to on.
export const CHAT_ENABLED_KEY = "ild_chat_enabled";

// Fired on the same tab when the preference changes, since the native `storage`
// event only reaches *other* tabs. Lets the bubble react without a reload.
export const CHAT_ENABLED_EVENT = "ild-chat-enabled-changed";

export function isChatEnabled(): boolean {
  return localStorage.getItem(CHAT_ENABLED_KEY) !== "false";
}

export function setChatEnabled(enabled: boolean): void {
  localStorage.setItem(CHAT_ENABLED_KEY, enabled ? "true" : "false");
  window.dispatchEvent(new Event(CHAT_ENABLED_EVENT));
}

export function useChatEnabled(): boolean {
  const [enabled, setEnabled] = useState(isChatEnabled);

  useEffect(() => {
    const sync = () => setEnabled(isChatEnabled());
    window.addEventListener(CHAT_ENABLED_EVENT, sync);
    window.addEventListener("storage", sync);
    return () => {
      window.removeEventListener(CHAT_ENABLED_EVENT, sync);
      window.removeEventListener("storage", sync);
    };
  }, []);

  return enabled;
}
