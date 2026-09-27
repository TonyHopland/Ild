import { useCallback, useEffect, useRef, useState } from "react";
import { workItemService } from "../../services/auth";
import { WorkItem, WorkItemListFilter, WorkItemStatus } from "../../types";
import { TASKBOARD_PAGE_SIZE, WORK_ITEM_STATUSES } from "../../utils/constants";
import type { TaskboardFilter } from "../../utils/taskboardFilter";
import {
  appendPage,
  findLoadedItem,
  applyItem as applyItemTo,
  removeItem as removeItemFrom,
  type TaskboardColumns,
} from "../../utils/taskboardColumns";

const STATUSES = WORK_ITEM_STATUSES.map((s) => s.value as WorkItemStatus);
const MAX_WINDOW = 500;

function emptyColumns(): TaskboardColumns {
  const columns = {} as TaskboardColumns;
  for (const status of STATUSES) {
    columns[status] = { items: [], total: 0, loaded: false, loadingMore: false };
  }
  return columns;
}

/** A reload of the first `loaded` cards: at least a page, at most what the server returns. */
function windowSize(loaded: number): number {
  return Math.min(MAX_WINDOW, Math.max(loaded, TASKBOARD_PAGE_SIZE));
}

function listFilter(filter: TaskboardFilter): WorkItemListFilter {
  return { search: filter.search.trim(), repositoryId: filter.repositoryId, tags: filter.tags };
}

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
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
 * Every column request is tagged with the board generation (bumped by each
 * reload and filter change) and the column's sequence (bumped by every change
 * to the column's cards). A response from an older generation is dropped; one
 * whose column changed while it was in flight would overwrite that change, so
 * it is discarded and the column's window is fetched again instead.
 */
export function useTaskboardColumns(filter: TaskboardFilter, onError: (message: string) => void) {
  const [columns, setColumns] = useState<TaskboardColumns>(emptyColumns);
  const [isInitialLoading, setIsInitialLoading] = useState(true);
  const boardRef = useRef(columns);
  const filterRef = useRef(filter);
  const generationRef = useRef(0);
  const seqRef = useRef<Record<string, number>>({});
  const settledRef = useRef(new Set<WorkItemStatus>());
  const countsRef = useRef({ inFlight: false, dirty: false });

  const commit = useCallback((next: TaskboardColumns, changesCards: boolean) => {
    const prev = boardRef.current;
    if (next === prev) return;
    if (changesCards) {
      for (const status of STATUSES) {
        if (next[status] !== prev[status])
          seqRef.current[status] = (seqRef.current[status] ?? 0) + 1;
      }
    }
    boardRef.current = next;
    setColumns(next);
  }, []);

  const settle = useCallback((status: WorkItemStatus) => {
    settledRef.current.add(status);
    if (settledRef.current.size === STATUSES.length) setIsInitialLoading(false);
  }, []);

  const fetchColumn = useCallback(
    function fetchColumn(status: WorkItemStatus, request: ColumnFetch) {
      const generation = generationRef.current;
      const seq = seqRef.current[status] ?? 0;
      workItemService
        .getPage({
          ...listFilter(filterRef.current),
          status,
          skip: request.skip,
          take: request.take,
        })
        .then((page) => {
          if (generation !== generationRef.current) return;
          const column = boardRef.current[status];
          if ((seqRef.current[status] ?? 0) !== seq) {
            const loaded = column.items.length + (request.append ? TASKBOARD_PAGE_SIZE : 0);
            fetchColumn(status, {
              skip: 0,
              take: windowSize(loaded),
              append: false,
              loadMore: request.loadMore,
            });
            return;
          }
          commit(
            {
              ...boardRef.current,
              [status]: {
                items: request.append ? appendPage(column.items, page.items) : page.items,
                total: page.total,
                loaded: true,
                loadingMore: request.loadMore ? false : column.loadingMore,
              },
            },
            true,
          );
          settle(status);
        })
        .catch((error: unknown) => {
          if (generation !== generationRef.current) return;
          if (request.loadMore) {
            const column = boardRef.current[status];
            commit({ ...boardRef.current, [status]: { ...column, loadingMore: false } }, false);
          }
          settle(status);
          onError(errorMessage(error, "Failed to load work items."));
        });
    },
    [commit, settle, onError],
  );

  const reloadAll = useCallback(
    ({ windowed }: { windowed: boolean }) => {
      generationRef.current += 1;
      countsRef.current = { inFlight: false, dirty: false };
      const board = boardRef.current;
      const next = { ...board };
      for (const status of STATUSES) {
        if (board[status].loadingMore) next[status] = { ...board[status], loadingMore: false };
      }
      commit(STATUSES.some((s) => next[s] !== board[s]) ? next : board, false);
      for (const status of STATUSES) {
        fetchColumn(status, {
          skip: 0,
          take: windowed ? windowSize(board[status].items.length) : TASKBOARD_PAGE_SIZE,
          append: false,
          loadMore: false,
        });
      }
    },
    [commit, fetchColumn],
  );

  const loadMore = useCallback(
    (status: WorkItemStatus) => {
      const column = boardRef.current[status];
      if (!column.loaded || column.loadingMore || column.items.length >= column.total) return;
      commit({ ...boardRef.current, [status]: { ...column, loadingMore: true } }, false);
      fetchColumn(status, {
        skip: column.items.length,
        take: TASKBOARD_PAGE_SIZE,
        append: true,
        loadMore: true,
      });
    },
    [commit, fetchColumn],
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
          commit(STATUSES.some((s) => next[s] !== board[s]) ? next : board, false);
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
    [commit],
  );

  const applyItem = useCallback(
    (item: WorkItem, { countAsNew }: { countAsNew: boolean }) => {
      commit(applyItemTo(boardRef.current, item, filterRef.current, { countAsNew }), true);
    },
    [commit],
  );

  const removeItem = useCallback(
    (id: string) => {
      commit(removeItemFrom(boardRef.current, id), true);
    },
    [commit],
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
