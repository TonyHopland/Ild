import { useEffect, useMemo } from "react";
import { computeLineDiff } from "../../../utils/jsonDiff";

export interface SaveDiffModalProps {
  isOpen: boolean;
  /** The last-saved loop as pretty-printed ild-loop-template/v1 JSON ("" for a new template). */
  beforeJson: string;
  /** The currently-edited loop as pretty-printed ild-loop-template/v1 JSON. */
  afterJson: string;
  isSaving: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

/**
 * Save-time review gate (ADR-0011): before a human Save persists, show the raw
 * whole-document JSON diff of last-saved vs currently-edited loop — covering
 * every pending change (AI edits and manual canvas edits alike, no
 * edit-provenance tracking) — and require an explicit confirm.
 *
 * The repo has no diff library and the files-tab DiffView consumes a
 * precomputed unified-diff string, so we render our own line diff here. Prompt
 * changes therefore show as escaped JSON in this view — an accepted tradeoff for
 * a whole-document review.
 */
export default function SaveDiffModal({
  isOpen,
  beforeJson,
  afterJson,
  isSaving,
  onConfirm,
  onCancel,
}: SaveDiffModalProps) {
  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !isSaving) onCancel();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [isOpen, isSaving, onCancel]);

  const diff = useMemo(() => computeLineDiff(beforeJson, afterJson), [beforeJson, afterJson]);
  const changed = useMemo(() => diff.some((line) => line.type !== "context"), [diff]);

  if (!isOpen) return null;

  return (
    <div className="modal-overlay" onMouseDown={() => !isSaving && onCancel()}>
      <div
        className="modal-content save-diff-modal"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label="Review changes before saving"
      >
        <div className="modal-header">
          <h2>Review changes</h2>
        </div>
        <div className="modal-body">
          {changed ? (
            <div className="save-diff-view" data-testid="save-diff-view">
              {diff.map((line, idx) => (
                <div key={idx} className={`save-diff-line save-diff-${line.type}`}>
                  <span className="save-diff-gutter">
                    {line.type === "add" ? "+" : line.type === "del" ? "-" : " "}
                  </span>
                  <span className="save-diff-text">{line.text}</span>
                </div>
              ))}
            </div>
          ) : (
            <p data-testid="save-diff-empty">
              No changes to save — the loop matches the last saved version.
            </p>
          )}
        </div>
        <div className="modal-footer">
          <button
            type="button"
            className="btn btn-secondary"
            onClick={onCancel}
            disabled={isSaving}
          >
            Cancel
          </button>
          <button type="button" className="btn btn-primary" onClick={onConfirm} disabled={isSaving}>
            {isSaving ? "Saving…" : "Save changes"}
          </button>
        </div>
      </div>
    </div>
  );
}
