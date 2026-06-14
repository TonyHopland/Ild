import { useEffect, useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { workItemService } from "../services/auth";
import WorkItemCard from "./WorkItemCard";

interface TaskboardColumnProps {
  status: WorkItemStatus;
  label: string;
  workItems: WorkItem[];
  onWorkItemUpdate: (workItem: WorkItem) => void;
  onWorkItemClick?: (workItem: WorkItem) => void;
  onError?: (message: string) => void;
  onMoveWorkItem?: (workItem: WorkItem, direction: "prev" | "next") => void;
  onAddItem?: () => void;
  // When set, the column lazily renders at most this many cards at a time and
  // reveals the rest in further increments of this size via a "Load more"
  // button. Omitted means every item is rendered up front.
  pageSize?: number;
  // Loop template names, forwarded to each card so loop tags are highlighted.
  loopTemplateNames?: string[];
}

export default function TaskboardColumn({
  status,
  label,
  workItems,
  onWorkItemUpdate,
  onWorkItemClick,
  onError,
  onMoveWorkItem,
  onAddItem,
  pageSize,
  loopTemplateNames,
}: TaskboardColumnProps) {
  const [dragOver, setDragOver] = useState(false);
  const [visibleCount, setVisibleCount] = useState(pageSize ?? 0);

  // Reset back to the first page whenever pagination is (re)configured so the
  // column never starts wider than one page after a prop change.
  useEffect(() => {
    setVisibleCount(pageSize ?? 0);
  }, [pageSize]);

  const paginated = pageSize !== undefined;
  const visibleItems = paginated ? workItems.slice(0, visibleCount) : workItems;
  const hasMore = paginated && workItems.length > visibleItems.length;

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

    // Ignore drops into the same column — nothing to do
    const existingItem = workItems.find((item) => item.id === workItemId);
    if (existingItem?.status === status) return;

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
        <div className="taskboard-column-header-end">
          <span className="taskboard-column-count">{workItems.length}</span>
          {onAddItem && (
            <button
              type="button"
              className="taskboard-column-add"
              onClick={onAddItem}
              title="New item"
              aria-label="New item"
            >
              + New
            </button>
          )}
        </div>
      </div>
      <div className="taskboard-column-body">
        {visibleItems.map((item) => (
          <WorkItemCard
            key={item.id}
            workItem={item}
            onClick={onWorkItemClick}
            onMove={onMoveWorkItem}
            loopTemplateNames={loopTemplateNames}
          />
        ))}
        {hasMore && (
          <button
            type="button"
            className="taskboard-column-load-more"
            onClick={() => setVisibleCount((count) => count + (pageSize ?? 0))}
          >
            Load more
          </button>
        )}
      </div>
      <style>{`
        .taskboard-column {
          flex: 1 1 0;
          min-width: 0;
          min-height: 0;
          background-color: #1e1e30;
          border-radius: 0.5rem;
          display: flex;
          flex-direction: column;
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

        .taskboard-column-header-end {
          display: flex;
          align-items: center;
          gap: 0.5rem;
        }

        .taskboard-column-count {
          font-size: 0.75rem;
          color: #707090;
          background-color: #2d2d44;
          padding: 0.125rem 0.5rem;
          border-radius: 9999px;
        }

        .taskboard-column-add {
          font-size: 0.75rem;
          font-weight: 600;
          color: #c0c0d0;
          background-color: #6366f1;
          border: none;
          padding: 0.2rem 0.55rem;
          border-radius: 0.375rem;
          cursor: pointer;
          line-height: 1.2;
          white-space: nowrap;
          transition: background-color 0.15s ease;
        }

        .taskboard-column-add:hover {
          background-color: #4f52d4;
        }

        .taskboard-column-body {
          flex: 1;
          overflow-y: auto;
          padding: 0.5rem;
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .taskboard-column-load-more {
          font-size: 0.75rem;
          font-weight: 600;
          color: #c0c0d0;
          background-color: #2d2d44;
          border: 1px solid #3a3a55;
          padding: 0.4rem 0.55rem;
          border-radius: 0.375rem;
          cursor: pointer;
          transition: background-color 0.15s ease;
        }

        .taskboard-column-load-more:hover {
          background-color: #353553;
        }
      `}</style>
    </div>
  );
}
