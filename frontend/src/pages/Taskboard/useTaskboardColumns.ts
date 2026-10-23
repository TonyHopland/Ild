import { useCallback, useEffect, useRef, useState } from "react";
import { workItemService } from "../../services/auth";
import { WorkItem, WorkItemListFilter, WorkItemPage, WorkItemStatus } from "../../types";
import { TASKBOARD_PAGE_SIZE } from "../../utils/constants";
import { errorMessage } from "../../utils/errorMessage";
import { matchesTaskboardFilter, type TaskboardFilter } from "../../utils/taskboardFilter";
import {
  appendPage,
  columnOf,
  findLoadedItem,
  applyItem as applyItemTo,
  type ApplyOptions,
  removeItem as removeItemFrom,
  type ColumnState,
  TASKBOARD_COLUMNS,
  type TaskboardColumnKey,
  type TaskboardColumns,
} from "../../utils/taskboardColumns";

const COLUMNS = TASKBOARD_COLUMNS.map((c) => c.key);
const STATUSES_OF = Object.fromEntries(
  TASKBOARD_COLUMNS.map(({ key, statuses }) => [key, statuses]),
) as Record<TaskboardColumnKey, WorkItemStatus[]>;
/** The most one page request returns. */
const MAX_READ = 500;

function emptyColumns(): TaskboardColumns {
  const columns = {} as TaskboardColumns;
  for (const column of COLUMNS) {
    columns[column] = { items: [], total: 0, loaded: false, loadingMore: false };
  }
  return columns;
}

/** A reload of the first `loaded` cards: at least a page, never fewer than are shown. */
function windowSize(loaded: number): number {
  return Math.max(loaded, TASKBOARD_PAGE_SIZE);
}

function listFilter(filter: TaskboardFilter): WorkItemListFilter {
  return { search: filter.search.trim(), repositoryId: filter.repositoryId, tags: filter.tags };
}

/** Whether two states of a column hold the same cards in the same order under the same total. */
function sameMembership(a: ColumnState, b: ColumnState): boolean {
  return (
    a.total === b.total &&
    a.items.length === b.items.length &&
    a.items.every((item, i) => item.id === b.items[i].id)
  );
}

interface ColumnFetch {
  skip: number;
  take: number;
  append: boolean;
  /** The request answers a Load more, whose busy flag it clears when it lands. */
  loadMore: boolean;
  /** The request re-reads a short column, which may be re-read again once it lands. */
  refill: boolean;
}

/**
 * The Taskboard's columns, each paged from the server under `filter` across
 * the statuses it holds, in one board order.
 *
 * A column's membership (which cards, in which order, out of what total) and
 * its cards' contents change independently. Every column read carries the
 * board generation (bumped by each reload and filter change), the column's
 * membership sequence (bumped by a live change to its membership) and the
 * content clock at issue. A read from an older generation is dropped; one
 * whose column's membership changed in flight would undo that change, so it
 * is discarded and the column's window is read again; so is a read of a
 * column's first cards that lands on a column a Load more has since made
 * longer, which it would cut short. Every live change to an
 * item is recorded, held by a column or not, with its live copy or its removal.
 * A read that lands after one no longer speaks for that item: it takes the
 * live copy while that still belongs in this column, and otherwise leaves the
 * item out, and out of its total; the totals are then asked for again. A
 * window of any size is read in pages the server allows, so a reload never
 * drops a loaded card. A loaded column never shows fewer cards than its total
 * or a page, whichever is less: one that does after a live change, a counts
 * refresh or a read that left cards out has its first page read again.
 */
