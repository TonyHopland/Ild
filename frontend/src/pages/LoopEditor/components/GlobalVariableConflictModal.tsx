import { useEffect, useMemo } from "react";
import LineDiffView from "../../../components/LineDiffView";
import { computeLineDiff } from "../../../utils/jsonDiff";

export interface GlobalVariableConflictModalProps {
  name: string;
  /** The value the instance holds now. */
  current: string;
  /** The value the imported file carries. */
  imported: string;
  onOverwrite: () => void;
  onKeepCurrent: () => void;
}

/** Asks whether an imported global variable replaces the differing one already on this instance. */
export default function GlobalVariableConflictModal({
  name,
  current,
  imported,
  onOverwrite,
  onKeepCurrent,
}: GlobalVariableConflictModalProps) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onKeepCurrent();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onKeepCurrent]);

  const diff = useMemo(() => computeLineDiff(current, imported), [current, imported]);

  return (
    <div className="modal-overlay" onMouseDown={onKeepCurrent}>
      <div
        className="modal-content save-diff-modal"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label="Global variable conflict"
      >
        <div className="modal-header">
          <h2>Global variable "{name}" already exists</h2>
        </div>
        <div className="modal-body">
          <p>The imported value differs from the current one. Overwrite it?</p>
          <LineDiffView diff={diff} testId="global-variable-diff" />
        </div>
        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onKeepCurrent}>
            Keep current
          </button>
          <button type="button" className="btn btn-primary" onClick={onOverwrite}>
            Overwrite
          </button>
        </div>
      </div>
    </div>
  );
}
