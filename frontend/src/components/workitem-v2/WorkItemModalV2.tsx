import { useState, useEffect } from "react";
import "./workitem-v2.css";
import { WorkItem, WorkItemStatus } from "../../types";
import { workItemService } from "../../services/auth";
import useRenderedPrompt from "../../hooks/useRenderedPrompt";
import { parseConversation } from "../../utils/workItemJson";
import LiveStream from "../NodeTimeline/LiveStream";
import ConfirmModal from "../ConfirmModal";
import LoopRunTerminal from "../LoopRunTerminal";
import { useWorkItemDetail } from "./useWorkItemDetail";
import { FeedbackBanner, ConversationPanel, QAPanel, MetaPanel, DescriptionPanel } from "./panels";
import RunsPanel from "./RunsPanel";
import EditPanel from "./EditPanel";

export type WorkItemUiVariant = "classic" | "tabs" | "split" | "rail";

export const WORK_ITEM_UI_VARIANT_KEY = "ild_workitem_ui_variant";

export const WORK_ITEM_UI_VARIANTS: { value: WorkItemUiVariant; label: string }[] = [
  { value: "classic", label: "Classic" },
  { value: "tabs", label: "Mockup A — Full tabs" },
  { value: "split", label: "Mockup B — Tabs + sidebar" },
  { value: "rail", label: "Mockup C — Side rail" },
];

type TabId = "overview" | "runs" | "conversation" | "qa";

interface WorkItemModalV2Props {
  workItem: WorkItem;
  variant: Exclude<WorkItemUiVariant, "classic">;
  onVariantChange: (variant: WorkItemUiVariant) => void;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
}

/**
 * Near-fullscreen work item dialog mockups. Three layout variants share the
 * same data hook and panels so only the navigation chrome differs:
 *  - "tabs":  horizontal tab bar, content uses the full width
 *  - "split": horizontal tab bar plus an always-visible metadata sidebar
 *  - "rail":  vertical navigation rail on the left
 */
