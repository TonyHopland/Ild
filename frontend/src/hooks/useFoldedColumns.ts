import { useCallback, useEffect, useRef, useState } from "react";
import { preferencesService } from "../services/auth";
import { WorkItemStatus } from "../types";

/** The preference holding which taskboard columns the user folded or opened. */
export const FOLDED_COLUMNS_PREFERENCE = "taskboard.folded";

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
 * Which taskboard columns the user has folded or opened, kept as one of their
 * preferences on the server so the board looks the same on every device. A
 * column with no choice yet folds by its own rule. `loaded` is false until the
 * server has answered, so the board does not draw folds it may have to undo; a
 * read that fails leaves every column to its rule. Choices are saved one at a
 * time, in the order they were made, so the last one made is the one kept.
 */
export function useFoldedColumns() {
  const [choices, setChoices] = useState<FoldChoices>({});
  const [loaded, setLoaded] = useState(false);
  const choicesRef = useRef<FoldChoices>({});
  const savingRef = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    let cancelled = false;
    void preferencesService
      .getAll()
      .then((all) => {
        if (cancelled) return;
        choicesRef.current = { ...parse(all[FOLDED_COLUMNS_PREFERENCE]), ...choicesRef.current };
        setChoices(choicesRef.current);
      })
      .catch(() => {})
      .finally(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const setFolded = useCallback((status: WorkItemStatus, folded: boolean) => {
    choicesRef.current = { ...choicesRef.current, [status]: folded };
    setChoices(choicesRef.current);
    const value = JSON.stringify(choicesRef.current);
    savingRef.current = savingRef.current
      .then(() => preferencesService.put(FOLDED_COLUMNS_PREFERENCE, value))
      // A save that fails keeps the choice on this page; the next one carries it.
      .catch((error: unknown) => console.error("Failed to save the folded columns:", error));
  }, []);

  return { choices, loaded, setFolded };
}
