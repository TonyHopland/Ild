import { useState, useEffect } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { workItemService } from "../services/auth";
import TaskboardColumn from "../components/TaskboardColumn";
import WorkItemModal from "../components/WorkItemModal";
import { useSignalR } from "../hooks/useSignalR";
import { WORK_ITEM_STATUSES } from "../utils/constants";

export default function Taskboard() {
  const [workItems, setWorkItems] = useState<WorkItem[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editingItem, setEditingItem] = useState<WorkItem | null>(null);
  const { on } = useSignalR();

  useEffect(() => {
    void loadWorkItems();
  }, []);

  useEffect(() => {
    on("HumanFeedbackRequired" as any, async (message: any) => {
      const { workItemId, reason } = message.payload as { workItemId: string; reason: string };
      setWorkItems((prev) =>
        prev.map((item) =>
          item.id === workItemId
            ? { ...item, status: WorkItemStatus.HumanFeedback, humanFeedbackReason: reason }
            : item,
        ),
      );

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
    });

    on("work_item_updated" as any, async (message: any) => {
      const updated = message.payload as WorkItem;
      setWorkItems((prev) => prev.map((item) => (item.id === updated.id ? updated : item)));
    });

    on("work_item_created" as any, async (message: any) => {
      const newItem = message.payload as WorkItem;
      setWorkItems((prev) => [...prev, newItem]);
    });

    on("work_item_deleted" as any, async (message: any) => {
      const deletedId = (message.payload as { id: string }).id;
      setWorkItems((prev) => prev.filter((item) => item.id !== deletedId));
    });
  }, [on]);

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
    setEditingItem(null);
    setModalOpen(true);
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
        <button className="btn btn-primary" onClick={openCreateModal}>
          + New Item
        </button>
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
            />
          );
        })}
      </div>
      <WorkItemModal
        workItem={editingItem}
        isOpen={modalOpen}
        onClose={() => setModalOpen(false)}
        onSave={handleSave}
      />
      <style>{`
        .taskboard-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .taskboard {
          display: flex;
          gap: 1rem;
          overflow-x: auto;
          padding-bottom: 1rem;
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
