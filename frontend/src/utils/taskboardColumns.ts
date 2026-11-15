import { WorkItemStatus, type WorkItem } from "../types";
import { WORK_ITEM_STATUS_ORDER, workItemStatusLabel } from "./labels";
import { matchesTaskboardFilter, type TaskboardFilter } from "./taskboardFilter";

/**
 * A board column, named by the status it is for. Ready has none of its own:
 * Ready items wait in the Work queue column, in one board order with the items
 * still waiting on their dependencies.
 */
export type TaskboardColumnKey = Exclude<WorkItemStatus, WorkItemStatus.Ready>;

/** The board's columns, left to right, and the statuses each one holds. */
export const TASKBOARD_COLUMNS: readonly {
  key: TaskboardColumnKey;
  label: string;
  statuses: WorkItemStatus[];
}[] = WORK_ITEM_STATUS_ORDER.filter(
  (status): status is TaskboardColumnKey => status !== WorkItemStatus.Ready,
).map((key) => ({
  key,
  label: workItemStatusLabel(key),
  statuses: key === WorkItemStatus.WorkQueue ? [key, WorkItemStatus.Ready] : [key],
}));

/** The column that holds items in this status. */
export function columnOf(status: WorkItemStatus): TaskboardColumnKey {
  return status === WorkItemStatus.Ready ? WorkItemStatus.WorkQueue : status;
}

/** What the board holds for one column. */
export interface ColumnState {
  /** The loaded window of the column, in server order. */
  items: WorkItem[];
  /** How many items the column holds on the server under the filter. */
  total: number;
  /** Whether the column has had a page from the server yet. */
  loaded: boolean;
  loadingMore: boolean;
}

export type TaskboardColumns = Record<TaskboardColumnKey, ColumnState>;

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

/** Every column that holds a card for the item: one, unless a read left a stale copy behind. */
function holdersOf(board: TaskboardColumns, id: string): TaskboardColumnKey[] {
  return (Object.keys(board) as TaskboardColumnKey[]).filter((column) =>
    board[column].items.some((wi) => wi.id === id),
  );
}

/** The board with each of `ids` gone from every column but `keep`, those columns' totals lowered by the cards removed. */
export function evictFromOtherColumns(
  board: TaskboardColumns,
  keep: TaskboardColumnKey,
  ids: ReadonlySet<string>,
): TaskboardColumns {
  let next = board;
  for (const column of Object.keys(board) as TaskboardColumnKey[]) {
    if (column === keep) continue;
    const held = board[column];
    const items = held.items.filter((wi) => !ids.has(wi.id));
    if (items.length === held.items.length) continue;
    if (next === board) next = { ...board };
    next[column] = {
      ...held,
      items,
      total: Math.max(0, held.total - (held.items.length - items.length)),
    };
  }
  return next;
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
 * it leaves the column that held it, and a matching item joins the column of
 * its status in server order. Either way, one that sorts beyond a column's loaded
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
  const holders = holdersOf(board, item.id);
  const matches = matchesTaskboardFilter(item, filter);
  const column = columnOf(item.status);
  if (holders.includes(column) && matches) {
    const kept = evictFromOtherColumns(board, column, new Set([item.id]));
    return {
      ...kept,
      [column]: { ...kept[column], items: placeInColumn(kept[column], item, placedAfter) },
    };
  }

  const next = { ...board };
  for (const holder of holders) {
    const held = next[holder];
    next[holder] = {
      ...held,
      items: held.items.filter((wi) => wi.id !== item.id),
      total: Math.max(0, held.total - 1),
    };
  }
  const target = next[column];
  if (!matches || !target) return holders.length ? next : board;

  next[column] = {
    ...target,
    items: placeInColumn(target, item, placedAfter),
    total: holders.length || countAsNew ? target.total + 1 : target.total,
  };
  return next;
}

/** The board without the item, its column's total lowered when it held it. */
export function removeItem(board: TaskboardColumns, id: string): TaskboardColumns {
  const holders = holdersOf(board, id);
  if (!holders.length) return board;
  const next = { ...board };
  for (const holder of holders) {
    const column = next[holder];
    next[holder] = {
      ...column,
      items: column.items.filter((wi) => wi.id !== id),
      total: Math.max(0, column.total - 1),
    };
  }
  return next;
}

/** The loaded items followed by a further page, skipping cards already loaded. */
export function appendPage(items: WorkItem[], page: WorkItem[]): WorkItem[] {
  const loaded = new Set(items.map((wi) => wi.id));
  return [...items, ...page.filter((wi) => !loaded.has(wi.id))];
}

/** The loaded card with this id, if any column holds it. */
export function findLoadedItem(board: TaskboardColumns, id: string): WorkItem | undefined {
  const holder = holdersOf(board, id)[0];
  return holder && board[holder].items.find((wi) => wi.id === id);
}
