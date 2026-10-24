import { useEffect, useId, useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { columnOf, type TaskboardColumnKey } from "../utils/taskboardColumns";
import { workItemService } from "../services/auth";
import WorkItemCard, { WORK_ITEM_DRAG_TYPE } from "./WorkItemCard";
import { errorMessage } from "../utils/errorMessage";
import Icon from "./Icon";
import "./TaskboardColumn.css";

/** What an empty column says: what lands there and how, once it is opened. */
const EMPTY_HINT: Record<TaskboardColumnKey, string> = {
  [WorkItemStatus.Backlog]: "New work items land here.",
  [WorkItemStatus.WorkQueue]: "Move a backlog item here to queue it.",
  [WorkItemStatus.Running]: "Ready items start here while the scheduler is running.",
  [WorkItemStatus.HumanFeedback]: "Runs that stop for a decision show up here.",
  [WorkItemStatus.WaitingForIld]: "Items wait here while ILD finishes something on its own.",
  [WorkItemStatus.Done]: "Finished items end up here.",
};

/** How the Work queue column's order is read: shown on it whether or not it has cards. */
const QUEUE_ORDER_NOTE =
  "Ild starts the top-most item that can start; a waiting item doesn't hold up the ones below it.";

interface TaskboardColumnProps {
  // The status the column is named by; a card dropped in from another column moves to it.
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
  // Whether the scheduler is paused, for the cards' waiting reasons; undefined until read.
  schedulerPaused?: boolean;
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
  schedulerPaused,
}: TaskboardColumnProps) {
  const [dragOver, setDragOver] = useState(false);
  const orderNoteId = useId();
  const orderNote = status === WorkItemStatus.WorkQueue ? QUEUE_ORDER_NOTE : null;
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

  // The drop spot is above the first loaded card whose middle is below the
  // pointer, or after the last one; read from the pointer's height alone, so a
  // gap between cards, the padding or the header map to the nearest spot.
  const dropIndexAt = (e: React.DragEvent<HTMLElement>): number => {
    const cards = new Map(
      Array.from(e.currentTarget.querySelectorAll("[data-work-item-id]"), (card) => [
        card.getAttribute("data-work-item-id"),
        card,
      ]),
    );
    const index = workItems.findIndex((wi) => {
      const rect = cards.get(wi.id)?.getBoundingClientRect();
      return rect !== undefined && e.clientY < rect.top + rect.height / 2;
    });
    return index < 0 ? workItems.length : index;
  };

  const clearDrag = () => {
    setDragOver(false);
    setDropIndex(null);
  };

  // Every part of the column takes a card's drag, as a move; cancelling
  // dragenter as well as dragover is what keeps the cursor from showing "not
  // allowed". Anything else, such as a file, is left to the browser to refuse.
  const handleDragOver = (e: React.DragEvent<HTMLElement>) => {
    if (!e.dataTransfer.types.includes(WORK_ITEM_DRAG_TYPE)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    const index = dropIndexAt(e);
    if (!dragOver) setDragOver(true);
    if (index !== dropIndex) setDropIndex(index);
  };

  // dragleave bubbles up from every card and child the pointer leaves, so only
  // a move out of the column clears it. Firefox can leave relatedTarget out;
  // then the pointer's position says whether it is still inside, and a drag
  // cancelled there is cleared by its dragend.
  const handleDragLeave = (e: React.DragEvent<HTMLElement>) => {
    const to = e.relatedTarget;
    if (to instanceof Node) {
      if (e.currentTarget.contains(to)) return;
    } else {
      const { left, right, top, bottom } = e.currentTarget.getBoundingClientRect();
      if (e.clientX > left && e.clientX < right && e.clientY > top && e.clientY < bottom) return;
    }
    clearDrag();
  };

  // A drag that ends anywhere, dropped in another column or cancelled with
  // Escape, need not leave this column through dragleave. A dragged card that a
  // live update removes ends its drag where no dragend reaches the window; but
  // browsers send no mouse events during a drag, so the first mousemove after
  // it says the drag is over.
  useEffect(() => {
    if (!dragOver) return;
    const ends = ["dragend", "drop", "mousemove"];
    for (const end of ends) window.addEventListener(end, clearDrag);
    return () => {
      for (const end of ends) window.removeEventListener(end, clearDrag);
    };
  }, [dragOver]);

  const report = (error: unknown, fallback: string) => {
    const msg = errorMessage(error, fallback);
    if (onError) onError(msg);
    else console.error(fallback, error);
  };

  const handleDrop = async (e: React.DragEvent<HTMLElement>) => {
    e.preventDefault();
    const index = dropIndexAt(e);
    clearDrag();

    const workItemId = e.dataTransfer.getData("text/plain");
    if (!workItemId) return;

    const fromThisColumn = workItems.some((wi) => wi.id === workItemId);
    const others = workItems.filter((wi) => wi.id !== workItemId);
    const position = workItems.slice(0, index).filter((wi) => wi.id !== workItemId).length;
    if (fromThisColumn && workItems[position]?.id === workItemId) return;

    // An empty column has no spot to name; any other drop is one request that
    // places the card, moving it into this column when it came from another.
    if (others.length === 0) {
      try {
        onWorkItemUpdate(await workItemService.moveTo(workItemId, status));
      } catch (error) {
        report(error, "Failed to update work item status.");
      }
      return;
    }

    const aboveId = others[position - 1]?.id;
    try {
      const placed = await workItemService.reorder(workItemId, {
        aboveId,
        belowId: others[position]?.id,
        ...(fromThisColumn ? {} : { targetStatus: status }),
      });
      if (onWorkItemPlaced) onWorkItemPlaced(placed, aboveId);
      else onWorkItemUpdate(placed);
    } catch (error) {
      report(
        error,
        fromThisColumn ? "Failed to reorder work item." : "Failed to update work item status.",
      );
    }
  };

  if (collapsed) {
    const summary = empty ? "empty" : `${total} ${total === 1 ? "item" : "items"}`;
    return (
      <div
        className={`taskboard-column taskboard-column--collapsed${needsYou ? " taskboard-column--needs" : ""}`}
        data-status={status}
        onDragEnter={handleDragOver}
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
      onDragEnter={handleDragOver}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {!alone && (
        <div className="taskboard-column-header">
          <div className="taskboard-column-heading">
            <h3 className="taskboard-column-title">{label}</h3>
            {orderNote && (
              <span
                className="taskboard-column-info"
                role="img"
                tabIndex={0}
                aria-label={`How Ild picks from ${label}`}
                aria-describedby={orderNoteId}
                title={orderNote}
              >
                <Icon name="info" size={14} />
                <span id={orderNoteId} className="taskboard-column-tooltip">
                  {orderNote}
                </span>
              </span>
            )}
          </div>
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
        {alone && orderNote && (
          <p className="taskboard-column-note">
            <Icon name="info" size={14} />
            {orderNote}
          </p>
        )}
        {empty && <p className="taskboard-column-empty">{EMPTY_HINT[columnOf(status)]}</p>}
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
            schedulerPaused={schedulerPaused}
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
