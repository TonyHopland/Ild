import { FormEvent, useLayoutEffect, useRef, useState } from "react";
import { ApiError, GlobalVariable } from "../../types";
import { globalVariableService } from "../../services/auth";

interface VariableDialogProps {
  /** The variable to edit, or null for a new one. */
  variable: GlobalVariable | null;
  /** The most characters a value may have; the server refuses a longer one. */
  limit: number;
  onClose: () => void;
  /** Reads the list again; called after every save attempt, since a refused one may mean the list is stale. */
  reload: () => Promise<void>;
}

/**
 * Creates a global variable or edits the value of one. Mounted per opening, so
 * every draft starts from the variable it was opened for.
 */
export default function VariableDialog({ variable, limit, onClose, reload }: VariableDialogProps) {
  const [name, setName] = useState(variable?.name ?? "");
  const [value, setValue] = useState(variable?.value ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const dialogRef = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    return () => {
      queueMicrotask(() => {
        if (opener?.isConnected) opener.focus();
      });
    };
  }, []);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      if (variable) await globalVariableService.update(variable.id, value);
      else await globalVariableService.create({ name, value });
      await reload();
      onClose();
    } catch (err) {
      setError((err as ApiError | Error).message);
      await reload();
    } finally {
      setSaving(false);
    }
  };

  const dismiss = () => {
    if (!saving) onClose();
  };

  const keepTabInside = (e: React.KeyboardEvent) => {
    const focusable = Array.from(
      dialogRef.current?.querySelectorAll<HTMLElement>(
        "button:not([disabled]), input:not([disabled]), textarea:not([disabled])",
      ) ?? [],
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  };

  const overLimit = value.length > limit;
  return (
    <div className="modal-overlay" onMouseDown={dismiss}>
      <div
        ref={dialogRef}
        className="modal-content"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="gvDialogTitle"
        onKeyDown={(e) => {
          if (e.key === "Tab") {
            keepTabInside(e);
            return;
          }
          if (e.key !== "Escape") return;
          e.stopPropagation();
          dismiss();
        }}
      >
        <div className="modal-header">
          <h2 id="gvDialogTitle">{variable ? "Edit Global Variable" : "New Global Variable"}</h2>
          <button
            type="button"
            className="btn-close"
            aria-label="Close"
            disabled={saving}
            onClick={dismiss}
          >
            &times;
          </button>
        </div>
        <form onSubmit={(e) => void handleSubmit(e)}>
          <div className="modal-body">
            <div className="form-group">
              <label htmlFor="gvName">Name</label>
              <input
                id="gvName"
                autoFocus={variable === null}
                type="text"
                value={name}
                readOnly={variable !== null}
                onChange={(e) => setName(e.target.value)}
                required
              />
              <span className="form-hint">
                {variable
                  ? "A name cannot change. Prompts use it as {{Global." + variable.name + "}}."
                  : "A letter, then letters, digits or underscores. Prompts use it as {{Global.<name>}}."}
              </span>
            </div>
            <div className="form-group">
              <label htmlFor="gvValue">Value</label>
              <textarea
                id="gvValue"
                autoFocus={variable !== null}
                className="gv-value-input"
                rows={10}
                value={value}
                aria-describedby="gvValueCount"
                onChange={(e) => setValue(e.target.value)}
              />
              <span id="gvValueCount" className={`form-hint${overLimit ? " is-problem" : ""}`}>
                Length: {value.length} / {limit} characters
                {overLimit && " — over the limit"}
              </span>
            </div>
            {error && (
              <span className="gv-error" role="alert">
                {error}
              </span>
            )}
          </div>
          <div className="modal-footer">
            <button type="button" className="btn btn-secondary" disabled={saving} onClick={dismiss}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={saving}>
              {variable ? "Save" : "Create"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