export default function WorkItemModalV2({
  workItem,
  variant,
  onVariantChange,
  onClose,
  onSave,
  onDelete,
}: WorkItemModalV2Props) {
  const detail = useWorkItemDetail(workItem, onSave);
  const [activeTab, setActiveTab] = useState<TabId>(() =>
    workItem.status === WorkItemStatus.Running ? "runs" : "overview",
  );
  const [editMode, setEditMode] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [showTerminal, setShowTerminal] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  useEffect(() => {
    setEditMode(false);
    setActiveTab(workItem.status === WorkItemStatus.Running ? "runs" : "overview");
    // Only reset when switching to a different item — status changes on the
    // same item must not yank the user away from the tab they are reading.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workItem.id]);

  // Rendered prompt for the currently-suspended Human/PR node, same as classic.
  const runId = workItem.currentLoopRunId ?? undefined;
  const humanPrompt = useRenderedPrompt(
    workItem.status === WorkItemStatus.HumanFeedback &&
      workItem.humanFeedbackReason === "Human Input Needed"
      ? runId
      : undefined,
    "NodeStarted",
  );
  const prPrompt = useRenderedPrompt(
    workItem.status === WorkItemStatus.HumanFeedback &&
      workItem.humanFeedbackReason === "PR Awaiting Merge"
      ? runId
      : undefined,
    "NodeStarted",
  );
  const feedbackPrompt =
    workItem.humanFeedbackReason === "PR Awaiting Merge" ? prPrompt : humanPrompt;

  const handleDeleteConfirm = async () => {
    try {
      await workItemService.delete(workItem.id);
      onDelete?.(workItem.id);
      onClose();
    } catch (error) {
      setDeleteError((error as { message?: string })?.message ?? "Failed to delete work item");
    } finally {
      setShowDeleteConfirm(false);
    }
  };

  const conversationCount = parseConversation(workItem).length;

  const tabs: { id: TabId; label: string }[] = [
    { id: "overview", label: "Overview" },
    { id: "runs", label: `Runs${detail.runs.length > 0 ? ` (${detail.runs.length})` : ""}` },
    {
      id: "conversation",
      label: `Conversation${conversationCount > 0 ? ` (${conversationCount})` : ""}`,
    },
    { id: "qa", label: `QA${detail.preview?.state === "running" ? " ●" : ""}` },
  ];

  const showCleanupButtons =
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason !== "Human Input Needed" &&
    workItem.humanFeedbackReason !== "PR Awaiting Merge";

  const renderTabContent = () => {
    switch (activeTab) {
      case "overview":
        return (
          <div
            className={variant === "split" ? "wiv2-overview" : "wiv2-overview wiv2-overview-cols"}
          >
            <div className="wiv2-overview-main">
              {detail.shouldStream && <LiveStream text={detail.progressText} />}
              <span className="detail-label">Description</span>
              <DescriptionPanel workItem={workItem} />
            </div>
            {variant !== "split" && (
              <aside className="wiv2-overview-aside">
                <MetaPanel workItem={workItem} detail={detail} />
              </aside>
            )}
          </div>
        );
      case "runs":
        return (
          <RunsPanel workItem={workItem} runs={detail.runs} progressText={detail.progressText} />
        );
      case "conversation":
        return <ConversationPanel workItem={workItem} />;
      case "qa":
        return <QAPanel workItem={workItem} detail={detail} />;
    }
  };

  const tabBar = (orientation: "horizontal" | "vertical") => (
    <div
      className={orientation === "vertical" ? "wiv2-rail" : "wiv2-tabbar"}
      role="tablist"
      aria-orientation={orientation}
    >
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          aria-selected={activeTab === tab.id}
          className={`wiv2-tab ${activeTab === tab.id ? "wiv2-tab-active" : ""}`}
          onClick={() => setActiveTab(tab.id)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );

  return (
    <>
      <div className="modal-overlay" onMouseDown={onClose}>
        <div
          className="wiv2-dialog"
          onMouseDown={(e) => e.stopPropagation()}
          role="dialog"
          aria-modal="true"
          aria-label="Work item details"
        >
          <div className="wiv2-header">
            <div className="wiv2-header-copy">
              <div className="modal-work-item-id">#{workItem.id}</div>
              <div className="wiv2-header-title-row">
                <h2>{workItem.title}</h2>
                <span className={`status-badge status-${workItem.status.toLowerCase()}`}>
                  {workItem.status}
                </span>
                <span className="wiv2-priority">{workItem.priority}</span>
              </div>
            </div>
            <div className="wiv2-header-actions">
              <label className="wiv2-variant-picker">
                UI
                <select
                  value={variant}
                  onChange={(e) => onVariantChange(e.target.value as WorkItemUiVariant)}
                >
                  {WORK_ITEM_UI_VARIANTS.map((v) => (
                    <option key={v.value} value={v.value}>
                      {v.label}
                    </option>
                  ))}
                </select>
              </label>
              {workItem.status === WorkItemStatus.HumanFeedback &&
                workItem.currentLoopRunId &&
                workItem.worktreePath && (
                  <button
                    type="button"
                    className="btn btn-sm btn-secondary"
                    onClick={() => setShowTerminal(true)}
                    title={`Open a shell in ${workItem.worktreePath}`}
                  >
                    Open Terminal
                  </button>
                )}
              {!editMode && (
                <button
                  type="button"
                  className="btn btn-sm btn-edit"
                  onClick={() => setEditMode(true)}
                >
                  Edit
                </button>
              )}
              <button className="modal-close" onClick={onClose} aria-label="Close">
                &times;
              </button>
            </div>
          </div>

          {editMode ? (
            <div className="wiv2-body wiv2-body-edit">
              <EditPanel
                workItem={workItem}
                detail={detail}
                onSave={onSave}
                onDone={() => setEditMode(false)}
              />
            </div>
          ) : (
            <>
              <FeedbackBanner workItem={workItem} detail={detail} prompt={feedbackPrompt} />
              {variant === "rail" ? (
                <div className="wiv2-body wiv2-body-rail">
                  {tabBar("vertical")}
                  <div className="wiv2-content">{renderTabContent()}</div>
                </div>
              ) : (
                <div className="wiv2-body wiv2-body-top">
                  {tabBar("horizontal")}
                  <div className="wiv2-content-row">
                    <div className="wiv2-content">{renderTabContent()}</div>
                    {variant === "split" && (
                      <aside className="wiv2-sidebar">
                        <MetaPanel workItem={workItem} detail={detail} />
                      </aside>
                    )}
                  </div>
                </div>
              )}
              <div className="wiv2-footer">
                <button
                  type="button"
                  className="btn btn-sm btn-danger"
                  onClick={() => setShowDeleteConfirm(true)}
                >
                  Delete
                </button>
                {showCleanupButtons && (
                  <>
                    <button
                      type="button"
                      className="btn btn-sm btn-warning"
                      onClick={() => void detail.handleCleanupDone()}
                    >
                      Cleanup -&gt; Done
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => void detail.handleCleanupBacklog()}
                    >
                      Cleanup -&gt; Backlog
                    </button>
                  </>
                )}
                <span className="wiv2-footer-spacer" />
                {workItem.prUrl && (
                  <button
                    type="button"
                    className="btn btn-success"
                    onClick={() => void detail.handleMarkMerged()}
                  >
                    Mark Merged
                  </button>
                )}
                <button type="button" className="btn btn-secondary" onClick={onClose}>
                  Close
                </button>
              </div>
            </>
          )}
        </div>
      </div>
      {deleteError && (
        <div className="delete-error-banner" role="alert">
          {deleteError}
          <button type="button" className="delete-error-close" onClick={() => setDeleteError(null)}>
            ×
          </button>
        </div>
      )}
      <ConfirmModal
        isOpen={showDeleteConfirm}
        title="Delete Work Item"
        message={`Are you sure you want to delete "${workItem.title}"?`}
        onConfirm={handleDeleteConfirm}
        onCancel={() => setShowDeleteConfirm(false)}
      />
      {showTerminal && workItem.currentLoopRunId && (
        <LoopRunTerminal
          loopRunId={workItem.currentLoopRunId}
          title={`#${workItem.id} — ${workItem.title}`}
          onClose={() => setShowTerminal(false)}
        />
      )}
    </>
  );
}
