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

const SECONDS_AND_FRACTION = /^(.*T\d\d:\d\d:\d\d)(?:\.(\d+))?(.*)$/;

/**
 * A creation time as the server orders it, to the 100 ns tick: the whole
 * seconds, which Date.parse reads with their zone, and the fraction as seven
 * digits, which it would round to the millisecond.
 */
function createdAtKey(createdAt: string): [seconds: number, ticks: string] {
  const match = SECONDS_AND_FRACTION.exec(createdAt);
  if (!match) return [Date.parse(createdAt), "0000000"];
  return [Date.parse(match[1] + match[3]), (match[2] ?? "").padEnd(7, "0").slice(0, 7)];
}

/**
 * The order the server pages a column in: newest first, then the id, highest
 * first by code unit, which is the server's ordinal comparison for these ids.
 */
export function compareServerOrder(a: WorkItem, b: WorkItem): number {
  const [aSeconds, aTicks] = createdAtKey(a.createdAt);
  const [bSeconds, bTicks] = createdAtKey(b.createdAt);
  if (aSeconds !== bSeconds) return bSeconds - aSeconds;
  if (aTicks !== bTicks) return aTicks < bTicks ? 1 : -1;
  if (a.id === b.id) return 0;
  return a.id < b.id ? 1 : -1;
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

/**
 * The board with a live copy of an item applied. A card still in its column and
 * still matching the filter is replaced where it is; otherwise it leaves the
 * column that held it, and a matching item joins its status column in server
 * order — unless it sorts after the last loaded card of a column that has more
 * on the server, where it would be out of order, so it is only counted.
 * `countAsNew` counts an item the board never held (a create); anything else
 * the board did not hold is left for the server's counts to tally.
 */
export function applyItem(
  board: TaskboardColumns,
  item: WorkItem,
  filter: TaskboardFilter,
  { countAsNew }: { countAsNew: boolean },
): TaskboardColumns {
  const holder = holderOf(board, item.id);
  const matches = matchesTaskboardFilter(item, filter);
  if (holder === item.status && matches) {
    const column = board[holder];
    return {
      ...board,
      [holder]: {
        ...column,
        items: column.items.map((wi) => (wi.id === item.id ? item : wi)),
      },
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

  const last = target.items[target.items.length - 1];
  const fits =
    target.loaded &&
    (target.items.length >= target.total ||
      (last !== undefined && compareServerOrder(item, last) < 0));
  next[item.status] = {
    ...target,
    items: fits ? insertInServerOrder(target.items, item) : target.items,
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
