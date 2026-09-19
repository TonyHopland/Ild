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
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const attachments = workItem.attachments ?? [];

  const act = async (
    attachment: WorkItemAttachment,
    fallback: string,
    action: () => Promise<void>,
  ) => {
    setBusyId(attachment.id);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError((e as { message?: string })?.message ?? fallback);
    } finally {
      setBusyId(null);
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
    <>
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
              disabled={busyId === attachment.id}
            >
              Download
            </button>
            <button
              type="button"
              className="wiv2-attach-remove"
              aria-label={`Remove attachment ${attachment.fileName}`}
              onClick={() => void remove(attachment)}
              disabled={busyId === attachment.id}
            >
              ×
            </button>
          </li>
        ))}
      </ul>
      {error && <span className="preview-message preview-error">{error}</span>}
    </>
  );
}
