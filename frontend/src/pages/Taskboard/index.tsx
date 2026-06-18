import { useState, useEffect } from "react";
import { useNavigate, useParams } from "react-router-dom";
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
import CombinedPreviewDrawer from "../../components/CombinedPreviewDrawer";
import ErrorBanner from "../../components/ErrorBanner";
import { useSignalR } from "../../hooks/useSignalR";
import { WORK_ITEM_STATUSES, TASKBOARD_PAGE_SIZE } from "../../utils/constants";
import { normalizeWorkItemStatus } from "../../utils/workItemStatus";
import { makeLoopTagMatcher } from "../../utils/workItemJson";
import {
  EMPTY_TASKBOARD_FILTER,
  collectRepositoryOptions,
  collectTags,
  filterWorkItems,
  isFilterActive,
  type TaskboardFilter,
} from "../../utils/taskboardFilter";

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
}

export default function Taskboard() {
  const navigate = useNavigate();
  // The id in the URL is the source of truth for which item's detail dialog is
  // open, so a work item can be linked to directly (e.g. /taskboard/<id>).
  const { workItemId: openWorkItemId } = useParams<{ workItemId?: string }>();
  const [workItems, setWorkItems] = useState<WorkItem[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [editingItem, setEditingItem] = useState<WorkItem | null>(null);
  const [errorText, setErrorText] = useState("");
  const [isPaused, setIsPaused] = useState(false);
  const [pauseBusy, setPauseBusy] = useState(false);
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [loopTemplateNames, setLoopTemplateNames] = useState<string[]>([]);
  const [filter, setFilter] = useState<TaskboardFilter>(EMPTY_TASKBOARD_FILTER);
  // "Preview together" multi-select: while selecting, cards toggle into the
  // selection set instead of opening; confirming opens the combined-preview
  // drawer for the chosen items.
  const [selecting, setSelecting] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [combinedPreviewOpen, setCombinedPreviewOpen] = useState(false);
  const { on, off } = useSignalR();

  useEffect(() => {
    void loadWorkItems();
    void settingsService
      .get(SchedulerSettingKeys.IsPaused)
      .then((s) => setIsPaused(s.value === "true"))
      .catch(() => {});
    void repositoryService
      .getAll()
      .then(setRepositories)
      .catch(() => {});
    void loopTemplateService
      .getAll()
      .then((templates) => setLoopTemplateNames(templates.map((t) => t.name)))
      .catch(() => {});
  }, []);

  useEffect(() => {
    const delayedTimers: number[] = [];

    const syncWorkItem = (workItemId: string) => {
      void workItemService
        .getById(workItemId)
        .then((wi) => {
          setWorkItems((items) => {
            const exists = items.some((item) => item.id === wi.id);
            if (!exists) return [...items, wi];
            return items.map((item) => (item.id === wi.id ? wi : item));
          });
        })
        .catch(() => {});
    };

    const onHumanFeedback = async (message: TypedSignalRMessage<"HumanFeedbackRequired">) => {
      const { workItemId, reason } = message.payload;
      setWorkItems((prev) =>
        prev.map((item) =>
          item.id === workItemId
            ? { ...item, status: WorkItemStatus.HumanFeedback, humanFeedbackReason: reason }
            : item,
        ),
      );
      syncWorkItem(workItemId);
      delayedTimers.push(setTimeout(() => syncWorkItem(workItemId), 500));

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
      setWorkItems((prev) => {
        const exists = prev.find((item) => item.id === workItemId);
        if (!exists) {
          // New work item — load it
          void workItemService
            .getById(workItemId)
            .then((wi) =>
              setWorkItems((items) =>
                items.some((item) => item.id === wi.id) ? items : [...items, wi],
              ),
            )
            .catch(() => {});
          return prev;
        }
        return prev.map((item) =>
          item.id === workItemId ? { ...item, status: normalizeWorkItemStatus(newStatus) } : item,
        );
      });
      syncWorkItem(workItemId);
      delayedTimers.push(setTimeout(() => syncWorkItem(workItemId), 500));
    };

    const onPreviewStateChanged = (message: TypedSignalRMessage<"PreviewStateChanged">) => {
      syncWorkItem(message.payload.workItemId);
    };

    // When a running item advances to a new node, re-sync it so its card shows
    // the current step. Node transitions don't change the work item's status, so
    // this is the only signal that keeps a running card's step fresh.
    const onRunProgressed = (message: TypedSignalRMessage<"WorkItemRunProgressed">) => {
      syncWorkItem(message.payload.workItemId);
    };

    const onSchedulerStateChanged = (message: TypedSignalRMessage<"SchedulerStateChanged">) => {
      setIsPaused(message.payload.isPaused);
    };

    on("HumanFeedbackRequired", onHumanFeedback);
    on("WorkItemStateChanged", onWorkItemStateChanged);
    on("PreviewStateChanged", onPreviewStateChanged);
    on("WorkItemRunProgressed", onRunProgressed);
    on("SchedulerStateChanged", onSchedulerStateChanged);

    return () => {
      off("HumanFeedbackRequired", onHumanFeedback);
      off("WorkItemStateChanged", onWorkItemStateChanged);
      off("PreviewStateChanged", onPreviewStateChanged);
      off("WorkItemRunProgressed", onRunProgressed);
      off("SchedulerStateChanged", onSchedulerStateChanged);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [on, off]);

  // Keep the open detail item in sync with the id in the URL. Resolving from
  // the loaded board keeps the dialog's data fresh; a direct link to an item
  // not on the board is fetched on demand, and a stale/invalid id bounces back
  // to the bare taskboard so the URL always matches what is actually open.
  useEffect(() => {
    if (!openWorkItemId) {
      setEditingItem(null);
      return;
    }
    const found = workItems.find((item) => item.id === openWorkItemId);
    if (found) {
      setEditingItem(found);
      return;
    }
    if (isLoading) return;
    let cancelled = false;
    void workItemService
      .getById(openWorkItemId)
      .then((wi) => {
        if (cancelled) return;
        setEditingItem(wi);
        setWorkItems((items) => (items.some((item) => item.id === wi.id) ? items : [...items, wi]));
      })
      .catch(() => {
        if (!cancelled) void navigate("/taskboard", { replace: true });
      });
    return () => {
      cancelled = true;
    };
  }, [openWorkItemId, workItems, isLoading, navigate]);

  const loadWorkItems = async () => {
    try {
      const items = await workItemService.getAll();
      setWorkItems(items);
    } catch (error) {
      console.error("Failed to load work items:", error);
    } finally {
      setIsLoading(false);
    }
  };

  const handleWorkItemUpdate = (updated: WorkItem) => {
    setWorkItems((prev) => prev.map((item) => (item.id === updated.id ? updated : item)));
  };

  const handleSave = (saved: WorkItem) => {
    setWorkItems((prev) => {
      const exists = prev.find((item) => item.id === saved.id);
      if (exists) {
        return prev.map((item) => (item.id === saved.id ? saved : item));
      }
      return [...prev, saved];
    });
  };

  const openCreateModal = () => {
    setCreateModalOpen(true);
  };

  const handleCardClick = (wi: WorkItem) => {
    void navigate(`/taskboard/${wi.id}`);
  };

  const toggleSelectMode = () => {
    setSelecting((on) => {
      if (on) setSelectedIds(new Set());
      return !on;
    });
  };

  const toggleSelected = (wi: WorkItem) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(wi.id)) next.delete(wi.id);
      else next.add(wi.id);
      return next;
    });
  };

  // Items currently selected, resolved against the live board so the drawer and
  // action bar always reflect real work items (and drop any that left the board).
  const selectedItems = workItems.filter((wi) => selectedIds.has(wi.id));

  const handleDeleted = (id: string) => {
    setWorkItems((prev) => prev.filter((wi) => wi.id !== id));
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
      tags: prev.tags.includes(tag) ? prev.tags.filter((t) => t !== tag) : [...prev.tags, tag],
    }));
  };

  const repositoryOptions = collectRepositoryOptions(workItems, repositories);
  const tagOptions = collectTags(workItems);
  const isLoopTag = makeLoopTagMatcher(loopTemplateNames);
  const visibleWorkItems = filterWorkItems(workItems, filter);
  const filterActive = isFilterActive(filter);

  if (isLoading) {
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
          {tagOptions.length > 0 && (
            <div className="taskboard-filter-tags" role="group" aria-label="Filter by tag">
              {tagOptions.map((tag) => {
                const active = filter.tags.includes(tag);
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
        <button
          type="button"
          className={`taskboard-select-toggle${selecting ? " is-active" : ""}`}
          aria-pressed={selecting}
          onClick={toggleSelectMode}
          title="Select multiple work items and preview them together in one integration worktree."
        >
          {selecting ? "✓ Selecting…" : "⊕ Preview together"}
        </button>
      </div>
      <div role="status" aria-live="polite" className="taskboard-live-region">
        {announcement}
      </div>
      <div className="taskboard">
        {WORK_ITEM_STATUSES.map((status) => {
          const items = visibleWorkItems.filter((wi) => wi.status === status.value);
          return (
            <TaskboardColumn
              key={status.value}
              status={status.value as WorkItemStatus}
              label={status.label}
              workItems={items}
              onWorkItemUpdate={handleWorkItemUpdate}
              onWorkItemClick={handleCardClick}
              onError={(msg) => setErrorText(msg)}
              onMoveWorkItem={handleMoveWorkItem}
              onAddItem={status.value === "Backlog" ? openCreateModal : undefined}
              loopTemplateNames={loopTemplateNames}
              selecting={selecting}
              selectedIds={selectedIds}
              onToggleSelect={toggleSelected}
              pageSize={
                status.value === "Backlog" || status.value === "Done"
                  ? TASKBOARD_PAGE_SIZE
                  : undefined
              }
            />
          );
        })}
      </div>
      {/* Existing items open in the tabbed detail dialog, keyed by the URL so the
          link matches the open item; a new item opens the same dialog with no
          work item, which renders its creation form. */}
      {editingItem && (
        <WorkItemModalV2
          workItem={editingItem}
          onClose={() => void navigate("/taskboard")}
          onSave={handleSave}
          onDelete={handleDeleted}
        />
      )}
      {createModalOpen && (
        <WorkItemModalV2
          workItem={null}
          onClose={() => setCreateModalOpen(false)}
          onSave={handleSave}
          onDelete={handleDeleted}
        />
      )}
      {selecting && selectedItems.length > 0 && (
        <div className="taskboard-action-bar" role="region" aria-label="Combined preview selection">
          <span className="taskboard-action-count">{selectedItems.length} selected</span>
          <div className="taskboard-action-chips">
            {selectedItems.map((item) => (
              <span key={item.id} className="taskboard-action-chip">
                #{item.id}
              </span>
            ))}
          </div>
          <button
            type="button"
            className="taskboard-action-clear"
            onClick={() => setSelectedIds(new Set())}
          >
            Clear
          </button>
          <button
            type="button"
            className="taskboard-action-go"
            onClick={() => setCombinedPreviewOpen(true)}
          >
            {selectedItems.length > 1
              ? `Preview ${selectedItems.length} together →`
              : "Preview together →"}
          </button>
        </div>
      )}
      {combinedPreviewOpen && (
        <CombinedPreviewDrawer
          items={selectedItems}
          onClose={() => setCombinedPreviewOpen(false)}
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
           the filter controls flush left. The select toggle follows it so both
           cluster at the right. */
        .taskboard-toolbar .scheduler-pause-toggle {
          margin-left: auto;
        }

        .taskboard-select-toggle {
          font-size: 0.82rem;
          padding: 0.4rem 0.75rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          cursor: pointer;
          white-space: nowrap;
          transition: background-color 0.15s ease, border-color 0.15s ease;
        }

        .taskboard-select-toggle:hover {
          background-color: #32324c;
        }

        .taskboard-select-toggle.is-active {
          background-color: #312e81;
          border-color: #6366f1;
          color: #c7d2fe;
        }

        /* Floating bar that summarises the current selection and launches the
           combined preview. Sits above the board, centred near the bottom. */
        .taskboard-action-bar {
          position: fixed;
          left: 50%;
          bottom: 1.25rem;
          transform: translateX(-50%);
          width: min(720px, calc(100% - 2.5rem));
          display: flex;
          align-items: center;
          gap: 0.85rem;
          padding: 0.7rem 0.9rem;
          background-color: #23233b;
          border: 1px solid #3a3a5c;
          border-radius: 0.6rem;
          box-shadow: 0 8px 30px rgba(0, 0, 0, 0.5);
          z-index: 40;
        }

        .taskboard-action-count {
          font-weight: 600;
          color: #c7d2fe;
          white-space: nowrap;
        }

        .taskboard-action-chips {
          display: flex;
          flex-wrap: wrap;
          gap: 0.3rem;
          flex: 1;
          min-width: 0;
        }

        .taskboard-action-chip {
          font-size: 0.72rem;
          background-color: #2d2d44;
          border: 1px solid #3a3a5c;
          color: #c0c0d0;
          padding: 0.12rem 0.45rem;
          border-radius: 999px;
          white-space: nowrap;
        }

        .taskboard-action-clear {
          background: none;
          border: none;
          color: #8a8ab0;
          cursor: pointer;
          font-size: 0.8rem;
          white-space: nowrap;
        }
        .taskboard-action-clear:hover {
          color: #c0c0d0;
          text-decoration: underline;
        }

        .taskboard-action-go {
          font-size: 0.82rem;
          padding: 0.45rem 0.85rem;
          background-color: #6366f1;
          border: 1px solid #6366f1;
          border-radius: 0.375rem;
          color: #fff;
          cursor: pointer;
          white-space: nowrap;
          transition: background-color 0.15s ease;
        }
        .taskboard-action-go:hover {
          background-color: #4f52d4;
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
