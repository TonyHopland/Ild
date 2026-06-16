import { useEffect, useState } from "react";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import { workItemService } from "../../services/auth";
import { parseTags } from "../../utils/workItemJson";
import TagAutocomplete from "../TagAutocomplete";
import type { WorkItemDetail } from "./useWorkItemDetail";

interface EditPanelProps {
  /** The item being edited, or null to create a new one. */
  workItem: WorkItem | null;
  detail: WorkItemDetail;
  onSave: (workItem: WorkItem) => void;
  onDone: () => void;
  /** Reports whether any field differs from its initial value, so the dialog can
   *  guard close paths against discarding unsaved edits. */
  onDirtyChange?: (dirty: boolean) => void;
  /** Opens the dialog's delete confirmation — the delete control lives here in
   *  the edit view rather than the detail footer. Omitted in create mode, where
   *  there is no item to delete. */
  onRequestDelete?: () => void;
}

/**
 * Work item form for the V2 dialog — the same fields and save behaviour for
 * both editing an existing item and creating a new one (when workItem is null),
 * rendered inside the full-screen layout.
 */
export default function EditPanel({
  workItem,
  detail,
  onSave,
  onDone,
  onDirtyChange,
  onRequestDelete,
}: EditPanelProps) {
  // Baselines double as the create-form defaults and the dirty-check reference.
  const baseTitle = workItem?.title ?? "";
  const baseDescription = workItem?.description ?? "";
  const baseStatus = workItem?.status ?? WorkItemStatus.Backlog;
  const basePriority = workItem?.priority ?? WorkItemPriority.Medium;
  const baseTags = workItem ? parseTags(workItem).join(", ") : "";
  const baseRepositoryId = workItem?.repositoryId ?? "";

  const [title, setTitle] = useState(baseTitle);
  const [description, setDescription] = useState(baseDescription);
  const [status, setStatus] = useState<WorkItemStatus>(baseStatus);
  const [priority, setPriority] = useState<WorkItemPriority>(basePriority);
  const [tags, setTags] = useState(baseTags);
  const [repositoryId, setRepositoryId] = useState(baseRepositoryId);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  const dirty =
    title !== baseTitle ||
    description !== baseDescription ||
    status !== baseStatus ||
    priority !== basePriority ||
    tags !== baseTags ||
    repositoryId !== baseRepositoryId;

  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [dirty, onDirtyChange]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setSubmitError(null);

    const parsedTags = tags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    const data = {
      title,
      description,
      status,
      priority,
      tags: parsedTags,
      repositoryId,
    };

    try {
      let saved: WorkItem;
      if (workItem) {
        saved = await workItemService.update(workItem.id, data as Partial<WorkItem>);
        if (workItem.status !== status) {
          try {
            await workItemService.transition(workItem.id, status);
            saved = await workItemService.getById(workItem.id);
          } catch (err) {
            console.error("Failed to transition status:", err);
            setSubmitError(
              `Status transition failed: ${err instanceof Error ? err.message : "Unknown error"}`,
            );
          }
        }
      } else {
        saved = await workItemService.create(data as Partial<WorkItem>);
      }
      onSave(saved);
      onDone();
    } catch (error) {
      console.error("Failed to save work item:", error);
      setSubmitError(
        `Failed to save: ${(error as { message?: string })?.message ?? "Unknown error"}`,
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <form onSubmit={handleSubmit} className="wiv2-edit-form">
      <div className="form-group">
        <label htmlFor="wiv2-title">Title</label>
        <input
          id="wiv2-title"
          type="text"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          required
        />
      </div>
      <div className="form-group">
        <label htmlFor="wiv2-description">Description</label>
        <textarea
          id="wiv2-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={10}
        />
      </div>
      <div className="form-row">
        <div className="form-group">
          <label htmlFor="wiv2-repository">Repository</label>
          <select
            id="wiv2-repository"
            value={repositoryId}
            onChange={(e) => setRepositoryId(e.target.value)}
            required
          >
            <option value="">Select repository...</option>
            {detail.repositories.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
              </option>
            ))}
          </select>
        </div>
        <div className="form-group">
          <label htmlFor="wiv2-status">Status</label>
          <select
            id="wiv2-status"
            value={status}
            onChange={(e) => setStatus(e.target.value as WorkItemStatus)}
          >
            <option value={WorkItemStatus.Backlog}>Backlog</option>
            <option value={WorkItemStatus.WorkQueue}>Work Queue</option>
            <option value={WorkItemStatus.Ready}>Ready</option>
            <option value={WorkItemStatus.Running}>Running</option>
            <option value={WorkItemStatus.HumanFeedback}>Human Feedback</option>
            <option value={WorkItemStatus.Done}>Done</option>
          </select>
        </div>
        <div className="form-group">
          <label htmlFor="wiv2-priority">Priority</label>
          <select
            id="wiv2-priority"
            value={priority}
            onChange={(e) => setPriority(e.target.value as WorkItemPriority)}
          >
            <option value={WorkItemPriority.Low}>Low</option>
            <option value={WorkItemPriority.Medium}>Medium</option>
            <option value={WorkItemPriority.High}>High</option>
            <option value={WorkItemPriority.Critical}>Critical</option>
          </select>
        </div>
      </div>
      <div className="form-group">
        <label htmlFor="wiv2-tags">
          Tags (comma separated) — each tag must match a loop template name
        </label>
        <TagAutocomplete
          id="wiv2-tags"
          value={tags}
          onChange={setTags}
          options={detail.templates.map((t) => t.name)}
          placeholder="e.g. build, deploy"
        />
      </div>
      {submitError && (
        <div role="alert" className="form-error">
          {submitError}
        </div>
      )}
      <div className="wiv2-edit-actions">
        {workItem && onRequestDelete && (
          <button
            type="button"
            className="btn btn-sm btn-danger"
            onClick={onRequestDelete}
            disabled={submitting}
          >
            Delete
          </button>
        )}
        <span className="wiv2-edit-actions-spacer" />
        <button type="button" className="btn btn-secondary" onClick={onDone} disabled={submitting}>
          Cancel
        </button>
        <button type="submit" className="btn btn-primary" disabled={submitting}>
          {submitting ? "Saving..." : workItem ? "Update" : "Create"}
        </button>
      </div>
    </form>
  );
}
