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
  // A dropped card as read back from the server, with the card it now sits
  // directly after; without it, the card goes to onWorkItemUpdate.
  onWorkItemPlaced?: (workItem: WorkItem, placedAfter: string | undefined) => void;
  onWorkItemClick?: (workItem: WorkItem) => void;
  onError?: (message: string) => void;
  onMoveWorkItem?: (workItem: WorkItem, direction: "prev" | "next") => void;
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
  // null before they have; the board keeps it among their preferences.
  foldChoice: boolean | null;
  onFoldChange: (folded: boolean) => void;
}

export default function TaskboardColumn({
  status,
  label,
  workItems,
  total,
  onWorkItemUpdate,
  onWorkItemPlaced,
  onWorkItemClick,
  onError,
  onMoveWorkItem,
  onLoadMore,
  loadingMore = false,
  loopTemplateNames,
  repositoryName,
  alone = false,
  foldChoice,
  onFoldChange,
}: TaskboardColumnProps) {
  const [dragOver, setDragOver] = useState(false);
  // Where a card dragged over the column would land: before the loaded card at
  // this index, or after the last one at the end.
  const [dropIndex, setDropIndex] = useState<number | null>(null);
  // Any column folds to a strip and opens again from it. Until the user
  // chooses, Done and the empty columns other than Running start folded, so the
  // busy ones get the room. A folded column opens while a card is dragged over it.
  // Empty means no card on screen and none counted: a card a live update just
  // added shows even before the count catches up.
  const empty = total === 0 && workItems.length === 0;
  const foldedByDefault =
    status === WorkItemStatus.Done || (empty && status !== WorkItemStatus.Running);
  const collapsed = (foldChoice ?? foldedByDefault) && !dragOver && !alone;
  const needsYou = status === WorkItemStatus.HumanFeedback && !empty;
  const hasMore = onLoadMore !== undefined && workItems.length < total;

  // On a card's upper half the drop spot is above it, on its lower half below
  // it; anywhere else in the column it is below the last loaded card.
  const dropIndexAt = (e: React.DragEvent): number => {
    const card = e.target instanceof Element ? e.target.closest("[data-work-item-id]") : null;
    const index = card
      ? workItems.findIndex((wi) => wi.id === card.getAttribute("data-work-item-id"))
      : -1;
    if (index < 0) return workItems.length;
    const { top, height } = card!.getBoundingClientRect();
    return e.clientY < top + height / 2 ? index : index + 1;
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(true);
    setDropIndex(dropIndexAt(e));
  };

  const handleDragLeave = () => {
    setDragOver(false);
    setDropIndex(null);
  };

  const report = (error: unknown, fallback: string) => {
    const msg = errorMessage(error, fallback);
    if (onError) onError(msg);
    else console.error(fallback, error);
  };

  const handleDrop = async (e: React.DragEvent) => {
    e.preventDefault();
    const index = dropIndexAt(e);
    setDragOver(false);
    setDropIndex(null);

    const workItemId = e.dataTransfer.getData("text/plain");
    if (!workItemId) return;

    const fromThisColumn = workItems.some((wi) => wi.id === workItemId);
    const others = workItems.filter((wi) => wi.id !== workItemId);
    const position = workItems.slice(0, index).filter((wi) => wi.id !== workItemId).length;
    if (fromThisColumn && workItems[position]?.id === workItemId) return;

    if (!fromThisColumn) {
      try {
        onWorkItemUpdate(await workItemService.moveTo(workItemId, status));
      } catch (error) {
        report(error, "Failed to update work item status.");
        return;
      }
      if (others.length === 0) return;
    }

    const aboveId = others[position - 1]?.id;
    try {
      const placed = await workItemService.reorder(workItemId, {
        aboveId,
        belowId: others[position]?.id,
      });
      if (onWorkItemPlaced) onWorkItemPlaced(placed, aboveId);
      else onWorkItemUpdate(placed);
    } catch (error) {
      report(error, "Failed to reorder work item.");
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
          onClick={() => onFoldChange(false)}
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
      onDragEnd={handleDragLeave}
      onDrop={handleDrop}
    >
      {!alone && (
        <div className="taskboard-column-header">
          <h3 className="taskboard-column-title">{label}</h3>
          <div className="taskboard-column-header-end">
            <span className="taskboard-column-count">{total}</span>
            <button
              type="button"
              className="taskboard-column-fold"
              aria-label={`Fold ${label}`}
              title="Fold column"
              onClick={() => onFoldChange(true)}
            >
              <Icon name="back" size={14} />
            </button>
          </div>
        </div>
      )}
      <div className="taskboard-column-body">
        {empty && <p className="taskboard-column-empty">{EMPTY_HINT[status]}</p>}
        {workItems.map((item, i) => (
          <WorkItemCard
            key={item.id}
            workItem={item}
            dropEdge={
              dropIndex === i
                ? "above"
                : dropIndex === workItems.length && i === workItems.length - 1
                  ? "below"
                  : undefined
            }
            onClick={onWorkItemClick}
            onMove={onMoveWorkItem}
            loopTemplateNames={loopTemplateNames}
            repositoryName={repositoryName?.(item.repositoryId) ?? undefined}
          />
        ))}
        {hasMore && (
          <button
            type="button"
            className="btn btn-secondary btn-sm taskboard-column-load-more"
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
