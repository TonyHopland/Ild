import { useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { workItemService } from "../services/auth";
import WorkItemCard from "./WorkItemCard";
import { errorMessage } from "../utils/errorMessage";
import Icon from "./Icon";
import "./TaskboardColumn.css";

/** What an empty column says: what lands there and how, once it is opened. */
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
  // A repository id in words, so each card can show which repository it is on.
  repositoryName?: (id: string) => string | null;
  // Shown on its own, as on a phone, where a picker above names the column and
  // counts it: no header, and an empty column says what lands there instead of folding.
  alone?: boolean;
  // Whether the user chose to fold this column (true) or open it (false), or
  // null before they have; the board keeps it among their preferences. Left
  // out, the column keeps the choice itself.
  foldChoice?: boolean | null;
  onFoldChange?: (folded: boolean) => void;
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
  repositoryName,
  alone = false,
  foldChoice,
  onFoldChange,
}: TaskboardColumnProps) {
  const [dragOver, setDragOver] = useState(false);
  // Any column folds to a strip and opens again from it. Until the user
  // chooses, Done and the empty columns other than Running start folded, so the
  // busy ones get the room. A folded column opens while a card is dragged over it.
  const [ownChoice, setOwnChoice] = useState<boolean | null>(null);
  const choice = foldChoice !== undefined ? foldChoice : ownChoice;
  const fold = (folded: boolean) => (onFoldChange ? onFoldChange(folded) : setOwnChoice(folded));
  // Empty means no card on screen and none counted: a card a live update just
  // added shows even before the count catches up.
  const empty = total === 0 && workItems.length === 0;
  const foldedByDefault =
    status === WorkItemStatus.Done || (empty && status !== WorkItemStatus.Running);
  const collapsed = (choice ?? foldedByDefault) && !dragOver && !alone;
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
      onWorkItemUpdate(await workItemService.moveTo(workItemId, status));
    } catch (error) {
      const msg = errorMessage(error, "Failed to update work item status.");
      if (onError) onError(msg);
      else console.error("Failed to update work item status:", error);
    }
  };

  if (collapsed) {
    const summary = empty ? "empty" : `${total} ${total === 1 ? "item" : "items"}`;
    return (
      <div
        className={`taskboard-column taskboard-column--collapsed${needsYou ? " taskboard-column--needs" : ""}`}
        data-status={status}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <button
          type="button"
          className="taskboard-column-strip"
          aria-expanded={false}
          aria-label={`${label}: ${summary}. Show column`}
          title={`${label}: ${summary}`}
          onClick={() => fold(false)}
        >
          <span className="taskboard-column-count">{total}</span>
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
            <button
              type="button"
              className="taskboard-column-fold"
              aria-label={`Fold ${label}`}
              title="Fold column"
              onClick={() => fold(true)}
            >
              <Icon name="back" size={14} />
            </button>
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
            repositoryName={repositoryName?.(item.repositoryId) ?? undefined}
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
    </div>
  );
}
