import { useEffect, useRef, useState } from "react";
import { repositoryService } from "../services/auth";

export const PREVIEW_ENV_LOAD_ERROR =
  "Couldn't load the stored custom .env. Leave the field blank to keep it, or type a new one to replace it.";

/**
 * A repository's custom .env in an editor: the stored plaintext, the text the user
 * is editing, and what a save of that edit means.
 *
 * The plaintext is the one secret the UI reads back, so it is fetched only while an
 * editor is actually open (`enabled`) and only for the repository being edited —
 * changing `repositoryId` drops the previous repository's text immediately and an
 * in-flight response for it is discarded, so one repository's .env can never be
 * shown, or saved, under another.
 *
 * Saving is split because the repository has one write endpoint for its whole row:
 * `pendingWrite` is the field the caller merges into its own create/update payload,
 * and `commit` is called once that write lands to apply what the payload could not
 * express — removing the .env, which the update endpoint reads as "keep what is
 * stored" — and to make the saved text the new baseline.
 */
export interface StoredPreviewEnv {
  /** The text being edited. Empty until the stored value arrives. */
  value: string;
  setValue: (value: string) => void;
  loading: boolean;
  /** A renderable message when the stored text could not be read, else null. */
  loadError: string | null;
  /** Whether the text differs from what is stored. */
  dirty: boolean;
  /** Whether saving this edit removes the stored .env rather than replacing it. */
  removing: boolean;
  /** The patch to merge into a repository create/update payload, or null when the payload must not carry the .env. */
  pendingWrite: { previewEnv: string } | null;
  commit: () => Promise<void>;
}

export function useStoredPreviewEnv(
  repositoryId: string | null,
  enabled: boolean = true,
): StoredPreviewEnv {
  const [value, setValue] = useState("");
  // The text as stored: null while unknown — before the fetch, or after one that
  // failed — which is what keeps a blank field from being read as a removal.
  const [baseline, setBaseline] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const loadedFor = useRef<string | null>(null);

  // Everything held here belongs to one repository. Drop it the moment that
  // changes, before the new value has arrived.
  useEffect(() => {
    loadedFor.current = null;
    setValue("");
    setBaseline(null);
    setLoadError(null);
    setLoading(false);
  }, [repositoryId]);

  useEffect(() => {
    if (!enabled || !repositoryId || loadedFor.current === repositoryId) return;
    loadedFor.current = repositoryId;
    let cancelled = false;
    let settled = false;
    setLoading(true);
    setLoadError(null);
    void (async () => {
      try {
        const text = await repositoryService.getPreviewEnv(repositoryId);
        if (!cancelled) {
          setBaseline(text);
          setValue(text);
        }
      } catch {
        if (!cancelled) setLoadError(PREVIEW_ENV_LOAD_ERROR);
      } finally {
        settled = true;
        if (!cancelled) setLoading(false);
      }
    })();
    // A request that never settled leaves nothing loaded, so let the next run
    // (a reopened editor, or React's remount in StrictMode) ask again.
    return () => {
      cancelled = true;
      if (!settled) loadedFor.current = null;
    };
  }, [repositoryId, enabled]);

  const dirty = value !== (baseline ?? "");
  const removing = dirty && baseline !== null && value.trim() === "";

  const commit = async () => {
    if (!dirty) return;
    if (removing) {
      if (!repositoryId) return;
      await repositoryService.clearPreviewEnv(repositoryId);
      setBaseline("");
      setValue("");
      return;
    }
    // The caller's payload carried the new text, so it is what is stored now.
    setBaseline(value);
  };

  return {
    value,
    setValue,
    loading,
    loadError,
    dirty,
    removing,
    pendingWrite: dirty && !removing ? { previewEnv: value } : null,
    commit,
  };
}
