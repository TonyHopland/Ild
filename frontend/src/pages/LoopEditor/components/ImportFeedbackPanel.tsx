import type { ImportFeedbackItem } from "../types";

export interface ImportFeedbackPanelProps {
  /** One line per import result; the panel is absent while there are none. */
  items: ImportFeedbackItem[];
  onDismiss: () => void;
}

/** The Import Results panel. */
export default function ImportFeedbackPanel({ items, onDismiss }: ImportFeedbackPanelProps) {
  if (items.length === 0) return null;

  return (
    <div className="import-feedback-panel">
      <div className="import-feedback-header">
        <span>Import Results</span>
        <button className="import-feedback-close" onClick={onDismiss}>
          ✕
        </button>
      </div>
      <div className="import-feedback-list">
        {items.map((item, index) => (
          <div key={index} className={`import-feedback-item import-feedback-${item.status}`}>
            <span className="import-feedback-status-icon">
              {item.status === "success" ? "✓" : item.status === "error" ? "✗" : "−"}
            </span>
            <span className="import-feedback-filename">{item.filename}</span>
            <span className="import-feedback-message">{item.message}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
