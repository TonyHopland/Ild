import { useCallback, useMemo } from "react";
import { WorkItemStatus } from "../types";
import {
  PreferenceKeys,
  getPreference,
  setPreference,
  usePreference,
  usePreferencesLoaded,
} from "../utils/userPreferences";

export type FoldChoices = Partial<Record<WorkItemStatus, boolean>>;

const STATUSES = new Set<string>(Object.values(WorkItemStatus));

function parse(stored: string | undefined): FoldChoices {
  if (!stored) return {};
  try {
    const raw = JSON.parse(stored) as Record<string, unknown>;
    const choices: FoldChoices = {};
    for (const [status, folded] of Object.entries(raw)) {
      if (STATUSES.has(status) && typeof folded === "boolean")
        choices[status as WorkItemStatus] = folded;
    }
    return choices;
  } catch {
    return {};
  }
}

/**
 * Which taskboard columns the user has folded or opened, one of their
 * preferences, so the board looks the same on every device. A column with no
 * choice yet folds by its own rule. `loaded` is false until the preferences
 * have been read, so the board does not draw folds it may have to undo.
 */
export function useFoldedColumns() {
  const stored = usePreference(PreferenceKeys.TaskboardFolded);
  const loaded = usePreferencesLoaded();
  const choices = useMemo(() => parse(stored), [stored]);

  const setFolded = useCallback((status: WorkItemStatus, folded: boolean) => {
    const current = parse(getPreference(PreferenceKeys.TaskboardFolded));
    setPreference(PreferenceKeys.TaskboardFolded, JSON.stringify({ ...current, [status]: folded }));
  }, []);

  return { choices, loaded, setFolded };
}
