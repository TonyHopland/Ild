import { useState, useEffect } from "react";
import { Link } from "react-router-dom";
import "./WorkItemModal.css";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  Repository,
  LoopTemplate,
  LoopRun,
} from "../types";
import {
  workItemService,
  repositoryService,
  loopTemplateService,
  loopRunService,
} from "../services/auth";
import ConfirmModal from "./ConfirmModal";

interface WorkItemModalProps {
  workItem: WorkItem | null;
  isOpen: boolean;
  editMode?: boolean;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
}

export default function WorkItemModal({
  workItem,
  isOpen,
  onClose,
  onSave,
  onDelete,
  editMode = true,
}: WorkItemModalProps) {
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [status, setStatus] = useState<WorkItemStatus>(WorkItemStatus.Backlog);
  const [priority, setPriority] = useState<WorkItemPriority>(WorkItemPriority.Medium);
  const [tags, setTags] = useState("");
  const [repositoryId, setRepositoryId] = useState("");
  const [loopTemplateId, setLoopTemplateId] = useState("");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [dependencies, setDependencies] = useState<WorkItem[]>([]);
  const [allWorkItems, setAllWorkItems] = useState<WorkItem[]>([]);
  const [showAddDep, setShowAddDep] = useState(false);
  const [selectedDepId, setSelectedDepId] = useState("");
  const [showLinkPr, setShowLinkPr] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [prUrlInput, setPrUrlInput] = useState("");
  const [feedbackInput, setFeedbackInput] = useState("");
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

  useEffect(() => {
    repositoryService
      .getAll()
      .then(setRepositories)
      .catch(() => {});
    loopTemplateService
      .getAll()
      .then(setTemplates)
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (workItem && !editMode) {
      workItemService
        .getRuns(workItem.id)
        .then((r) => setRuns(Array.isArray(r) ? r : []))
        .catch(() => {});
      workItemService
        .getDependencies(workItem.id)
        .then((d) => setDependencies(Array.isArray(d) ? d : []))
        .catch(() => {});
      workItemService
        .getAll()
        .then((w) => setAllWorkItems(Array.isArray(w) ? w : []))
        .catch(() => {});
    }
  }, [workItem?.id, editMode]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  useEffect(() => {
    if (workItem) {
      setTitle(workItem.title);
      setDescription(workItem.description);
      setStatus(workItem.status);
      setPriority(workItem.priority);
      setTags(
        Array.isArray(workItem.labels)
          ? workItem.labels.join(", ")
          : typeof workItem.labels === "string"
            ? workItem.labels
            : "",
      );
      setRepositoryId(workItem.repositoryId);
      setLoopTemplateId(
        workItem.loopTemplateId ?? (workItem as any).loopTemplateVersion?.loopTemplateId ?? "",
      );
      setShowLinkPr(false);
      setPrUrlInput("");
      setFeedbackInput("");
    } else {
      setTitle("");
      setDescription("");
      setStatus(WorkItemStatus.Backlog);
      setPriority(WorkItemPriority.Medium);
      setTags("");
      setRepositoryId("");
      setLoopTemplateId("");
    }
  }, [workItem?.id]);

  if (!isOpen) return null;

  const handleStart = async () => {
    if (!workItem) return;
    try {
      await loopRunService.trigger(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to start work item:", error);
    }
  };

  const handleLinkPr = async () => {
    if (!workItem || !prUrlInput.trim()) return;
    try {
      await workItemService.linkPr(workItem.id, prUrlInput.trim());
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
      setShowLinkPr(false);
      setPrUrlInput("");
    } catch (error) {
      console.error("Failed to link PR:", error);
    }
  };

  const handleMarkMerged = async () => {
    if (!workItem) return;
    try {
      await workItemService.markMerged(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to mark merged:", error);
    }
  };

  const handleContinue = async () => {
    if (!workItem) return;
    try {
      await workItemService.humanFeedbackInput(workItem.id, feedbackInput);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to submit feedback:", error);
    }
  };

  const handleReject = async () => {
    if (!workItem) return;
    try {
      await workItemService.humanFeedbackReject(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to reject:", error);
    }
  };

  const handleCleanupDone = async () => {
    if (!workItem) return;
    try {
      await workItemService.cleanupToDone(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to cleanup to done:", error);
    }
  };

  const handleCleanupBacklog = async () => {
    if (!workItem) return;
    try {
      await workItemService.cleanupToBacklog(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to cleanup to backlog:", error);
    }
  };

  const handleDelete = async () => {
    if (!workItem) return;
    setShowDeleteConfirm(true);
  };

  const handleDeleteConfirm = async () => {
    setShowDeleteConfirm(false);
    if (!workItem) return;
    try {
      await workItemService.delete(workItem.id);
      onDelete?.(workItem.id);
      onClose();
    } catch (error) {
      console.error("Failed to delete work item:", error);
    }
  };

  const handleAddDependency = async () => {
    if (!workItem || !selectedDepId) return;
    try {
      await workItemService.addDependency(workItem.id, selectedDepId);
      const deps = await workItemService.getDependencies(workItem.id);
      setDependencies(deps);
      setShowAddDep(false);
      setSelectedDepId("");
    } catch (error) {
      console.error("Failed to add dependency:", error);
    }
  };

  const handleRemoveDependency = async (depId: string) => {
    if (!workItem) return;
    try {
      await workItemService.removeDependency(workItem.id, depId);
      setDependencies((prev) => prev.filter((d) => d.id !== depId));
    } catch (error) {
      console.error("Failed to remove dependency:", error);
    }
  };

  if (workItem && !editMode) {
    return (
      <div className="modal-overlay" onClick={onClose}>
        <div
          className="modal-content modal-content-detail"
          onClick={(e) => e.stopPropagation()}
          role="dialog"
          aria-modal="true"
          aria-label="Work item details"
        >
          <div className="modal-header">
            <h2>{workItem.title}</h2>
            <button className="modal-close" onClick={onClose}>
              &times;
            </button>
          </div>
          <div className="modal-body">
            <div className="detail-row">
              <span className="detail-label">Status</span>
              <span className={`status-badge status-${workItem.status.toLowerCase()}`}>
                {workItem.status}
              </span>
            </div>
            {workItem.description && (
              <div className="detail-row">
                <span className="detail-label">Description</span>
                <span className="detail-value">{workItem.description}</span>
              </div>
            )}
            <div className="detail-section">
              <span className="detail-label">Pull Request</span>
              {workItem.pullRequestUrl ? (
                <div className="pr-section">
                  <a
                    href={workItem.pullRequestUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="pr-link"
                  >
                    {workItem.pullRequestUrl}
                  </a>
                </div>
              ) : (
                <span className="detail-value pr-none">No PR linked</span>
              )}
              {showLinkPr ? (
                <div className="link-pr-form">
                  <input
                    type="url"
                    value={prUrlInput}
                    onChange={(e) => setPrUrlInput(e.target.value)}
                    placeholder="https://forgejo/pr/..."
                    className="pr-input"
                  />
                  <div className="link-pr-actions">
                    <button type="button" className="btn btn-sm btn-primary" onClick={handleLinkPr}>
                      Link
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setShowLinkPr(false)}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                <button
                  type="button"
                  className="btn btn-sm btn-secondary"
                  onClick={() => setShowLinkPr(true)}
                >
                  Link PR
                </button>
              )}
            </div>
            <div className="detail-section">
              <span className="detail-label">Dependencies</span>
              <div className="dependency-list">
                {dependencies.length === 0 && <span className="detail-value">No dependencies</span>}
                {dependencies.map((dep) => (
                  <span key={dep.id} className="dependency-tag">
                    <Link to={`/workitems/${dep.id}`} className="dependency-link">
                      {dep.title}
                    </Link>
                    <button
                      type="button"
                      className="dependency-remove-btn"
                      onClick={() => handleRemoveDependency(dep.id)}
                      aria-label={`Remove dependency ${dep.title}`}
                    >
                      ×
                    </button>
                  </span>
                ))}
              </div>
              {showAddDep ? (
                <div className="link-pr-form">
                  <select
                    value={selectedDepId}
                    onChange={(e) => setSelectedDepId(e.target.value)}
                    className="pr-input"
                  >
                    <option value="">Select work item...</option>
                    {allWorkItems
                      .filter(
                        (w) => w.id !== workItem.id && !dependencies.some((d) => d.id === w.id),
                      )
                      .map((w) => (
                        <option key={w.id} value={w.id}>
                          {w.title}
                        </option>
                      ))}
                  </select>
                  <div className="link-pr-actions">
                    <button
                      type="button"
                      className="btn btn-sm btn-primary"
                      onClick={handleAddDependency}
                    >
                      Add
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setShowAddDep(false)}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                <button
                  type="button"
                  className="btn btn-sm btn-secondary"
                  onClick={() => setShowAddDep(true)}
                >
                  Add Dependency
                </button>
              )}
            </div>
            {runs.length > 0 && (
              <div className="detail-section">
                <span className="detail-label">Run History</span>
                <div className="run-history">
                  {runs.map((run) => (
                    <Link key={run.id} to={`/loop-runs/${run.id}`} className="run-history-item">
                      <span className={`status-badge status-${run.status.toLowerCase()}`}>
                        {run.status}
                      </span>
                      <span className="run-time">{new Date(run.startedAt).toLocaleString()}</span>
                    </Link>
                  ))}
                </div>
              </div>
            )}
            {workItem.status === WorkItemStatus.HumanFeedback &&
              workItem.humanFeedbackReason === "Human Input Needed" && (
                <div className="detail-section human-feedback-section">
                  <span className="detail-label">Human Feedback</span>
                  <textarea
                    className="feedback-textarea"
                    value={feedbackInput}
                    onChange={(e) => setFeedbackInput(e.target.value)}
                    placeholder="Provide input or context..."
                    rows={3}
                  />
                  <div className="feedback-actions">
                    <button
                      type="button"
                      className="btn btn-sm btn-primary"
                      onClick={handleContinue}
                    >
                      Continue
                    </button>
                    <button type="button" className="btn btn-sm btn-danger" onClick={handleReject}>
                      Reject
                    </button>
                  </div>
                </div>
              )}
            {workItem.status === WorkItemStatus.HumanFeedback &&
              workItem.humanFeedbackReason &&
              workItem.humanFeedbackReason !== "Human Input Needed" && (
                <div className="detail-section human-feedback-section">
                  <span className="detail-label">Human Feedback</span>
                  <div className="feedback-reason">{workItem.humanFeedbackReason}</div>
                  <div className="feedback-actions">
                    <button
                      type="button"
                      className="btn btn-sm btn-warning"
                      onClick={handleCleanupDone}
                    >
                      Cleanup -&gt; Done
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={handleCleanupBacklog}
                    >
                      Cleanup -&gt; Backlog
                    </button>
                  </div>
                </div>
              )}
          </div>
          <div className="modal-footer">
            {workItem.status === WorkItemStatus.Ready && (
              <button type="button" className="btn btn-primary" onClick={handleStart}>
                Start
              </button>
            )}
            {workItem.pullRequestUrl && (
              <button type="button" className="btn btn-success" onClick={handleMarkMerged}>
                Mark Merged
              </button>
            )}
            <button type="button" className="btn btn-danger" onClick={handleDelete}>
              Delete
            </button>
            <button type="button" className="btn btn-secondary" onClick={onClose}>
              Close
            </button>
          </div>
        </div>
      </div>
    );
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setSubmitError(null);

    const domTitle = (document.getElementById("title") as HTMLInputElement)?.value ?? title;
    const domDescription =
      (document.getElementById("description") as HTMLTextAreaElement)?.value ?? description;
    const domTags = (document.getElementById("tags") as HTMLInputElement)?.value ?? tags;
    const domRepository =
      (document.getElementById("repository") as HTMLSelectElement)?.value ?? repositoryId;
    const domTemplate =
      (document.getElementById("loopTemplate") as HTMLSelectElement)?.value ?? loopTemplateId;
    const domStatus = (document.getElementById("status") as HTMLSelectElement)?.value ?? status;
    const domPriority =
      (document.getElementById("priority") as HTMLSelectElement)?.value ?? priority;

    const parsedLabels = domTags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    const data: Partial<WorkItem> = {
      title: domTitle,
      description: domDescription,
      status: domStatus as WorkItemStatus,
      priority: domPriority as WorkItemPriority,
      labels: parsedLabels,
      repositoryId: domRepository,
      loopTemplateId: domTemplate,
    };

    try {
      let saved: WorkItem;
      if (workItem) {
        saved = await workItemService.update(workItem.id, data);
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
        saved = await workItemService.create(data);
      }
      onSave(saved);
      onClose();
    } catch (error) {
      console.error("Failed to save work item:", error);
      setSubmitError(`Failed to save: ${error instanceof Error ? error.message : "Unknown error"}`);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div
        className="modal-content"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label={workItem ? "Edit work item" : "New work item"}
      >
        <div className="modal-header">
          <h2>{workItem ? "Edit Work Item" : "New Work Item"}</h2>
          <button className="modal-close" onClick={onClose}>
            &times;
          </button>
        </div>
        <form onSubmit={handleSubmit}>
          <div className="modal-body">
            <div className="form-group">
              <label htmlFor="title">Title</label>
              <input
                id="title"
                type="text"
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                required
              />
            </div>
            <div className="form-group">
              <label htmlFor="description">Description</label>
              <textarea
                id="description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                rows={3}
              />
            </div>
            <div className="form-row">
              <div className="form-group">
                <label htmlFor="repository">Repository</label>
                <select
                  id="repository"
                  value={repositoryId}
                  onChange={(e) => setRepositoryId(e.target.value)}
                >
                  <option value="">Select repository...</option>
                  {repositories.map((r) => (
                    <option key={r.id} value={r.id}>
                      {r.name}
                    </option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label htmlFor="loopTemplate">Loop Template</label>
                <select
                  id="loopTemplate"
                  value={loopTemplateId}
                  onChange={(e) => setLoopTemplateId(e.target.value)}
                >
                  <option value="">Select template...</option>
                  {templates.map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <div className="form-row">
              <div className="form-group">
                <label htmlFor="status">Status</label>
                <select
                  id="status"
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
                <label htmlFor="priority">Priority</label>
                <select
                  id="priority"
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
              <label htmlFor="tags">Labels (comma separated)</label>
              <input id="tags" type="text" value={tags} onChange={(e) => setTags(e.target.value)} />
            </div>
            {submitError && (
              <div role="alert" className="form-error">
                {submitError}
              </div>
            )}
          </div>
          <div className="modal-footer">
            <button
              type="button"
              className="btn btn-secondary"
              onClick={onClose}
              disabled={submitting}
            >
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={submitting}>
              {submitting ? "Saving..." : workItem ? "Update" : "Create"}
            </button>
          </div>
        </form>
      </div>
      <ConfirmModal
        isOpen={showDeleteConfirm}
        title="Delete Work Item"
        message={`Are you sure you want to delete "${workItem?.title}"?`}
        onConfirm={handleDeleteConfirm}
        onCancel={() => setShowDeleteConfirm(false)}
      />
    </div>
  );
}
