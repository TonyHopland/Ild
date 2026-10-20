import { useCallback, useMemo, useSyncExternalStore } from "react";
import { WorkItemStatus } from "../types";
import {
  getPreference,
  setPreference,
  subscribePreferences,
  usePreferencesLoaded,
} from "../utils/userPreferences";

export type FoldChoices = Partial<Record<WorkItemStatus, boolean>>;

/**
 * The preference holding whether the user folded (true) or opened (false) one
 * column. One key per column, so folding one writes that column alone: a tab or
 * device that loaded earlier cannot undo a fold made since in another column.
 */
export function foldedColumnKey(status: WorkItemStatus): string {
  return `taskboard.folded.${status.toLowerCase()}`;
}

const STATUSES = Object.values(WorkItemStatus);

function read(status: WorkItemStatus): boolean | undefined {
  const stored = getPreference(foldedColumnKey(status));
  return stored === "true" ? true : stored === "false" ? false : undefined;
}

// Every column's choice as one string, so the hook re-renders only when one changes.
const snapshot = () => STATUSES.map((s) => String(read(s) ?? "")).join(",");

function parse(snap: string): FoldChoices {
  const choices: FoldChoices = {};
  snap.split(",").forEach((folded, i) => {
    if (folded !== "") choices[STATUSES[i]] = folded === "true";
  });
  return choices;
}

/**
 * Which taskboard columns the user has folded or opened, kept among their
 * preferences so the board looks the same on every device. A column with no
 * choice yet folds by its own rule. `loaded` is false until the preferences
 * have been read, so the board does not draw folds it may have to undo.
 */
export function useFoldedColumns() {
  const loaded = usePreferencesLoaded();
  const snap = useSyncExternalStore(subscribePreferences, snapshot);
  const choices = useMemo(() => parse(snap), [snap]);

  const setFolded = useCallback((status: WorkItemStatus, folded: boolean) => {
    setPreference(foldedColumnKey(status), folded ? "true" : "false");
  }, []);

  return { choices, loaded, setFolded };
}
