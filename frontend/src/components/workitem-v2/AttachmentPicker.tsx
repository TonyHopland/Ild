import { formatBytes } from "../../utils/attachments";
import type { AttachmentStaging } from "./useAttachmentStaging";

interface AttachmentPickerProps {
  staging: AttachmentStaging;
  /** Ties the control's label to its input. */
  inputId: string;
}

/**
 * Stages files for the work item being created or edited: pick them, drop them
 * on the area, or paste a screenshot anywhere in the surrounding form — the
 * paste handler belongs to that form, since the textarea it is pasted into sits
 * outside this control. Nothing here uploads; the save does.
 */
export default function AttachmentPicker({ staging, inputId }: AttachmentPickerProps) {
  const { staged, limits, stagingError, uploading } = staging;

  return (
    <div className="wiv2-attach">
      <div
        className="wiv2-attach-drop"
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.preventDefault();
          staging.add(e.dataTransfer?.files);
        }}
      >
        <label htmlFor={inputId} className="wiv2-attach-label">
          Attachments
        </label>
        <input
          id={inputId}
          type="file"
          multiple
          className="wiv2-attach-input"
          onChange={(e) => {
            staging.add(e.target.files);
            // Re-picking the same file has to stage it again, and no change
            // event fires while the input still holds it.
            e.target.value = "";
          }}
        />
        <small className="form-hint">
          Drop files here or paste a screenshot.
          {limits && ` Up to ${formatBytes(limits.maxBytesPerFile)} per file.`}
        </small>
      </div>
      {stagingError && (
        <div role="alert" className="form-error">
          {stagingError}
        </div>
      )}
      {staged.length > 0 && (
        <ul className="wiv2-attach-staged">
          {staged.map((entry) => (
            <li key={entry.key} className="wiv2-attach-row">
              <span className="wiv2-attach-name">{entry.file.name}</span>
              <span className="wiv2-attach-size">{formatBytes(entry.file.size)}</span>
              {/* An uploaded file is on the work item, where the overview's own
                  list removes it; this row only records that it landed. */}
              {entry.status === "uploaded" ? (
                <span className="wiv2-attach-done">Attached</span>
              ) : (
                <button
                  type="button"
                  className="wiv2-attach-remove"
                  aria-label={`Remove ${entry.file.name}`}
                  onClick={() => staging.remove(entry.key)}
                  disabled={uploading}
                >
                  ×
                </button>
              )}
              {entry.error && (
                <span className="preview-message preview-error wiv2-attach-row-error">
                  {entry.error}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
