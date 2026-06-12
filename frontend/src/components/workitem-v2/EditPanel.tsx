import { useState } from "react";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import { workItemService } from "../../services/auth";
import { parseTags } from "../../utils/workItemJson";
import TagAutocomplete from "../TagAutocomplete";
import type { WorkItemDetail } from "./useWorkItemDetail";

interface EditPanelProps {
  workItem: WorkItem;
  detail: WorkItemDetail;
  onSave: (workItem: WorkItem) => void;
  onDone: () => void;
}

/**
 * Inline edit form for the V2 dialog — same fields and save behaviour as the
 * classic modal's edit mode, but rendered inside the full-screen layout.
 */
export default function EditPanel({ workItem, detail, onSave, onDone }: EditPanelProps) {
  const [title, setTitle] = useState(workItem.title);
  const [description, setDescription] = useState(workItem.description);
  const [status, setStatus] = useState<WorkItemStatus>(workItem.status);
  const [priority, setPriority] = useState<WorkItemPriority>(workItem.priority);
  const [tags, setTags] = useState(parseTags(workItem).join(", "));
  const [repositoryId, setRepositoryId] = useState(workItem.repositoryId);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

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
      let saved = await workItemService.update(workItem.id, data as Partial<WorkItem>);
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
        <button type="button" className="btn btn-secondary" onClick={onDone} disabled={submitting}>
          Cancel
        </button>
        <button type="submit" className="btn btn-primary" disabled={submitting}>
          {submitting ? "Saving..." : "Update"}
        </button>
      </div>
    </form>
  );
}
