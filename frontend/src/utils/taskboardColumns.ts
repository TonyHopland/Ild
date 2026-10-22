import type { WorkItem, WorkItemStatus } from "../types";
import { matchesTaskboardFilter, type TaskboardFilter } from "./taskboardFilter";

/** What the board holds for one status column. */
export interface ColumnState {
  /** The loaded window of the column, in server order. */
  items: WorkItem[];
  /** How many items the column holds on the server under the filter. */
  total: number;
  /** Whether the column has had a page from the server yet. */
  loaded: boolean;
  loadingMore: boolean;
}

export type TaskboardColumns = Record<WorkItemStatus, ColumnState>;

/**
 * The board order the server pages a column in: the lowest priority first,
 * then the id, shorter first and then by code unit, which is the server's
 * ordinal comparison for these ids. The WorkItem server's
 * `WorkItemService.InBoardOrder` is the source of truth, and ILD's
 * `WorkItemOrder` follows it too: change all three together.
 */
export function compareServerOrder(a: WorkItem, b: WorkItem): number {
  if (a.priority !== b.priority) return a.priority < b.priority ? -1 : 1;
  if (a.id.length !== b.id.length) return a.id.length - b.id.length;
  if (a.id === b.id) return 0;
  return a.id < b.id ? -1 : 1;
}

/** The items with `item` in its place in server order, replacing any card with its id. */
export function insertInServerOrder(items: WorkItem[], item: WorkItem): WorkItem[] {
  const rest = items.filter((wi) => wi.id !== item.id);
  const index = rest.findIndex((wi) => compareServerOrder(item, wi) < 0);
  return index < 0 ? [...rest, item] : [...rest.slice(0, index), item, ...rest.slice(index)];
}

function holderOf(board: TaskboardColumns, id: string): WorkItemStatus | undefined {
  return (Object.keys(board) as WorkItemStatus[]).find((status) =>
    board[status].items.some((wi) => wi.id === id),
  );
}

/** How a live copy of an item joins the board. */
export interface ApplyOptions {
  /** Count an item the board never held (a create). */
  countAsNew: boolean;
  /** The card the server just placed the item directly after, for a drop. */
  placedAfter?: string;
}

/**
 * The column's loaded cards with `item` in its place in server order, or without
 * it when it sorts after the last loaded card of a column that has more on the
 * server, where it would be out of order — unless the server placed it directly
 * after that card.
 */
function placeInColumn(column: ColumnState, item: WorkItem, placedAfter?: string): WorkItem[] {
  const last = column.items[column.items.length - 1];
  const fits =
    column.loaded &&
    (column.items.length >= column.total ||
      (last !== undefined && (compareServerOrder(item, last) <= 0 || last.id === placedAfter)));
  return fits
    ? insertInServerOrder(column.items, item)
    : column.items.filter((wi) => wi.id !== item.id);
}

/**
 * The board with a live copy of an item applied. A card still in its column and
 * still matching the filter moves to its place there in server order; otherwise
 * it leaves the column that held it, and a matching item joins its status
 * column in server order. Either way, one that sorts beyond a column's loaded
 * cards (see `placeInColumn`) is only counted.
 * `countAsNew` counts an item the board never held (a create); anything else
 * the board did not hold is left for the server's counts to tally.
 */
export function applyItem(
  board: TaskboardColumns,
  item: WorkItem,
  filter: TaskboardFilter,
  { countAsNew, placedAfter }: ApplyOptions,
): TaskboardColumns {
  const holder = holderOf(board, item.id);
  const matches = matchesTaskboardFilter(item, filter);
  if (holder === item.status && matches) {
    return {
      ...board,
      [holder]: { ...board[holder], items: placeInColumn(board[holder], item, placedAfter) },
    };
  }

  const next = { ...board };
  if (holder) {
    const column = next[holder];
    next[holder] = {
      ...column,
      items: column.items.filter((wi) => wi.id !== item.id),
      total: Math.max(0, column.total - 1),
    };
  }
  const target = next[item.status];
  if (!matches || !target) return holder ? next : board;

  next[item.status] = {
    ...target,
    items: placeInColumn(target, item, placedAfter),
    total: holder || countAsNew ? target.total + 1 : target.total,
  };
  return next;
}

/** The board without the item, its column's total lowered when it held it. */
export function removeItem(board: TaskboardColumns, id: string): TaskboardColumns {
  const holder = holderOf(board, id);
  if (!holder) return board;
  const column = board[holder];
  return {
    ...board,
    [holder]: {
      ...column,
      items: column.items.filter((wi) => wi.id !== id),
      total: Math.max(0, column.total - 1),
    },
  };
}

/** The loaded items followed by a further page, skipping cards already loaded. */
export function appendPage(items: WorkItem[], page: WorkItem[]): WorkItem[] {
  const loaded = new Set(items.map((wi) => wi.id));
  return [...items, ...page.filter((wi) => !loaded.has(wi.id))];
}

/** The loaded card with this id, if any column holds it. */
export function findLoadedItem(board: TaskboardColumns, id: string): WorkItem | undefined {
  const holder = holderOf(board, id);
  return holder && board[holder].items.find((wi) => wi.id === id);
}
