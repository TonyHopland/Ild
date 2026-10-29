import { useRef, useState } from "react";
import type { NetworkMode } from "../../../types";

export function describeError(err: unknown, fallback: string): string {
  if (err && typeof err === "object" && "message" in err) {
    const message = (err as { message?: unknown }).message;
    if (typeof message === "string" && message) return message;
  }
  return fallback;
}

/** Whether a rule of this kind is in effect; before the mode has loaded, none is marked as ignored. */
export function listInEffect(mode: NetworkMode | null, kind: "whitelist" | "blacklist"): boolean {
  return (mode !== "whitelist" && mode !== "blacklist") || mode === kind;
}

/**
 * One request a form sends at a time, with what went wrong. Enter reaches the
 * form past a disabled button, and twice over reaches it while the first request
 * is still out, so the guard lives with the request rather than on the control.
 */
export function useFormRequest(failure: string) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const sending = useRef(false);

  const send = async (request: () => Promise<void>): Promise<boolean> => {
    if (sending.current) return false;
    sending.current = true;
    setBusy(true);
    setError(null);
    try {
      await request();
      return true;
    } catch (err) {
      setError(describeError(err, failure));
      return false;
    } finally {
      sending.current = false;
      setBusy(false);
    }
  };

  return { busy, error, setError, send };
}

/**
 * A table row that can turn into its own edit form. The draft lives in the row,
 * keyed by the item, so a refresh of the list changes the saved values without
 * touching what the user is typing.
 */
export function useRowEdit(failure: string) {
  const [editing, setEditing] = useState(false);
  const { busy, error, setError, send } = useFormRequest(failure);

  const start = (loadDraft: () => void) => {
    loadDraft();
    setError(null);
    setEditing(true);
  };

  const save = async (request: () => Promise<void>) => {
    if (await send(request)) setEditing(false);
  };

  return { editing, busy, error, start, save, cancel: () => setEditing(false) };
}

/** Save and Cancel for a row being edited; `subject` names the row, e.g. "postgres forward". */
export function EditActions({
  subject,
  busy,
  canSave,
  onSave,
  onCancel,
}: {
  subject: string;
  busy: boolean;
  canSave: boolean;
  onSave: () => void;
  onCancel: () => void;
}) {
  return (
    <>
      <button
        type="button"
        className="btn btn-primary"
        onClick={onSave}
        disabled={busy || !canSave}
        aria-label={`Save the ${subject}`}
      >
        Save
      </button>
      <button
        type="button"
        className="btn"
        onClick={onCancel}
        disabled={busy}
        aria-label={`Cancel editing the ${subject}`}
      >
        Cancel
      </button>
    </>
  );
}
