import { useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { workItemService } from "../services/auth";
import WorkItemCard from "./WorkItemCard";
import { errorMessage } from "../utils/errorMessage";

/**
 * What an empty column says: what lands there and how. Shown when the column
 * is opened; until then an empty column folds to a narrow strip.
 */
const EMPTY_HINT: Record<WorkItemStatus, string> = {
  [WorkItemStatus.Backlog]: "New work items land here.",
  [WorkItemStatus.WorkQueue]: "Move a backlog item here to have the scheduler pick it up.",
  [WorkItemStatus.Ready]: "Queued items whose dependencies are done show up here.",
  [WorkItemStatus.Running]: "Ready items start here while the scheduler is running.",
  [WorkItemStatus.HumanFeedback]: "Runs that stop for a decision show up here.",
  [WorkItemStatus.WaitingForIld]: "Items wait here while ILD finishes something on its own.",
  [WorkItemStatus.Done]: "Finished items end up here.",
};

interface TaskboardColumnProps {
  status: WorkItemStatus;
  label: string;
  // The column's loaded cards, in server order.
  workItems: WorkItem[];
  // How many items the column holds on the server under the board filter.
  total: number;
  onWorkItemUpdate: (workItem: WorkItem) => void;
  onWorkItemClick?: (workItem: WorkItem) => void;
  onError?: (message: string) => void;
  onMoveWorkItem?: (workItem: WorkItem, direction: "prev" | "next") => void;
  onAddItem?: () => void;
  // Fetches the column's next page; offered while fewer cards are loaded than
  // the total.
  onLoadMore?: () => void;
  loadingMore?: boolean;
  // Loop template names, forwarded to each card so loop tags are highlighted.
  loopTemplateNames?: string[];
  // Repository names by id, so each card can show which repository it is on.
  repositoryNames?: ReadonlyMap<string, string>;
  // Shown on its own, as on a phone, where a picker above names the column and
  // counts it: no header, and an empty column says what lands there instead of folding.
  alone?: boolean;
}

export default function TaskboardColumn({
  status,
  label,
  workItems,
  total,
  onWorkItemUpdate,
  onWorkItemClick,
  onError,
  onMoveWorkItem,
  onAddItem,
  onLoadMore,
  loadingMore = false,
  loopTemplateNames,
  repositoryNames,
  alone = false,
}: TaskboardColumnProps) {
  const [dragOver, setDragOver] = useState(false);
  // An empty column folds to a strip so the busy ones get the room; it opens
  // when clicked, or while a card is dragged over it.
  const [opened, setOpened] = useState(false);
  // Empty means no card on screen and none counted: a card a live update just
  // added shows even before the count catches up.
  const empty = total === 0 && workItems.length === 0;
  const collapsed = empty && !opened && !dragOver && !alone;
  const needsYou = status === WorkItemStatus.HumanFeedback && !empty;
  const hasMore = onLoadMore !== undefined && workItems.length < total;

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
      const msg = errorMessage(error, "Failed to update work item status.");
      if (onError) onError(msg);
      else console.error("Failed to update work item status:", error);
    }
  };

  if (collapsed) {
    return (
      <div
        className="taskboard-column taskboard-column--collapsed"
        data-status={status}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <button
          type="button"
          className="taskboard-column-strip"
          aria-expanded={false}
          aria-label={`${label}: empty. Show column`}
          title={`${label}: empty`}
          onClick={() => setOpened(true)}
        >
          <span className="taskboard-column-count">0</span>
          <h3 className="taskboard-column-title">{label}</h3>
        </button>
      </div>
    );
  }

  return (
    <div
      className={`taskboard-column ${dragOver ? "drag-over" : ""}${needsYou ? " taskboard-column--needs" : ""}${alone ? " taskboard-column--alone" : ""}`}
      data-status={status}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {!alone && (
        <div className="taskboard-column-header">
          <h3 className="taskboard-column-title">{label}</h3>
          <div className="taskboard-column-header-end">
            <span className="taskboard-column-count">{total}</span>
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
            {empty && opened && (
              <button
                type="button"
                className="taskboard-column-fold"
                aria-label={`Fold ${label}`}
                onClick={() => setOpened(false)}
              >
                ×
              </button>
            )}
          </div>
        </div>
      )}
      <div className="taskboard-column-body">
        {empty && <p className="taskboard-column-empty">{EMPTY_HINT[status]}</p>}
        {workItems.map((item) => (
          <WorkItemCard
            key={item.id}
            workItem={item}
            onClick={onWorkItemClick}
            onMove={onMoveWorkItem}
            loopTemplateNames={loopTemplateNames}
            repositoryName={repositoryNames?.get(item.repositoryId)}
          />
        ))}
        {hasMore && (
          <button
            type="button"
            className="taskboard-column-load-more"
            onClick={onLoadMore}
            disabled={loadingMore}
          >
            Load more
          </button>
        )}
      </div>
      <style>{`
        .taskboard-column.taskboard-column--alone {
          border: 0;
          background: none;
        }

        .taskboard-column--alone .taskboard-column-body {
          padding: 0;
          overflow: visible;
        }

        .taskboard-column {
          flex: 1 1 0;
          min-width: 0;
          min-height: 0;
          background-color: var(--surface);
          border: 1px solid var(--border);
          border-radius: var(--radius-lg);
          display: flex;
          flex-direction: column;
        }

        /* Needs you: the column with something waiting on a person. */
        .taskboard-column--needs {
          border-color: rgba(245, 158, 11, 0.45);
        }

        .taskboard-column--needs .taskboard-column-count {
          background-color: var(--attention);
          color: #1a1205;
          font-weight: 700;
        }

        /* An empty column folded to a strip: its name runs up the side. */
        .taskboard-column--collapsed {
          flex: 0 0 44px;
        }

        .taskboard-column-strip {
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: var(--space-3);
          height: 100%;
          width: 100%;
          padding: var(--space-3) 0;
          background: none;
          border: none;
          cursor: pointer;
          color: var(--text-2);
          border-radius: var(--radius-lg);
        }

        .taskboard-column-strip:hover {
          background-color: var(--surface-2);
        }

        .taskboard-column-strip .taskboard-column-title {
          writing-mode: vertical-rl;
          transform: rotate(180deg);
          white-space: nowrap;
        }

        .taskboard-column-empty {
          margin: var(--space-5) var(--space-3);
          text-align: center;
          font-size: var(--fs-sm);
          color: var(--text-3);
        }

        .taskboard-column-fold {
          background: none;
          border: none;
          color: var(--text-3);
          cursor: pointer;
          font-size: 1rem;
          line-height: 1;
        }

        .taskboard-column.drag-over {
          outline: 2px dashed #6366f1;
          background-color: #252540;
        }

        .taskboard-column-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          gap: var(--space-2);
          padding: var(--space-3) 14px;
          border-bottom: 1px solid var(--border);
        }

        .taskboard-column-title {
          font-size: var(--fs-sm);
          font-weight: 600;
          color: var(--text);
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
        }

        .taskboard-column-header-end {
          display: flex;
          align-items: center;
          gap: 0.5rem;
        }

        .taskboard-column-count {
          font-size: var(--fs-xs);
          color: var(--text-2);
          background-color: var(--surface-3);
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

        .taskboard-column-load-more:disabled {
          opacity: 0.6;
          cursor: default;
        }
      `}</style>
    </div>
  );
}
