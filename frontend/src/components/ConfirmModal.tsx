import { useEffect, useRef, type CSSProperties } from "react";

interface ConfirmModalProps {
  isOpen: boolean;
  title: string;
  message: string;
  /** What the action will affect, listed under the message. */
  items?: string[];
  onConfirm: () => void;
  onCancel: () => void;
  /** Label for the confirm button. Defaults to "Delete". */
  confirmText?: string;
  /** Colour class for the confirm button. Defaults to "btn-danger". */
  confirmClassName?: string;
  /** Inline style for the confirm button, such as a colour of its own. */
  confirmStyle?: CSSProperties;
  /**
   * Focus the confirm button once the dialog opens, so Enter confirms; a
   * held-down Enter does not. Escape then cancels this dialog alone, before any
   * other listener sees it.
   */
  focusConfirm?: boolean;
}

export default function ConfirmModal({
  isOpen,
  title,
  message,
  onConfirm,
  onCancel,
  items = [],
  confirmText = "Delete",
  confirmClassName = "btn-danger",
  confirmStyle,
  focusConfirm = false,
}: ConfirmModalProps) {
  const confirmRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!isOpen) return;
    if (focusConfirm) {
      // Capturing on window runs before every document and React listener, so
      // a dialog this one opens over does not take the Escape as its own.
      const onKey = (e: KeyboardEvent) => {
        if (e.key !== "Escape") return;
        e.stopPropagation();
        e.preventDefault();
        onCancel();
      };
      window.addEventListener("keydown", onKey, true);
      return () => window.removeEventListener("keydown", onKey, true);
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onCancel();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [isOpen, onCancel, focusConfirm]);

  useEffect(() => {
    if (isOpen && focusConfirm) confirmRef.current?.focus();
  }, [isOpen, focusConfirm]);

  if (!isOpen) return null;

  return (
    <div className="modal-overlay" onMouseDown={onCancel}>
      <div
        className="modal-content confirm-modal-content"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label={title}
      >
        <div className="modal-header">
          <h2>{title}</h2>
        </div>
        <div className="modal-body">
          <p>{message}</p>
          {items.length > 0 && (
            <ul>
              {items.map((item, index) => (
                <li key={index}>{item}</li>
              ))}
            </ul>
          )}
        </div>
        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onCancel}>
            Cancel
          </button>
          <button
            ref={confirmRef}
            type="button"
            className={`btn ${confirmClassName}`}
            style={confirmStyle}
            onClick={onConfirm}
            onKeyDown={
              focusConfirm
                ? (e) => {
                    if (e.key !== "Enter") return;
                    e.preventDefault();
                    if (!e.repeat) onConfirm();
                  }
                : undefined
            }
          >
            {confirmText}
          </button>
        </div>
      </div>
    </div>
  );
}
