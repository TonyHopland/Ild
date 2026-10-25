import { useEffect } from "react";

export interface ExportGlobalVariablesModalProps {
  /** Stored names of the used global variables that exist on this instance. */
  names: string[];
  onInclude: () => void;
  onExcludeVariables: () => void;
  onCancel: () => void;
}

/** Asks whether an export should carry the global variables (with their values) the loop uses. */
export default function ExportGlobalVariablesModal({
  names,
  onInclude,
  onExcludeVariables,
  onCancel,
}: ExportGlobalVariablesModalProps) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onCancel();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel]);

  return (
    <div className="modal-overlay" onMouseDown={onCancel}>
      <div
        className="modal-content"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label="Include global variables"
      >
        <div className="modal-header">
          <h2>Include global variables?</h2>
        </div>
        <div className="modal-body">
          <p>
            This loop uses these global variables. Including them writes their current values, in
            plain text, into the exported file.
          </p>
          <ul>
            {names.map((name) => (
              <li key={name}>{name}</li>
            ))}
          </ul>
        </div>
        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn btn-secondary" onClick={onExcludeVariables}>
            Export without
          </button>
          <button type="button" className="btn btn-primary" onClick={onInclude}>
            Include variables
          </button>
        </div>
      </div>
    </div>
  );
}
