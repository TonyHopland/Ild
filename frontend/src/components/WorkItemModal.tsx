import { useState, useEffect } from "react";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../types";
import { workItemService } from "../services/auth";

interface WorkItemModalProps {
  workItem: WorkItem | null;
  isOpen: boolean;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
}

export default function WorkItemModal({ workItem, isOpen, onClose, onSave }: WorkItemModalProps) {
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [status, setStatus] = useState<WorkItemStatus>(WorkItemStatus.Backlog);
  const [priority, setPriority] = useState<WorkItemPriority>(WorkItemPriority.Medium);
  const [tags, setTags] = useState("");

  useEffect(() => {
    if (workItem) {
      setTitle(workItem.title);
      setDescription(workItem.description);
      setStatus(workItem.status);
      setPriority(workItem.priority);
      setTags(workItem.labels.join(", "));
    } else {
      setTitle("");
      setDescription("");
      setStatus(WorkItemStatus.Backlog);
      setPriority(WorkItemPriority.Medium);
      setTags("");
    }
  }, [workItem]);

  if (!isOpen) return null;

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
      `}</style>
    </div>
  );
}
