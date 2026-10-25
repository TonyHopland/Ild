export interface ImportConflictModalProps {
  /** The name of the imported template that already exists. */
  name: string;
  onSkip: () => void;
  onUpdate: () => void;
}

/** Asks whether an imported template replaces the one of the same name or is skipped. */
export default function ImportConflictModal({ name, onSkip, onUpdate }: ImportConflictModalProps) {
  return (
    <div className="modal-overlay" onMouseDown={onSkip}>
      <div
        className="modal-content import-conflict-modal"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label="Import conflict"
      >
        <div className="modal-header">
          <h2>Template Already Exists</h2>
        </div>
        <div className="modal-body">
          <p>
            A template named <strong>"{name}"</strong> already exists.
          </p>
          <p>What would you like to do?</p>
        </div>
        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onSkip}>
            Skip
          </button>
          <button type="button" className="btn btn-primary" onClick={onUpdate}>
            Update
          </button>
        </div>
      </div>
    </div>
  );
}
