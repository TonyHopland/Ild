import { useCallback, useEffect, useRef, useState } from "react";
import { workItemService } from "../../services/auth";
import { WorkItem, WorkItemListFilter, WorkItemPage, WorkItemStatus } from "../../types";
import { TASKBOARD_PAGE_SIZE, WORK_ITEM_STATUSES } from "../../utils/constants";
import { errorMessage } from "../../utils/errorMessage";
import { matchesTaskboardFilter, type TaskboardFilter } from "../../utils/taskboardFilter";
import {
  appendPage,
  findLoadedItem,
  applyItem as applyItemTo,
  removeItem as removeItemFrom,
  type ColumnState,
  type TaskboardColumns,
} from "../../utils/taskboardColumns";

const STATUSES = WORK_ITEM_STATUSES.map((s) => s.value as WorkItemStatus);
/** The most one page request returns. */
const MAX_READ = 500;

function emptyColumns(): TaskboardColumns {
  const columns = {} as TaskboardColumns;
  for (const status of STATUSES) {
    columns[status] = { items: [], total: 0, loaded: false, loadingMore: false };
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
}

/**
 * The Taskboard's status columns, each paged from the server under `filter`.
 *
 * A column's membership (which cards, in which order, out of what total) and
 * its cards' contents change independently. Every column read carries the
 * board generation (bumped by each reload and filter change), the column's
 * membership sequence (bumped by a live change to its membership) and the
 * content clock at issue. A read from an older generation is dropped; one
 * whose column's membership changed in flight would undo that change, so it
 * is discarded and the column's window is read again. Every live change to an
 * item is recorded, held by a column or not, with its live copy or its removal.
 * A read that lands after one no longer speaks for that item: it takes the
 * live copy while that still belongs in this column, and otherwise leaves the
 * item out. A window of any size is read in pages the server allows, so a
 * reload never drops a loaded card.
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
  const settledRef = useRef(new Set<WorkItemStatus>());
  const countsRef = useRef({ inFlight: false, dirty: false });

  const show = useCallback((next: TaskboardColumns) => {
    if (next === boardRef.current) return;
    boardRef.current = next;
    setColumns(next);
  }, []);

  const commitLive = useCallback(
    (id: string, item: WorkItem | null, next: TaskboardColumns) => {
      liveRef.current.set(id, { at: ++clockRef.current, item });
      const prev = boardRef.current;
      for (const status of STATUSES) {
        if (!sameMembership(prev[status], next[status]))
          seqRef.current[status] = (seqRef.current[status] ?? 0) + 1;
      }
      show(next);
    },
    [show],
  );

  const settle = useCallback((status: WorkItemStatus) => {
    settledRef.current.add(status);
    if (settledRef.current.size === STATUSES.length) setIsInitialLoading(false);
  }, []);

  const readWindow = useCallback(
    async (
      status: WorkItemStatus,
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
          status,
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

  const fetchColumn = useCallback(
    function fetchColumn(status: WorkItemStatus, request: ColumnFetch) {
      const generation = generationRef.current;
      const seq = seqRef.current[status] ?? 0;
      const issuedAt = clockRef.current;
      const stale = () => generation !== generationRef.current;
      const moved = () => (seqRef.current[status] ?? 0) !== seq;
      readWindow(status, request.skip, request.take, () => stale() || moved())
        .then((page) => {
          if (stale()) return;
          const board = boardRef.current;
          const column = board[status];
          if (moved()) {
            const loaded = column.items.length + (request.append ? TASKBOARD_PAGE_SIZE : 0);
            fetchColumn(status, {
              skip: 0,
              take: windowSize(loaded),
              append: false,
              loadMore: request.loadMore,
            });
            return;
          }
          const fresh = page.items.flatMap((item) => {
            const live = liveRef.current.get(item.id);
            if (!live || live.at <= issuedAt) return [item];
            const copy = live.item;
            return copy && copy.status === status && matchesTaskboardFilter(copy, filterRef.current)
              ? [copy]
              : [];
          });
          show({
            ...board,
            [status]: {
              items: request.append ? appendPage(column.items, fresh) : fresh,
              total: page.total,
              loaded: true,
              loadingMore: request.loadMore ? false : column.loadingMore,
            },
          });
          settle(status);
        })
        .catch((error: unknown) => {
          if (stale()) return;
          if (request.loadMore) {
            const column = boardRef.current[status];
            show({ ...boardRef.current, [status]: { ...column, loadingMore: false } });
          }
          settle(status);
          onErrorRef.current(errorMessage(error, "Failed to load work items."));
        });
    },
    [readWindow, show, settle],
  );

  const reloadAll = useCallback(
    ({ windowed }: { windowed: boolean }) => {
      generationRef.current += 1;
      countsRef.current = { inFlight: false, dirty: false };
      liveRef.current.clear();
      const board = boardRef.current;
      const next = { ...board };
      for (const status of STATUSES) {
        if (board[status].loadingMore) next[status] = { ...board[status], loadingMore: false };
      }
      show(STATUSES.some((s) => next[s] !== board[s]) ? next : board);
      for (const status of STATUSES) {
        fetchColumn(status, {
          skip: 0,
          take: windowed ? windowSize(board[status].items.length) : TASKBOARD_PAGE_SIZE,
          append: false,
          loadMore: false,
        });
      }
    },
    [show, fetchColumn],
  );

  const loadMore = useCallback(
    (status: WorkItemStatus) => {
      const column = boardRef.current[status];
      if (!column.loaded || column.loadingMore || column.items.length >= column.total) return;
      show({ ...boardRef.current, [status]: { ...column, loadingMore: true } });
      fetchColumn(status, {
        skip: column.items.length,
        take: TASKBOARD_PAGE_SIZE,
        append: true,
        loadMore: true,
      });
    },
    [show, fetchColumn],
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
          for (const status of STATUSES) {
            const total = totals[status];
            if (typeof total === "number" && total !== board[status].total) {
              next[status] = { ...board[status], total };
            }
          }
          show(STATUSES.some((s) => next[s] !== board[s]) ? next : board);
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
    [show],
  );

  const applyItem = useCallback(
    (item: WorkItem, { countAsNew }: { countAsNew: boolean }) => {
      commitLive(
        item.id,
        item,
        applyItemTo(boardRef.current, item, filterRef.current, { countAsNew }),
      );
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
