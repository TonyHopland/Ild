import { useCallback, useState, useEffect, useRef } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import { Repository, WorkItem, WorkItemStatus } from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import { workItemService, repositoryService, loopTemplateService } from "../../services/auth";
import TaskboardColumn from "../../components/TaskboardColumn";
import WorkItemModalV2 from "../../components/workitem-v2/WorkItemModalV2";
import Icon from "../../components/Icon";
import Sheet from "../../components/Sheet";
import { useIsMobile } from "../../hooks/useIsMobile";
import ErrorBanner from "../../components/ErrorBanner";
import { useSignalR } from "../../hooks/useSignalR";
import { WORK_ITEM_STATUSES } from "../../utils/constants";
import { normalizeWorkItemStatus } from "../../utils/workItemStatus";
import { errorMessage } from "../../utils/errorMessage";
import { makeLoopTagMatcher } from "../../utils/workItemJson";
import {
  EMPTY_TASKBOARD_FILTER,
  compareTags,
  isFilterActive,
  sameTag,
  type TaskboardFilter,
} from "../../utils/taskboardFilter";
import { findLoadedItem } from "../../utils/taskboardColumns";
import { useTaskboardColumns } from "./useTaskboardColumns";

const SEARCH_DEBOUNCE_MS = 300;

export default function Taskboard() {
  const navigate = useNavigate();
  // The id in the URL is the source of truth for which item's detail dialog is
  // open, so a work item can be linked to directly (e.g. /taskboard/<id>).
  const { workItemId: openWorkItemId } = useParams<{ workItemId?: string }>();
  const [searchParams] = useSearchParams();
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [editingItem, setEditingItem] = useState<WorkItem | null>(null);
  const [errorText, setErrorText] = useState("");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [loopTemplateNames, setLoopTemplateNames] = useState<string[]>([]);
  const [tagOptions, setTagOptions] = useState<string[]>([]);
  const tagsRefreshRef = useRef({ inFlight: false, dirty: false });
  const [filter, setFilter] = useState<TaskboardFilter>(EMPTY_TASKBOARD_FILTER);
  // A phone shows one column at a time, picked above the board; it opens on
  // Needs you, the column a person looks at the board for.
  const isMobile = useIsMobile();
  const [mobileStatus, setMobileStatus] = useState<WorkItemStatus>(WorkItemStatus.HumanFeedback);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const closeFilters = useCallback(() => setFiltersOpen(false), []);
  const pickerRef = useRef<HTMLDivElement>(null);
  // Search reaches the server once typing pauses; the repository and tags at once.
  const [appliedSearch, setAppliedSearch] = useState(filter.search);
  const {
    columns,
    isInitialLoading,
    reloadAll,
    loadMore,
    applyItem,
    removeItem,
    findItem,
    requestCountsRefresh,
  } = useTaskboardColumns({ ...filter, search: appliedSearch }, setErrorText);
  const { on, off, connectionState } = useSignalR();
  // Highest getById request ordinal issued per work item. A newly created item
  // receives a burst of hub events (create → claim → run-progressed → human),
  // each firing a getById; an early fetch the server answered with an older
  // status can resolve *after* a later one and clobber the fresher state. We
  // apply a fetch's result only when it is still the latest request for that
  // item, so a late, stale response is dropped instead of reverting the card.
  const syncSeqRef = useRef<Map<string, number>>(new Map());
  // Items whose latest getById is still out. A copy this client reads back after
  // changing the item may postdate that read, so the read is started again
  // rather than let land on top of it.
  const syncInFlightRef = useRef(new Set<string>());
  // Items announced as created, edited or moved whose fresh copy has not landed
  // yet. Only those can change the totals or the tags in use, and the mark
  // outlives any fetch a later run or preview event supersedes, so both are
  // still reconciled.
  const itemChangedRef = useRef(new Set<string>());
  // The work-item hub replays nothing on (re)subscribe — unlike SubscribeToRun,
  // SubscribeToWorkItems has no backlog buffer — so any events delivered while
  // the socket was down are lost for good. We reload the board on each
  // reconnect to re-sync; this ref skips the very first connect, whose load the
  // board's own first pages already performed.
  const hasConnectedRef = useRef(false);

  useEffect(() => {
    requestTagsRefresh();
    void repositoryService
      .getEvery()
      .then(setRepositories)
      .catch(() => {});
    void loopTemplateService
      .getEvery()
      .then((templates) => setLoopTemplateNames(templates.map((t) => t.name)))
      .catch(() => {});
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => setAppliedSearch(filter.search), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [filter.search]);

  const syncWorkItem = (workItemId: string, { itemChanged }: { itemChanged: boolean }) => {
    if (itemChanged) itemChangedRef.current.add(workItemId);
    const seq = (syncSeqRef.current.get(workItemId) ?? 0) + 1;
    syncSeqRef.current.set(workItemId, seq);
    syncInFlightRef.current.add(workItemId);
    const isLatest = () => syncSeqRef.current.get(workItemId) === seq;
    void workItemService
      .getById(workItemId)
      .then((wi) => {
        // Drop a response that a newer request has already superseded — the
        // later request reflects a fresher server state, so honoring this one
        // would revert the card to a stale status.
        if (!isLatest()) return;
        syncInFlightRef.current.delete(workItemId);
        if (itemChangedRef.current.delete(workItemId)) showLiveItem(wi, { countAsNew: false });
        else refreshShownItem(wi);
      })
      .catch(() => {
        if (isLatest()) syncInFlightRef.current.delete(workItemId);
      });
  };

  useEffect(() => {
    const delayedTimers: number[] = [];

    const onHumanFeedback = async (message: TypedSignalRMessage<"HumanFeedbackRequired">) => {
      const { workItemId, reason } = message.payload;
      const loaded = findItem(workItemId);
      if (loaded) {
        applyItem(
          { ...loaded, status: WorkItemStatus.HumanFeedback, humanFeedbackReason: reason },
          { countAsNew: false },
        );
      }
      syncWorkItem(workItemId, { itemChanged: true });
      delayedTimers.push(setTimeout(() => syncWorkItem(workItemId, { itemChanged: false }), 500));

      const notificationsEnabled = localStorage.getItem("ild_notifications_enabled") !== "false";
      if (
        notificationsEnabled &&
        typeof Notification !== "undefined" &&
        Notification.permission === "granted"
      ) {
        new Notification("Work Item Needs Attention", {
          body: reason,
        });
      }
    };

    const onWorkItemStateChanged = async (message: TypedSignalRMessage<"WorkItemStateChanged">) => {
      const { workItemId, newStatus } = message.payload;
      // Optimistically reflect the new status on a card already on the board; an
      // item we have not seen yet is placed by the syncWorkItem fetch below. Both
      // the optimistic write and the fetch are reconciled by that fetch, so the
      // status never lingers behind the event.
      const loaded = findItem(workItemId);
      if (loaded) {
        applyItem({ ...loaded, status: normalizeWorkItemStatus(newStatus) }, { countAsNew: false });
      }
      syncWorkItem(workItemId, { itemChanged: true });
      delayedTimers.push(setTimeout(() => syncWorkItem(workItemId, { itemChanged: false }), 500));
    };

    const onPreviewStateChanged = (message: TypedSignalRMessage<"PreviewStateChanged">) => {
      syncWorkItem(message.payload.workItemId, { itemChanged: false });
    };

    // When a running item advances to a new node, re-sync it so its card shows
    // the current step. Node transitions don't change the work item's status, so
    // this is the only signal that keeps a running card's step fresh.
    const onRunProgressed = (message: TypedSignalRMessage<"WorkItemRunProgressed">) => {
      syncWorkItem(message.payload.workItemId, { itemChanged: false });
    };

    const onEditProposalsChanged = (
      message: TypedSignalRMessage<"WorkItemEditProposalsChanged">,
    ) => {
      syncWorkItem(message.payload.workItemId, { itemChanged: false });
    };

    on("HumanFeedbackRequired", onHumanFeedback);
    on("WorkItemStateChanged", onWorkItemStateChanged);
    on("PreviewStateChanged", onPreviewStateChanged);
    on("WorkItemRunProgressed", onRunProgressed);
    on("WorkItemEditProposalsChanged", onEditProposalsChanged);

    return () => {
      off("HumanFeedbackRequired", onHumanFeedback);
      off("WorkItemStateChanged", onWorkItemStateChanged);
      off("PreviewStateChanged", onPreviewStateChanged);
      off("WorkItemRunProgressed", onRunProgressed);
      off("WorkItemEditProposalsChanged", onEditProposalsChanged);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [on, off]);

  // Reload the board whenever the hub transitions back into "connected". The
  // first connect is skipped because the board's first pages are already
  // loading; every later transition follows a dropout during which the
  // work-item hub buffered nothing, so reloading what each column shows is the
  // only way to recover missed events. The item open in the dialog is re-read
  // too, since it may not be on any column. The scheduler paused state and the
  // tags in use are re-synced for the same reason.
  useEffect(() => {
    if (connectionState !== "connected") return;
    if (!hasConnectedRef.current) {
      hasConnectedRef.current = true;
      return;
    }
    reloadAll({ windowed: true });
    if (openWorkItemId) syncWorkItem(openWorkItemId, { itemChanged: false });
    requestTagsRefresh();
  }, [connectionState]);

  // Keep the open detail item in sync with the id in the URL. Resolving from
  // the loaded board keeps the dialog's data fresh; a direct link to an item
  // not on the board is fetched on demand and shown in the dialog only — it
  // joins no column, where it could sit out of order — and a stale/invalid id
  // bounces back to the bare taskboard so the URL always matches what is open.
  const boardItem = openWorkItemId ? findLoadedItem(columns, openWorkItemId) : undefined;
  const editingItemId = editingItem?.id;
  useEffect(() => {
    if (!openWorkItemId) {
      setEditingItem(null);
      return;
    }
    if (boardItem) {
      setEditingItem(boardItem);
      return;
    }
    // Already open — fetched directly, or its card has since left the loaded
    // columns; live updates keep it fresh.
    if (editingItemId === openWorkItemId) return;
    if (isInitialLoading) return;
    let cancelled = false;
    void workItemService
      .getById(openWorkItemId)
      .then((wi) => {
        if (!cancelled) setEditingItem(wi);
      })
      .catch(() => {
        if (!cancelled) void navigate("/taskboard", { replace: true });
      });
    return () => {
      cancelled = true;
    };
  }, [openWorkItemId, boardItem, editingItemId, isInitialLoading, navigate]);

  // The tags in use, re-read from the server after every change that may add or
  // drop one: at most one read in flight, and one more if asked meanwhile, so
  // the last answer always postdates the last change.
  const requestTagsRefresh = () => {
    const refresh = tagsRefreshRef.current;
    if (refresh.inFlight) {
      refresh.dirty = true;
      return;
    }
    refresh.inFlight = true;
    void workItemService
      .getTags()
      .then(setTagOptions)
      .catch(() => {
        // Keep the chips already shown if the fetch fails.
      })
      .finally(() => {
        refresh.inFlight = false;
        if (refresh.dirty) {
          refresh.dirty = false;
          requestTagsRefresh();
        }
      });
  };

  // A created, edited or moved item: placed on the board, counted, its tags
  // reconciled with the chips, and refreshed in the dialog when it is the one open.
  const showLiveItem = (wi: WorkItem, { countAsNew }: { countAsNew: boolean }) => {
    applyItem(wi, { countAsNew });
    requestCountsRefresh();
    requestTagsRefresh();
    setEditingItem((open) => (open?.id === wi.id ? wi : open));
  };

  // A fresh copy of an item nothing announced as changed (a run step, a preview,
  // edit proposals): it updates the card and the dialog showing it, and no more.
  const refreshShownItem = (wi: WorkItem) => {
    if (findItem(wi.id)) applyItem(wi, { countAsNew: false });
    setEditingItem((open) => (open?.id === wi.id ? wi : open));
  };

  // A copy read back after this client changed the item: an answer, an edit, a
  // move or a creation.
  const showReadBack = (wi: WorkItem, { countAsNew }: { countAsNew: boolean }) => {
    showLiveItem(wi, { countAsNew });
    if (syncInFlightRef.current.has(wi.id)) syncWorkItem(wi.id, { itemChanged: false });
  };

  const handleWorkItemUpdate = (updated: WorkItem) => {
    showReadBack(updated, { countAsNew: false });
  };

  const handleCreated = (created: WorkItem) => {
    showReadBack(created, { countAsNew: true });
  };

  const openCreateModal = () => {
    setCreateModalOpen(true);
  };

  const handleCardClick = (wi: WorkItem) => {
    void navigate(`/taskboard/${wi.id}`);
  };

  const handleDeleted = (id: string) => {
    removeItem(id);
    requestCountsRefresh();
    requestTagsRefresh();
  };

  const [announcement, setAnnouncement] = useState("");

  const handleMoveWorkItem = async (workItem: WorkItem, direction: "prev" | "next") => {
    const order = WORK_ITEM_STATUSES.map((s) => s.value);
    const currentIndex = order.indexOf(workItem.status);
    if (currentIndex < 0) return;
    const nextIndex = direction === "next" ? currentIndex + 1 : currentIndex - 1;
    if (nextIndex < 0 || nextIndex >= order.length) return;
    const target = order[nextIndex] as WorkItemStatus;
    try {
      await workItemService.transition(workItem.id, target);
      const updated = await workItemService.getById(workItem.id);
      handleWorkItemUpdate(updated);
      setAnnouncement(`${workItem.title} moved to ${target}`);
    } catch (error) {
      setErrorText(errorMessage(error, "Failed to move work item."));
    }
  };

  const toggleTagFilter = (tag: string) => {
    setFilter((prev) => ({
      ...prev,
      tags: prev.tags.some((t) => sameTag(t, tag))
        ? prev.tags.filter((t) => !sameTag(t, tag))
        : [...prev.tags, tag],
    }));
  };

  useEffect(() => {
    pickerRef.current
      ?.querySelector<HTMLElement>('[aria-selected="true"]')
      ?.scrollIntoView?.({ inline: "center", block: "nearest" });
  }, [mobileStatus, isMobile, isInitialLoading]);

  const repositoryOptions = [...repositories].sort((a, b) => a.name.localeCompare(b.name));
  const repositoryNames = new Map(repositories.map((repo) => [repo.id, repo.name]));
  // A selected tag keeps its chip after its last use is gone, so it can still be unselected.
  const tagChips = [
    ...tagOptions,
    ...filter.tags.filter((tag) => !tagOptions.some((option) => sameTag(option, tag))),
  ].sort(compareTags);
  const isLoopTag = makeLoopTagMatcher(loopTemplateNames);
  const filterActive = isFilterActive(filter);
  const activeFilterCount = (filter.repositoryId ? 1 : 0) + filter.tags.length;
  // The repository and tag filters: inline on a wide screen, in a sheet on a phone.
  const filterControls = (
    <>
      <select
        className="taskboard-filter-repo"
        aria-label="Filter by repository"
        value={filter.repositoryId}
        onChange={(e) => setFilter((prev) => ({ ...prev, repositoryId: e.target.value }))}
      >
        <option value="">All repositories</option>
        {repositoryOptions.map((repo) => (
          <option key={repo.id} value={repo.id}>
            {repo.name}
          </option>
        ))}
      </select>
      {tagChips.length > 0 && (
        <div className="taskboard-filter-tags" role="group" aria-label="Filter by tag">
          {tagChips.map((tag) => {
            const active = filter.tags.some((selected) => sameTag(selected, tag));
            const loop = isLoopTag(tag);
            return (
              <button
                key={tag}
                type="button"
                className={`taskboard-filter-tag${loop ? " taskboard-filter-tag--loop" : ""}${
                  active ? " is-active" : ""
                }`}
                aria-pressed={active}
                onClick={() => toggleTagFilter(tag)}
              >
                {loop && <Icon name="loop" size={12} />}
                {tag}
              </button>
            );
          })}
        </div>
      )}
      {filterActive && (
        <button
          type="button"
          className="taskboard-filter-clear"
          onClick={() => setFilter(EMPTY_TASKBOARD_FILTER)}
        >
          Clear filters
        </button>
      )}
    </>
  );

  if (isInitialLoading) {
    return (
      <div className="page-container">
        <p>Loading taskboard...</p>
      </div>
    );
  }

  return (
    <div className="page-container taskboard-page">
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />
      <div className="taskboard-toolbar">
        <div className="taskboard-filter" role="search">
          <input
            type="search"
            className="taskboard-filter-search"
            placeholder="Search title or #id"
            aria-label="Search work items"
            value={filter.search}
            onChange={(e) => setFilter((prev) => ({ ...prev, search: e.target.value }))}
          />
          {isMobile ? (
            <button
              type="button"
              className="btn btn-secondary taskboard-filters-button"
              aria-haspopup="dialog"
              aria-expanded={filtersOpen}
              onClick={() => setFiltersOpen(true)}
            >
              <Icon name="filter" />
              Filters
              {activeFilterCount > 0 && (
                <span className="taskboard-filters-count">
                  {activeFilterCount}
                  <span className="sr-only"> on</span>
                </span>
              )}
            </button>
          ) : (
            filterControls
          )}
        </div>
        <button
          type="button"
          className={`btn btn-primary taskboard-new${isMobile ? " btn-icon" : ""}`}
          aria-label={isMobile ? "New work item" : undefined}
          onClick={openCreateModal}
        >
          <Icon name="plus" />
          {!isMobile && "New work item"}
        </button>
      </div>
      <div role="status" aria-live="polite" className="taskboard-live-region">
        {announcement}
      </div>
      {isMobile && (
        <div className="taskboard-picker" role="tablist" aria-label="Columns" ref={pickerRef}>
          {WORK_ITEM_STATUSES.map((status) => {
            const value = status.value as WorkItemStatus;
            const total = columns[value].total;
            const picked = value === mobileStatus;
            return (
              <button
                key={value}
                type="button"
                role="tab"
                aria-selected={picked}
                className={`taskboard-picker-tab${
                  value === WorkItemStatus.HumanFeedback && total > 0 ? " is-needs" : ""
                }`}
                onClick={() => setMobileStatus(value)}
              >
                {status.label}
                <span className="taskboard-picker-count">{total}</span>
              </button>
            );
          })}
        </div>
      )}
      <div className={`taskboard${isMobile ? " taskboard--one" : ""}`}>
        {WORK_ITEM_STATUSES.filter((status) => !isMobile || status.value === mobileStatus).map(
          (status) => {
            const column = columns[status.value as WorkItemStatus];
            return (
              <TaskboardColumn
                key={status.value}
                status={status.value as WorkItemStatus}
                label={status.label}
                workItems={column.items}
                total={column.total}
                onWorkItemUpdate={handleWorkItemUpdate}
                onWorkItemClick={handleCardClick}
                onError={(msg) => setErrorText(msg)}
                onMoveWorkItem={handleMoveWorkItem}
                loopTemplateNames={loopTemplateNames}
                repositoryNames={repositoryNames}
                onLoadMore={() => loadMore(status.value as WorkItemStatus)}
                loadingMore={column.loadingMore}
                alone={isMobile}
              />
            );
          },
        )}
      </div>
      {isMobile && filtersOpen && (
        <Sheet title="Filters" onClose={closeFilters}>
          <div className="taskboard-filter taskboard-filter--sheet">{filterControls}</div>
          <button
            type="button"
            className="btn btn-primary taskboard-filters-done"
            onClick={closeFilters}
          >
            Show results
          </button>
        </Sheet>
      )}
      {/* Existing items open in the tabbed detail dialog, keyed by the URL so the
          link matches the open item; a new item opens the same dialog with no
          work item, which renders its creation form. */}
      {editingItem && (
        // Keyed by the item, so opening another one builds a new dialog rather
        // than swapping the item under the old one. Everything the dialog holds
        // — staged files, a request still in flight, the errors it would report
        // — belongs to the item it was opened for, and this is what ends it with
        // that item rather than leaving it to be carried into the next.
        <WorkItemModalV2
          key={editingItem.id}
          workItem={editingItem}
          onClose={() => void navigate("/taskboard")}
          onSave={handleWorkItemUpdate}
          onDelete={handleDeleted}
          revealGlobalProposalId={searchParams.get("globalProposal")}
        />
      )}
      {createModalOpen && (
        <WorkItemModalV2
          key="new-work-item"
          workItem={null}
          onClose={() => setCreateModalOpen(false)}
          onSave={handleCreated}
          onDelete={handleDeleted}
        />
      )}
      <style>{`
        .taskboard-toolbar {
          display: flex;
          flex-wrap: wrap;
          align-items: center;
          gap: 0.75rem 1rem;
          margin-bottom: 1rem;
          padding: 0;
          background: none;
          border: 0;
        }

        /* New work item sits at the trailing edge, the filters flush left. */
        .taskboard-toolbar .taskboard-new {
          margin-left: auto;
        }

        .taskboard-filter {
          display: flex;
          flex: 1 1 auto;
          flex-wrap: wrap;
          align-items: center;
          gap: 0.5rem;
        }

        .taskboard-filter-search,
        .taskboard-filter-repo {
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          padding: 0.4rem 0.6rem;
          font-size: 0.85rem;
        }

        .taskboard-filter-search {
          min-width: 16rem;
          flex: 1 1 16rem;
          max-width: 24rem;
        }

        .taskboard-filter-search:focus-visible,
        .taskboard-filter-repo:focus-visible {
          outline: 2px solid var(--focus-color, #3b82f6);
          outline-offset: 1px;
        }

        .taskboard-filter-tags {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
        }

        .taskboard-filter-tag {
          display: inline-flex;
          align-items: center;
          gap: 5px;
          min-height: 30px;
          font-size: var(--fs-xs);
          padding: 0 12px;
          background-color: var(--surface-2);
          border: 1px solid var(--border);
          border-radius: 999px;
          color: var(--text-2);
          cursor: pointer;
        }

        /* Loop tags carry the loop icon, as on the cards. */
        .taskboard-filter-tag--loop {
          color: #c7c8fb;
        }

        .taskboard-filter-tag.is-active {
          background-color: var(--accent);
          border-color: var(--accent);
          color: #fff;
        }

        .taskboard-filter-clear {
          font-size: 0.8rem;
          padding: 0.3rem 0.6rem;
          background: none;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #a0a0b0;
          cursor: pointer;
        }

        .taskboard-filter-clear:hover {
          color: #e0e0e0;
        }

        .taskboard-live-region {
          position: absolute;
          width: 1px;
          height: 1px;
          padding: 0;
          margin: -1px;
          overflow: hidden;
          clip: rect(0, 0, 0, 0);
          white-space: nowrap;
          border: 0;
        }

        /* Fill the main area so the column row, not the window, owns the
           vertical space — the page never grows past the viewport. */
        .taskboard-page {
          height: 100%;
          display: flex;
          flex-direction: column;
          min-height: 0;
        }

        .taskboard {
          flex: 1;
          min-height: 0;
          display: flex;
          gap: 1rem;
          overflow-x: auto;
          padding-bottom: 1rem;
        }

        .taskboard-filters-count {
          min-width: 18px;
          padding: 0 5px;
          border-radius: 999px;
          background: var(--accent);
          color: #fff;
          font-size: 11px;
          font-weight: 700;
          line-height: 18px;
        }

        .taskboard-filter--sheet {
          flex-direction: column;
          align-items: stretch;
          gap: 16px;
          margin-bottom: 16px;
        }

        .taskboard-filter--sheet .taskboard-filter-repo {
          min-height: 44px;
        }

        .taskboard-filter--sheet .taskboard-filter-tag {
          min-height: 36px;
        }

        .taskboard-filter--sheet .taskboard-filter-tags {
          gap: 6px;
        }

        .taskboard-filters-done {
          width: 100%;
          min-height: 44px;
        }

        /* Phone: search, Filters and + on one row; the column picker scrolls
           sideways under them; the picked column fills the page and the page
           scrolls, not the column. */
        @media (max-width: 767px) {
          .taskboard-page {
            height: auto;
          }

          .taskboard-toolbar {
            flex-wrap: nowrap;
            gap: 8px;
            margin-bottom: 12px;
          }

          .taskboard-toolbar .taskboard-filter {
            flex-wrap: nowrap;
            min-width: 0;
          }

          .taskboard-filter-search {
            min-width: 0;
            max-width: none;
            flex: 1 1 auto;
            min-height: 44px;
          }

          .taskboard-toolbar .btn {
            min-height: 44px;
            flex: none;
          }

          .taskboard-toolbar .btn-icon {
            width: 44px;
          }

          .taskboard-picker {
            display: flex;
            gap: 6px;
            overflow-x: auto;
            margin: 0 -12px 12px;
            padding: 0 12px 4px;
            scrollbar-width: none;
          }

          .taskboard-picker::-webkit-scrollbar {
            display: none;
          }

          .taskboard-picker-tab {
            flex: none;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            min-height: 40px;
            padding: 0 14px;
            border-radius: 999px;
            border: 1px solid var(--border);
            background: var(--surface-2);
            color: var(--text-2);
            font-size: var(--fs-sm);
            white-space: nowrap;
            cursor: pointer;
          }

          .taskboard-picker-tab[aria-selected="true"] {
            background: var(--surface-3);
            border-color: var(--border-strong);
            color: var(--text);
          }

          .taskboard-picker-count {
            padding: 1px 7px;
            border-radius: 999px;
            background: var(--surface-3);
            color: var(--text-2);
            font-size: var(--fs-xs);
          }

          .taskboard-picker-tab.is-needs .taskboard-picker-count {
            background: var(--attention);
            color: #1a1205;
            font-weight: 700;
          }

          .taskboard.taskboard--one {
            display: block;
            overflow: visible;
            padding-bottom: 0;
          }
        }
      `}</style>
    </div>
  );
}
