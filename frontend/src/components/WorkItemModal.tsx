import { useState, useEffect } from "react";
import { Link } from "react-router-dom";
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

interface WorkItemModalProps {
  workItem: WorkItem | null;
  isOpen: boolean;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  editMode?: boolean;
}

export default function WorkItemModal({
  workItem,
  isOpen,
  onClose,
  onSave,
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
  const [showLinkPr, setShowLinkPr] = useState(false);
  const [prUrlInput, setPrUrlInput] = useState("");

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
        .then(setRuns)
        .catch(() => {});
    }
  }, [workItem?.id, editMode]);

  useEffect(() => {
    if (workItem) {
      setTitle(workItem.title);
      setDescription(workItem.description);
      setStatus(workItem.status);
      setPriority(workItem.priority);
      setTags(workItem.labels.join(", "));
      setRepositoryId(workItem.repositoryId);
      setLoopTemplateId(workItem.loopTemplateId);
      setShowLinkPr(false);
      setPrUrlInput("");
    } else {
      setTitle("");
      setDescription("");
      setStatus(WorkItemStatus.Backlog);
      setPriority(WorkItemPriority.Medium);
      setTags("");
      setRepositoryId("");
      setLoopTemplateId("");
    }
  }, [workItem]);

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

  if (workItem && !editMode) {
    return (
      <div className="modal-overlay" onClick={onClose}>
        <div className="modal-content modal-content-detail" onClick={(e) => e.stopPropagation()}>
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
            {workItem.dependencyIds.length > 0 && (
              <div className="detail-section">
                <span className="detail-label">Dependencies</span>
                <div className="dependency-list">
                  {workItem.dependencyIds.map((depId) => (
                    <span key={depId} className="dependency-tag">
                      {depId}
                    </span>
                  ))}
                </div>
              </div>
            )}
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

    const parsedLabels = tags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    const data: Partial<WorkItem> = {
      title,
      description,
      status,
      priority,
      labels: parsedLabels,
      repositoryId,
      loopTemplateId,
    };

    try {
      let saved: WorkItem;
      if (workItem) {
        saved = await workItemService.update(workItem.id, data);
      } else {
        saved = await workItemService.create(data);
      }
      onSave(saved);
      onClose();
    } catch (error) {
      console.error("Failed to save work item:", error);
    }
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-content" onClick={(e) => e.stopPropagation()}>
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
          </div>
          <div className="modal-footer">
            <button type="button" className="btn btn-secondary" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary">
              {workItem ? "Update" : "Create"}
            </button>
          </div>
        </form>
      </div>
      <style>{`
        .modal-overlay {
          position: fixed;
          inset: 0;
          background-color: rgba(0, 0, 0, 0.6);
          display: flex;
          align-items: center;
          justify-content: center;
          z-index: 200;
        }

        .modal-content {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          width: 100%;
          max-width: 500px;
          border: 1px solid #2d2d44;
        }

        .modal-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 1rem;
          border-bottom: 1px solid #2d2d44;
        }

        .modal-header h2 {
          font-size: 1rem;
          color: #e0e0e0;
        }

        .modal-close {
          background: none;
          border: none;
          color: #808090;
          font-size: 1.5rem;
          cursor: pointer;
          line-height: 1;
        }

        .modal-close:hover {
          color: #e0e0e0;
        }

        .modal-body {
          padding: 1rem;
        }

        .form-group {
          margin-bottom: 0.75rem;
        }

        .form-group label {
          display: block;
          font-size: 0.75rem;
          color: #a0a0b0;
          margin-bottom: 0.25rem;
        }

        .form-group input,
        .form-group textarea,
        .form-group select {
          width: 100%;
          padding: 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.875rem;
        }

        .form-group input:focus,
        .form-group textarea:focus,
        .form-group select:focus {
          outline: none;
          border-color: #6366f1;
        }

        .form-row {
          display: grid;
          grid-template-columns: repeat(2, 1fr);
          gap: 0.5rem;
        }

        .modal-footer {
          display: flex;
          justify-content: flex-end;
          gap: 0.5rem;
          padding: 1rem;
          border-top: 1px solid #2d2d44;
        }

        .btn-secondary {
          background-color: #2d2d44;
          color: #a0a0b0;
        }

        .btn-secondary:hover {
          background-color: #3a3a5c;
        }

        .btn-primary {
          background-color: #6366f1;
          color: #fff;
        }

        .btn-primary:hover {
          background-color: #5558e6;
        }

        .modal-content-detail {
          max-width: 550px;
        }

        .detail-row {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.5rem 0;
          border-bottom: 1px solid #2d2d44;
        }

        .detail-label {
          font-size: 0.75rem;
          color: #a0a0b0;
        }

        .detail-value {
          font-size: 0.875rem;
          color: #e0e0e0;
        }

        .status-badge {
          padding: 0.25rem 0.5rem;
          border-radius: 0.25rem;
          font-size: 0.75rem;
          font-weight: 600;
        }

        .status-ready {
          background-color: #16a34a;
          color: #fff;
        }

        .status-running {
          background-color: #2563eb;
          color: #fff;
        }

        .status-backlog {
          background-color: #6b7280;
          color: #fff;
        }

        .status-workqueue {
          background-color: #d97706;
          color: #fff;
        }

        .status-done {
          background-color: #059669;
          color: #fff;
        }

        .status-humanfeedback {
          background-color: #9333ea;
          color: #fff;
        }

        .detail-section {
          margin-top: 0.5rem;
          padding: 0.5rem 0;
          border-bottom: 1px solid #2d2d44;
        }

        .dependency-list {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
          margin-top: 0.25rem;
        }

        .dependency-tag {
          background-color: #2a2a40;
          color: #a0a0b0;
          padding: 0.125rem 0.5rem;
          border-radius: 0.25rem;
          font-size: 0.75rem;
          cursor: pointer;
        }

        .dependency-tag:hover {
          background-color: #3a3a5c;
          color: #e0e0e0;
        }

        .run-history {
          margin-top: 0.25rem;
        }

        .run-history-item {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.375rem 0;
          border-bottom: 1px solid #2d2d44;
        }

        .run-time {
          font-size: 0.75rem;
          color: #808090;
        }

        .run-history-item {
          text-decoration: none;
          color: inherit;
        }

        .run-history-item:hover {
          opacity: 0.8;
        }

        .pr-section {
          margin-top: 0.5rem;
        }

        .pr-link {
          color: #6366f1;
          font-size: 0.8125rem;
          word-break: break-all;
        }

        .pr-link:hover {
          color: #818cf8;
        }

        .pr-none {
          color: #606070;
          font-style: italic;
        }

        .link-pr-form {
          display: flex;
          flex-direction: column;
          gap: 0.375rem;
          margin-top: 0.375rem;
        }

        .pr-input {
          width: 100%;
          padding: 0.375rem 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.8125rem;
        }

        .pr-input:focus {
          outline: none;
          border-color: #6366f1;
        }

        .link-pr-actions {
          display: flex;
          gap: 0.375rem;
        }

        .btn-sm {
          padding: 0.25rem 0.5rem;
          font-size: 0.75rem;
        }

        .btn-success {
          background-color: #16a34a;
          color: #fff;
        }

        .btn-success:hover {
          background-color: #15803d;
        }
      `}</style>
    </div>
  );
}
