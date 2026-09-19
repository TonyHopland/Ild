import { useState } from "react";
import { WorkItem, WorkItemAttachment } from "../../types";
import { workItemService } from "../../services/auth";
import { formatBytes } from "../../utils/attachments";

interface AttachmentListProps {
  workItem: WorkItem;
  /**
   * A file has left the work item: the list needs rereading so the removal shows
   * without a page reload, and anything else holding this attachment — the
   * staging rows, the note an answer is about to carry — has to let go of it.
   */
  onRemoved: (attachmentId: string) => void;
}

/**
 * The files on a work item, from the metadata the item already carries. The
 * bytes are fetched through the API client and saved under the attachment's own
 * name: the token travels in a header, so a bare href would fetch them signed
 * out, and a blob URL opened in a tab would run on ILD's own origin.
 */
export default function AttachmentList({ workItem, onRemoved }: AttachmentListProps) {
  // Each row answers for itself. The rows act independently — a download on one
  // while another is being removed — so a single "busy" or a single error would
  // let one row's request re-enable another's controls and wipe its message.
  const [busy, setBusy] = useState<ReadonlySet<string>>(() => new Set());
  const [errors, setErrors] = useState<ReadonlyMap<string, string>>(() => new Map());

  const attachments = workItem.attachments ?? [];

  const act = async (
    attachment: WorkItemAttachment,
    fallback: string,
    action: () => Promise<void>,
  ) => {
    setBusy((prev) => new Set(prev).add(attachment.id));
    setErrors((prev) => {
      const next = new Map(prev);
      next.delete(attachment.id);
      return next;
    });
    try {
      await action();
    } catch (e) {
      const message = (e as { message?: string })?.message ?? fallback;
      setErrors((prev) => new Map(prev).set(attachment.id, message));
    } finally {
      setBusy((prev) => {
        const next = new Set(prev);
        next.delete(attachment.id);
        return next;
      });
    }
  };

  const download = (attachment: WorkItemAttachment) =>
    act(attachment, `Failed to download ${attachment.fileName}.`, async () => {
      const blob = await workItemService.downloadAttachment(workItem.id, attachment.id);
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = attachment.fileName;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);
    });

  const remove = (attachment: WorkItemAttachment) =>
    act(attachment, `Failed to remove ${attachment.fileName}.`, async () => {
      await workItemService.deleteAttachment(workItem.id, attachment.id);
      onRemoved(attachment.id);
    });

  if (attachments.length === 0) {
    return <span className="detail-value">No attachments.</span>;
  }

  return (
    <ul className="wiv2-attach-list">
      {attachments.map((attachment) => (
        <li key={attachment.id} className="wiv2-attach-list-entry">
          <span className="wiv2-attach-name">{attachment.fileName}</span>
          <span className="wiv2-attach-size">{formatBytes(attachment.sizeBytes)}</span>
          <button
            type="button"
            className="btn btn-sm btn-secondary"
            aria-label={`Download ${attachment.fileName}`}
            onClick={() => void download(attachment)}
            disabled={busy.has(attachment.id)}
          >
            Download
          </button>
          <button
            type="button"
            className="wiv2-attach-remove"
            aria-label={`Remove attachment ${attachment.fileName}`}
            onClick={() => void remove(attachment)}
            disabled={busy.has(attachment.id)}
          >
            ×
          </button>
          {errors.get(attachment.id) && (
            <span className="preview-message preview-error wiv2-attach-row-error">
              {errors.get(attachment.id)}
            </span>
          )}
        </li>
      ))}
    </ul>
  );
}
