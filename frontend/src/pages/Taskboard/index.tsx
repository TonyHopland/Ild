import { useState, useEffect, useRef } from "react";
import { useNavigate, useParams } from "react-router";
import { Repository, WorkItem, WorkItemStatus } from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import {
  workItemService,
  settingsService,
  repositoryService,
  loopTemplateService,
  SchedulerSettingKeys,
} from "../../services/auth";
import TaskboardColumn from "../../components/TaskboardColumn";
import WorkItemModalV2 from "../../components/workitem-v2/WorkItemModalV2";
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
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [editingItem, setEditingItem] = useState<WorkItem | null>(null);
  const [errorText, setErrorText] = useState("");
  const [isPaused, setIsPaused] = useState(false);
  const [pauseBusy, setPauseBusy] = useState(false);
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [loopTemplateNames, setLoopTemplateNames] = useState<string[]>([]);
  const [tagOptions, setTagOptions] = useState<string[]>([]);
  const tagsRefreshRef = useRef({ inFlight: false, dirty: false });
  const [filter, setFilter] = useState<TaskboardFilter>(EMPTY_TASKBOARD_FILTER);
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
    void loadSchedulerPaused();
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

  useEffect(() => {
    const delayedTimers: number[] = [];

    const syncWorkItem = (workItemId: string, { itemChanged }: { itemChanged: boolean }) => {
      if (itemChanged) itemChangedRef.current.add(workItemId);
      const seq = (syncSeqRef.current.get(workItemId) ?? 0) + 1;
      syncSeqRef.current.set(workItemId, seq);
      void workItemService
        .getById(workItemId)
        .then((wi) => {
          // Drop a response that a newer request has already superseded — the
          // later request reflects a fresher server state, so honoring this one
          // would revert the card to a stale status.
          if (syncSeqRef.current.get(workItemId) !== seq) return;
          if (itemChangedRef.current.delete(workItemId)) showLiveItem(wi, { countAsNew: false });
          else refreshShownItem(wi);
        })
        .catch(() => {});
    };

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

    const onSchedulerStateChanged = (message: TypedSignalRMessage<"SchedulerStateChanged">) => {
      setIsPaused(message.payload.isPaused);
    };

    on("HumanFeedbackRequired", onHumanFeedback);
    on("WorkItemStateChanged", onWorkItemStateChanged);
    on("PreviewStateChanged", onPreviewStateChanged);
    on("WorkItemRunProgressed", onRunProgressed);
    on("SchedulerStateChanged", onSchedulerStateChanged);
    on("WorkItemEditProposalsChanged", onEditProposalsChanged);

    return () => {
      off("HumanFeedbackRequired", onHumanFeedback);
      off("WorkItemStateChanged", onWorkItemStateChanged);
      off("PreviewStateChanged", onPreviewStateChanged);
      off("WorkItemRunProgressed", onRunProgressed);
      off("SchedulerStateChanged", onSchedulerStateChanged);
      off("WorkItemEditProposalsChanged", onEditProposalsChanged);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [on, off]);

  // Reload the board whenever the hub transitions back into "connected". The
  // first connect is skipped because the board's first pages are already
  // loading; every later transition follows a dropout during which the
  // work-item hub buffered nothing, so reloading what each column shows is the
  // only way to recover missed events. The scheduler paused state and the tags
  // in use are re-synced for the same reason.
  useEffect(() => {
    if (connectionState !== "connected") return;
    if (!hasConnectedRef.current) {
      hasConnectedRef.current = true;
      return;
    }
    reloadAll({ windowed: true });
    void loadSchedulerPaused();
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

  const loadSchedulerPaused = async () => {
    try {
      const setting = await settingsService.get(SchedulerSettingKeys.IsPaused);
      setIsPaused(setting.value === "true");
    } catch {
      // Leave the toggle on its last-known state if the fetch fails.
    }
  };

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

  const handleWorkItemUpdate = (updated: WorkItem) => {
    showLiveItem(updated, { countAsNew: false });
  };

  const handleCreated = (created: WorkItem) => {
    showLiveItem(created, { countAsNew: true });
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

  const repositoryOptions = [...repositories].sort((a, b) => a.name.localeCompare(b.name));
  // A selected tag keeps its chip after its last use is gone, so it can still be unselected.
  const tagChips = [
    ...tagOptions,
    ...filter.tags.filter((tag) => !tagOptions.some((option) => sameTag(option, tag))),
  ].sort(compareTags);
  const isLoopTag = makeLoopTagMatcher(loopTemplateNames);
  const filterActive = isFilterActive(filter);

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
            placeholder="Search work items..."
            aria-label="Search work items"
            value={filter.search}
            onChange={(e) => setFilter((prev) => ({ ...prev, search: e.target.value }))}
          />
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
        </div>
        <label
          className={`scheduler-pause-toggle${isPaused ? "" : " is-running"}`}
          title="Toggle the scheduler — when paused, Ready items stay queued until resumed."
        >
          <input
            type="checkbox"
            className="scheduler-pause-toggle-input"
            checked={!isPaused}
            disabled={pauseBusy}
            aria-label="Scheduler running"
            onChange={async (e) => {
              const running = e.target.checked;
              const next = !running;
              setPauseBusy(true);
              try {
                await settingsService.put(SchedulerSettingKeys.IsPaused, next ? "true" : "false");
                setIsPaused(next);
              } catch (err) {
                setErrorText(errorMessage(err, "Failed to update scheduler state."));
              } finally {
                setPauseBusy(false);
              }
            }}
          />
          <span className="scheduler-pause-toggle-track" aria-hidden="true">
            <span className="scheduler-pause-toggle-thumb" />
          </span>
          <span className="scheduler-pause-toggle-label">{isPaused ? "Paused" : "Running"}</span>
        </label>
      </div>
      <div role="status" aria-live="polite" className="taskboard-live-region">
        {announcement}
      </div>
      <div className="taskboard">
        {WORK_ITEM_STATUSES.map((status) => {
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
              onAddItem={status.value === "Backlog" ? openCreateModal : undefined}
              loopTemplateNames={loopTemplateNames}
              onLoadMore={() => loadMore(status.value as WorkItemStatus)}
              loadingMore={column.loadingMore}
            />
          );
        })}
      </div>
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
          padding: 0.6rem 0.75rem;
          background-color: #23233b;
          border: 1px solid #3a3a5c;
          border-radius: 0.5rem;
        }

        /* Push the running toggle to the trailing edge of the toolbar, leaving
           the filter controls flush left. */
        .taskboard-toolbar .scheduler-pause-toggle {
          margin-left: auto;
        }

        .scheduler-pause-toggle {
          display: inline-flex;
          align-items: center;
          gap: 0.5rem;
          font-size: 0.9rem;
          color: var(--text-secondary, #555);
          cursor: pointer;
          user-select: none;
        }

        .scheduler-pause-toggle-input {
          position: absolute;
          width: 1px;
          height: 1px;
          padding: 0;
          margin: -1px;
          overflow: hidden;
          clip: rect(0 0 0 0);
          white-space: nowrap;
          border: 0;
        }

        .scheduler-pause-toggle-track {
          position: relative;
          display: inline-block;
          width: 36px;
          height: 20px;
          border-radius: 999px;
          background: var(--border-color, #ccc);
          transition: background 0.2s ease;
          flex-shrink: 0;
        }

        .scheduler-pause-toggle.is-running .scheduler-pause-toggle-track {
          background: var(--success-color, #2e9e5b);
        }

        .scheduler-pause-toggle-thumb {
          position: absolute;
          top: 2px;
          left: 2px;
          width: 16px;
          height: 16px;
          border-radius: 50%;
          background: #fff;
          box-shadow: 0 1px 2px rgba(0, 0, 0, 0.3);
          transition: transform 0.2s ease;
        }

        .scheduler-pause-toggle.is-running .scheduler-pause-toggle-thumb {
          transform: translateX(16px);
        }

        .scheduler-pause-toggle-input:focus-visible + .scheduler-pause-toggle-track {
          outline: 2px solid var(--focus-color, #3b82f6);
          outline-offset: 2px;
        }

        .scheduler-pause-toggle-input:disabled ~ .scheduler-pause-toggle-track,
        .scheduler-pause-toggle-input:disabled ~ .scheduler-pause-toggle-label {
          opacity: 0.5;
        }

        .scheduler-pause-toggle-label {
          font-variant-numeric: tabular-nums;
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
          font-size: 0.7rem;
          padding: 0.2rem 0.5rem;
          background-color: #2d2d44;
          border: 1px solid #3a3a5c;
          border-radius: 999px;
          color: #a0a0b0;
          cursor: pointer;
        }

        .taskboard-filter-tag.is-active {
          background-color: #3b82f6;
          border-color: #3b82f6;
          color: #fff;
        }

        /* Loop tags carry the same purple identity here as on the cards, so the
           filter bar distinguishes them from free-form tags. Rules follow the
           base is-active rule so the active loop variant wins on equal
           specificity. */
        .taskboard-filter-tag--loop {
          background-color: #4c1d95;
          border-color: #5b21b6;
          color: #ddd6fe;
        }

        .taskboard-filter-tag--loop.is-active {
          background-color: #7c3aed;
          border-color: #7c3aed;
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

        @media (max-width: 640px) {
          .taskboard {
            flex-direction: column;
            overflow-y: auto;
          }

          .taskboard-column {
            min-height: 200px;
          }
        }
      `}</style>
    </div>
  );
}
