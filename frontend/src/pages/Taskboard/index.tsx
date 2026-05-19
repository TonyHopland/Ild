import { useState, useEffect, useRef } from "react";
import { WorkItem, WorkItemStatus } from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import { workItemService, settingsService, SchedulerSettingKeys } from "../../services/auth";
import TaskboardColumn from "../../components/TaskboardColumn";
import WorkItemModal from "../../components/WorkItemModal";
import ErrorBanner from "../../components/ErrorBanner";
import { useSignalR } from "../../hooks/useSignalR";
import { WORK_ITEM_STATUSES } from "../../utils/constants";

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
}

export default function Taskboard() {
  const [workItems, setWorkItems] = useState<WorkItem[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editingItem, setEditingItem] = useState<WorkItem | null>(null);
  const editingItemIdRef = useRef<string | null>(null);
  editingItemIdRef.current = editingItem?.id ?? null;
  const [errorText, setErrorText] = useState("");
  const [isPaused, setIsPaused] = useState(false);
  const [pauseBusy, setPauseBusy] = useState(false);
  const { on, off } = useSignalR();

  useEffect(() => {
    void loadWorkItems();
    void settingsService
      .get(SchedulerSettingKeys.IsPaused)
      .then((s) => setIsPaused(s.value === "true"))
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
          if (editingItemIdRef.current === wi.id) {
            setEditingItem(wi);
          }
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
        return prev.map((item) => (item.id === workItemId ? { ...item, status: newStatus } : item));
      });
      syncWorkItem(workItemId);
      delayedTimers.push(setTimeout(() => syncWorkItem(workItemId), 500));
    };

    const onPreviewStateChanged = (message: TypedSignalRMessage<"PreviewStateChanged">) => {
      syncWorkItem(message.payload.workItemId);
    };

    const onSchedulerStateChanged = (message: TypedSignalRMessage<"SchedulerStateChanged">) => {
      setIsPaused(message.payload.isPaused);
    };

    on("HumanFeedbackRequired", onHumanFeedback);
    on("WorkItemStateChanged", onWorkItemStateChanged);
    on("PreviewStateChanged", onPreviewStateChanged);
    on("SchedulerStateChanged", onSchedulerStateChanged);

    return () => {
      off("HumanFeedbackRequired", onHumanFeedback);
      off("WorkItemStateChanged", onWorkItemStateChanged);
      off("PreviewStateChanged", onPreviewStateChanged);
      off("SchedulerStateChanged", onSchedulerStateChanged);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [on, off]);

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
    if (editingItem?.id === saved.id) {
      setEditingItem(saved);
    }
  };

  const openCreateModal = () => {
    setEditingItem(null);
    setModalOpen(true);
  };

  const handleCardClick = (wi: WorkItem) => {
    setEditingItem(wi);
    setModalOpen(true);
  };

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

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading taskboard...</p>
      </div>
    );
  }

  return (
    <div className="page-container">
      <div className="taskboard-header">
        <h1 className="page-title">Taskboard</h1>
        <div className="taskboard-header-actions">
          <label
            className="scheduler-pause-toggle"
            title="Pause the scheduler — Ready items stay queued until resumed."
          >
            <input
              type="checkbox"
              checked={isPaused}
              disabled={pauseBusy}
              aria-label="Pause scheduler"
              onChange={async (e) => {
                const next = e.target.checked;
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
            {isPaused ? "Paused" : "Running"}
          </label>
          <button className="btn btn-primary" onClick={openCreateModal}>
            + New Item
          </button>
        </div>
      </div>
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />
      <div role="status" aria-live="polite" className="taskboard-live-region">
        {announcement}
      </div>
      <div className="taskboard">
        {WORK_ITEM_STATUSES.map((status) => {
          const items = workItems.filter((wi) => wi.status === status.value);
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
            />
          );
        })}
      </div>
      <WorkItemModal
        workItem={editingItem}
        isOpen={modalOpen}
        onClose={() => setModalOpen(false)}
        onSave={handleSave}
        onDelete={handleDeleted}
      />
      <style>{`
        .taskboard-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .taskboard-header-actions {
          display: flex;
          align-items: center;
          gap: 1rem;
        }

        .scheduler-pause-toggle {
          display: inline-flex;
          align-items: center;
          gap: 0.4rem;
          font-size: 0.9rem;
          color: var(--text-secondary, #555);
          cursor: pointer;
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

        .taskboard {
          display: flex;
          gap: 1rem;
          overflow-x: auto;
          padding-bottom: 1rem;
        }

        @media (max-width: 640px) {
          .taskboard {
            flex-direction: column;
          }

          .taskboard-column {
            max-height: none;
            min-height: 200px;
          }
        }

        .btn-primary {
          background-color: #6366f1;
          color: #fff;
          padding: 0.5rem 1rem;
          border-radius: 0.375rem;
          border: none;
          cursor: pointer;
          font-size: 0.875rem;
          transition: background-color 0.15s ease;
        }

        .btn-primary:hover {
          background-color: #5558e6;
        }
      `}</style>
    </div>
  );
}
