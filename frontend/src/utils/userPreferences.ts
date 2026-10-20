import { useSyncExternalStore } from "react";

/**
 * The signed-in user's preferences: the one place a UI setting is kept. They
 * live on the server (`/api/v1/preferences`), so they follow the user to every
 * device, and are read once after sign-in into this store, which every reader
 * shares. A setting is a text value under a key; each one's meaning and default
 * belong to the code that reads it.
 *
 * Until the server has answered, `loaded` is false, so nothing is drawn from a
 * default that the user's own choice would then replace. A read that fails
 * still loads, with no choices: every setting shows its default.
 */
export const PreferenceKeys = {
  NotificationsEnabled: "notifications.enabled",
  ChatEnabled: "chat.enabled",
  SubmitDefaultOnEnter: "feedback.submit-default-on-enter",
  TaskboardFolded: "taskboard.folded",
  ChatFabPosition: "chat.fab-position",
  ChatPanelSize: "chat.panel-size",
  ChatPanelPosition: "chat.panel-position",
  ChatSidebarOpen: "chat.sidebar-open",
} as const;

/**
 * Where these were kept in the browser before they moved to the server. Read
 * once after the first load: a value the server does not have yet is carried
 * over, and the browser's copy is then removed either way.
 */
const BROWSER_KEYS: Record<string, string> = {
  ild_notifications_enabled: PreferenceKeys.NotificationsEnabled,
  ild_chat_enabled: PreferenceKeys.ChatEnabled,
  ild_submit_default_on_enter: PreferenceKeys.SubmitDefaultOnEnter,
  ild_chat_fab_pos: PreferenceKeys.ChatFabPosition,
  ild_chat_panel_size: PreferenceKeys.ChatPanelSize,
  ild_chat_panel_pos: PreferenceKeys.ChatPanelPosition,
  ild_chat_sidebar_open: PreferenceKeys.ChatSidebarOpen,
};

// Loaded when first needed rather than imported, so a test setup file can seed
// this store without pulling in the API client a test file mocks.
const service = async () => (await import("../services/auth")).preferencesService;

interface State {
  loaded: boolean;
  values: Readonly<Record<string, string>>;
}

let state: State = { loaded: false, values: {} };
// Bumped by every reset, so a load that was in flight for an earlier sign-in
// installs nothing.
let generation = 0;
const listeners = new Set<() => void>();
// One save in flight per key at a time, sent in the order the changes were
// made, so the last value set is the one the server keeps.
const saving = new Map<string, Promise<void>>();
// Off while seeded by a test: values change here and nothing is sent.
let saves = true;

function publish(next: State) {
  state = next;
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function preferencesLoaded(): boolean {
  return state.loaded;
}

/** The user's value for a key, or undefined when they have not set one. */
export function getPreference(key: string): string | undefined {
  return state.values[key];
}

/** Sets one of the user's preferences here at once and saves it to the server. */
export function setPreference(key: string, value: string): void {
  if (state.values[key] === value) return;
  publish({ ...state, values: { ...state.values, [key]: value } });
  if (!saves) return;
  const previous = saving.get(key) ?? Promise.resolve();
  const next = previous
    .then(async () => (await service()).put(key, value))
    // A save that fails keeps the value on this page; the next change carries it.
    .catch((error: unknown) => console.error(`Failed to save the preference ${key}:`, error));
  saving.set(key, next);
}

/** Reads the user's preferences from the server: once after each sign-in. */
export async function loadPreferences(): Promise<void> {
  const mine = ++generation;
  saves = true;
  let stored: Record<string, string> = {};
  try {
    stored = await (await service()).getAll();
  } catch (error) {
    console.error("Failed to load preferences:", error);
  }
  if (mine !== generation) return;
  // A change made on this page while the read was out is newer than the read.
  publish({ loaded: true, values: { ...stored, ...state.values } });
  carryOverFromBrowser(stored);
}

function carryOverFromBrowser(stored: Record<string, string>) {
  for (const [browserKey, key] of Object.entries(BROWSER_KEYS)) {
    let local: string | null = null;
    try {
      local = localStorage.getItem(browserKey);
      localStorage.removeItem(browserKey);
    } catch {
      continue;
    }
    if (local !== null && stored[key] === undefined) setPreference(key, local);
  }
}

/** Forgets every preference, as on sign-out, so the next user starts from their own. */
export function clearPreferences(): void {
  generation++;
  saving.clear();
  publish({ loaded: false, values: {} });
}

/** For tests: start loaded from these values, with saving to the server off. */
export function seedPreferences(values: Record<string, string> = {}): void {
  generation++;
  saving.clear();
  saves = false;
  publish({ loaded: true, values: { ...values } });
}

/** The user's value for a key, kept live as it changes; undefined when unset. */
export function usePreference(key: string): string | undefined {
  return useSyncExternalStore(subscribe, () => state.values[key]);
}

/** Whether the user's preferences have been read since they signed in. */
export function usePreferencesLoaded(): boolean {
  return useSyncExternalStore(subscribe, () => state.loaded);
}