export function useTaskboardColumns(filter: TaskboardFilter, onError: (message: string) => void) {
  const [columns, setColumns] = useState<TaskboardColumns>(emptyColumns);
  const [isInitialLoading, setIsInitialLoading] = useState(true);
  const boardRef = useRef(columns);
  const filterRef = useRef(filter);
  // Read when a failure lands, so a new callback never restarts the board's reads.
  const onErrorRef = useRef(onError);
  onErrorRef.current = onError;
  const generationRef = useRef(0);
  const seqRef = useRef<Record<string, number>>({});
  // Content clock, and each item's latest live change: its copy, or null once removed.
  const clockRef = useRef(0);
  const liveRef = useRef(new Map<string, { at: number; item: WorkItem | null }>());
  const settledRef = useRef(new Set<TaskboardColumnKey>());
  const countsRef = useRef({ inFlight: false, dirty: false });
  const refillingRef = useRef(new Set<TaskboardColumnKey>());
  const fetchColumnRef = useRef<(key: TaskboardColumnKey, request: ColumnFetch) => void>(() => {});

  const show = useCallback((next: TaskboardColumns) => {
    if (next === boardRef.current) return;
    boardRef.current = next;
    setColumns(next);
  }, []);

  const refillShortColumns = useCallback(() => {
    for (const key of COLUMNS) {
      const column = boardRef.current[key];
      if (!column.loaded || column.loadingMore || refillingRef.current.has(key)) continue;
      if (column.items.length >= Math.min(column.total, TASKBOARD_PAGE_SIZE)) continue;
      refillingRef.current.add(key);
      fetchColumnRef.current(key, {
        skip: 0,
        take: TASKBOARD_PAGE_SIZE,
        append: false,
        loadMore: false,
        refill: true,
      });
    }
  }, []);

  const commitLive = useCallback(
    (id: string, item: WorkItem | null, next: TaskboardColumns) => {
      liveRef.current.set(id, { at: ++clockRef.current, item });
      const prev = boardRef.current;
      for (const key of COLUMNS) {
        if (!sameMembership(prev[key], next[key]))
          seqRef.current[key] = (seqRef.current[key] ?? 0) + 1;
      }
      show(next);
      refillShortColumns();
    },
    [show, refillShortColumns],
  );

  const settle = useCallback((key: TaskboardColumnKey) => {
    settledRef.current.add(key);
    if (settledRef.current.size === COLUMNS.length) setIsInitialLoading(false);
  }, []);

  const readWindow = useCallback(
    async (
      key: TaskboardColumnKey,
      skip: number,
      take: number,
      outdated: () => boolean,
    ): Promise<WorkItemPage> => {
      const items: WorkItem[] = [];
      let total = 0;
      while (items.length < take) {
        const chunk = Math.min(MAX_READ, take - items.length);
        const page = await workItemService.getPage({
          ...listFilter(filterRef.current),
          statuses: STATUSES_OF[key],
          skip: skip + items.length,
          take: chunk,
        });
        items.push(...page.items);
        total = page.total;
        if (page.items.length < chunk || outdated()) break;
      }
      return { items, total };
    },
    [],
  );

  const requestCountsRefresh = useCallback(
    function requestCountsRefresh() {
      const counts = countsRef.current;
      if (counts.inFlight) {
        counts.dirty = true;
        return;
      }
      counts.inFlight = true;
      const generation = generationRef.current;
      workItemService
        .getCounts(listFilter(filterRef.current))
        .then((totals) => {
          if (generation !== generationRef.current) return;
          const board = boardRef.current;
          const next = { ...board };
          for (const key of COLUMNS) {
            const counted = STATUSES_OF[key].map((status) => totals[status]);
            if (!counted.every((total) => typeof total === "number")) continue;
            const total = counted.reduce((sum, n) => sum + n, 0);
            if (total !== board[key].total) next[key] = { ...board[key], total };
          }
          show(COLUMNS.some((k) => next[k] !== board[k]) ? next : board);
          refillShortColumns();
        })
        .catch(() => {
          // Best effort: the totals keep their optimistic values until the next
          // live change or reload asks again.
        })
        .finally(() => {
          if (generation !== generationRef.current) return;
          counts.inFlight = false;
          if (counts.dirty) {
            counts.dirty = false;
            requestCountsRefresh();
          }
        });
    },
    [show, refillShortColumns],
  );

  const fetchColumn = useCallback(
    function fetchColumn(key: TaskboardColumnKey, request: ColumnFetch) {
      const generation = generationRef.current;
      const seq = seqRef.current[key] ?? 0;
      const issuedAt = clockRef.current;
      const shown = boardRef.current[key].items.length;
      const stale = () => generation !== generationRef.current;
      const moved = () => (seqRef.current[key] ?? 0) !== seq;
      readWindow(key, request.skip, request.take, () => stale() || moved())
        .then((page) => {
          if (stale()) return;
          const board = boardRef.current;
          const column = board[key];
          const outgrown = !request.append && column.items.length > shown;
          if (moved() || outgrown) {
            const loaded = column.items.length + (request.append ? TASKBOARD_PAGE_SIZE : 0);
            fetchColumn(key, {
              skip: 0,
              take: windowSize(loaded),
              append: false,
              loadMore: request.loadMore,
              refill: request.refill,
            });
            return;
          }
          const fresh = page.items.flatMap((item) => {
            const live = liveRef.current.get(item.id);
            if (!live || live.at <= issuedAt) return [item];
            const copy = live.item;
            return copy &&
              columnOf(copy.status) === key &&
              matchesTaskboardFilter(copy, filterRef.current)
              ? [copy]
              : [];
          });
          show({
            ...board,
            [key]: {
              items: request.append ? appendPage(column.items, fresh) : fresh,
              total: Math.max(0, page.total - (page.items.length - fresh.length)),
              loaded: true,
              loadingMore: request.loadMore ? false : column.loadingMore,
            },
          });
          settle(key);
          if (request.refill) refillingRef.current.delete(key);
          if (fresh.length < page.items.length) refillShortColumns();
          // The read's total predates the live changes since it was issued.
          if (clockRef.current > issuedAt) requestCountsRefresh();
        })
        .catch((error: unknown) => {
          if (stale()) return;
          if (request.refill) refillingRef.current.delete(key);
          if (request.loadMore) {
            const column = boardRef.current[key];
            show({ ...boardRef.current, [key]: { ...column, loadingMore: false } });
          }
          settle(key);
          onErrorRef.current(errorMessage(error, "Failed to load work items."));
        });
    },
    [readWindow, show, settle, requestCountsRefresh, refillShortColumns],
  );
  fetchColumnRef.current = fetchColumn;

  const reloadAll = useCallback(
    ({ windowed }: { windowed: boolean }) => {
      generationRef.current += 1;
      countsRef.current = { inFlight: false, dirty: false };
      liveRef.current.clear();
      refillingRef.current.clear();
      const board = boardRef.current;
      const next = { ...board };
      for (const key of COLUMNS) {
        if (board[key].loadingMore) next[key] = { ...board[key], loadingMore: false };
      }
      show(COLUMNS.some((k) => next[k] !== board[k]) ? next : board);
      for (const key of COLUMNS) {
        fetchColumn(key, {
          skip: 0,
          take: windowed ? windowSize(board[key].items.length) : TASKBOARD_PAGE_SIZE,
          append: false,
          loadMore: false,
          refill: false,
        });
      }
    },
    [show, fetchColumn],
  );

  const loadMore = useCallback(
    (key: TaskboardColumnKey) => {
      const column = boardRef.current[key];
      if (!column.loaded || column.loadingMore || column.items.length >= column.total) return;
      show({ ...boardRef.current, [key]: { ...column, loadingMore: true } });
      fetchColumn(key, {
        skip: column.items.length,
        take: TASKBOARD_PAGE_SIZE,
        append: true,
        loadMore: true,
        refill: false,
      });
    },
    [show, fetchColumn],
  );

  const applyItem = useCallback(
    (item: WorkItem, options: ApplyOptions) => {
      commitLive(item.id, item, applyItemTo(boardRef.current, item, filterRef.current, options));
    },
    [commitLive],
  );

  const removeItem = useCallback(
    (id: string) => {
      commitLive(id, null, removeItemFrom(boardRef.current, id));
    },
    [commitLive],
  );

  const findItem = useCallback((id: string) => findLoadedItem(boardRef.current, id), []);

  // A new filter object with the same values is not a new filter.
  const [applied, setApplied] = useState(filter);
  if (JSON.stringify(listFilter(filter)) !== JSON.stringify(listFilter(applied))) {
    setApplied(filter);
  }
  useEffect(() => {
    filterRef.current = applied;
    reloadAll({ windowed: false });
  }, [applied, reloadAll]);

  // Nothing started by this board lands after it is gone.
  useEffect(
    () => () => {
      generationRef.current += 1;
    },
    [],
  );

  return {
    columns,
    isInitialLoading,
    reloadAll,
    loadMore,
    applyItem,
    removeItem,
    findItem,
    requestCountsRefresh,
  };
}
