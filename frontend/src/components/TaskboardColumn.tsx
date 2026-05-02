import { useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { workItemService } from "../services/auth";
import WorkItemCard from "./WorkItemCard";

interface TaskboardColumnProps {
  status: WorkItemStatus;
  label: string;
  workItems: WorkItem[];
  onWorkItemUpdate: (workItem: WorkItem) => void;
  onWorkItemClick?: (workItem: WorkItem) => void;
  onWorkItemDeleted?: (id: string) => void;
  onError?: (message: string) => void;
  onMoveWorkItem?: (workItem: WorkItem, direction: "prev" | "next") => void;
}

export default function TaskboardColumn({
  status,
  label,
  workItems,
  onWorkItemUpdate,
  onWorkItemClick,
  onWorkItemDeleted,
  onError,
  onMoveWorkItem,
}: TaskboardColumnProps) {
  const [dragOver, setDragOver] = useState(false);

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(true);
  };

  const handleDragLeave = () => {
    setDragOver(false);
  };

  const handleDrop = async (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);

    const workItemId = e.dataTransfer.getData("text/plain");
    if (!workItemId) return;

    try {
      await workItemService.transition(workItemId, status);
      const updated = await workItemService.getById(workItemId);
      onWorkItemUpdate(updated);
    } catch (error) {
      const fallback = "Failed to update work item status.";
      const msg =
        error instanceof Error && error.message
          ? error.message
          : typeof error === "string"
            ? error
            : fallback;
      if (onError) onError(msg);
      else console.error("Failed to update work item status:", error);
    }
  };

  return (
    <div
      className={`taskboard-column ${dragOver ? "drag-over" : ""}`}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      <div className="taskboard-column-header">
        <h3 className="taskboard-column-title">{label}</h3>
        <span className="taskboard-column-count">{workItems.length}</span>
      </div>
      <div className="taskboard-column-body">
        {workItems.map((item) => (
          <WorkItemCard
            key={item.id}
            workItem={item}
            onClick={onWorkItemClick}
            onDeleted={onWorkItemDeleted}
            onMove={onMoveWorkItem}
          />
        ))}
      </div>
      <style>{`
        .taskboard-column {
          flex: 1 1 0;
          min-width: 0;
          background-color: #1e1e30;
          border-radius: 0.5rem;
          display: flex;
          flex-direction: column;
          max-height: calc(100vh - 8rem);
        }

        .taskboard-column.drag-over {
          outline: 2px dashed #6366f1;
          background-color: #252540;
        }

        .taskboard-column-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.75rem 1rem;
          border-bottom: 1px solid #2d2d44;
        }

        .taskboard-column-title {
          font-size: 0.875rem;
          font-weight: 600;
          color: #c0c0d0;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .taskboard-column-count {
          font-size: 0.75rem;
          color: #707090;
          background-color: #2d2d44;
          padding: 0.125rem 0.5rem;
          border-radius: 9999px;
        }

        .taskboard-column-body {
          flex: 1;
          overflow-y: auto;
          padding: 0.5rem;
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }
      `}</style>
    </div>
  );
}
