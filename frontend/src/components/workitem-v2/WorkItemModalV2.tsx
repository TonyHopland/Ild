import { useState, useEffect, useCallback } from "react";
import "./workitem-v2.css";
import { WorkItem, WorkItemStatus } from "../../types";
import { workItemService } from "../../services/auth";
import useRenderedPrompt from "../../hooks/useRenderedPrompt";
import { parseConversation } from "../../utils/workItemJson";
import LiveStream from "../NodeTimeline/LiveStream";
import ConfirmModal from "../ConfirmModal";
import LoopRunTerminal from "../LoopRunTerminal";
import { useWorkItemDetail } from "./useWorkItemDetail";
import {
  FeedbackBanner,
  ConversationPanel,
  PreviewPanel,
  MetaPanel,
  DescriptionPanel,
} from "./panels";
import RunsPanel from "./RunsPanel";
import EditPanel from "./EditPanel";

type TabId = "overview" | "runs" | "conversation" | "preview";

interface WorkItemModalV2Props {
  workItem: WorkItem;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
}

/**
 * Near-fullscreen work item detail dialog: a horizontal tab bar (Overview,
 * Runs, Conversation, Preview) over the full width, with run history shown
 * inline rather than on a separate page. New items are still created through
 * the classic modal — this dialog is the detail/edit view for existing items.
 */
export default function WorkItemModalV2({
  workItem,
  onClose,
  onSave,
  onDelete,
}: WorkItemModalV2Props) {
  const detail = useWorkItemDetail(workItem, onSave);
  // The detail dialog always opens on Overview, regardless of status or which
  // tab was last viewed on a previously-opened item.
  const [activeTab, setActiveTab] = useState<TabId>("overview");
  const [editMode, setEditMode] = useState(false);
  const [editDirty, setEditDirty] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showCloseConfirm, setShowCloseConfirm] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [showTerminal, setShowTerminal] = useState(false);

  const exitEdit = useCallback(() => {
    setEditMode(false);
    setEditDirty(false);
  }, []);

  // Closing must not silently discard an in-progress edit or typed feedback.
  const hasUnsavedChanges = (editMode && editDirty) || detail.feedbackInput.trim().length > 0;

  const requestClose = useCallback(() => {
    if (hasUnsavedChanges) {
      setShowCloseConfirm(true);
    } else {
      onClose();
    }
  }, [hasUnsavedChanges, onClose]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") requestClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [requestClose]);

  useEffect(() => {
    setEditMode(false);
    setEditDirty(false);
    setActiveTab("overview");
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
    { id: "preview", label: `Preview${detail.preview?.state === "running" ? " ●" : ""}` },
  ];

  const showCleanupButtons =
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason !== "Human Input Needed" &&
    workItem.humanFeedbackReason !== "PR Awaiting Merge";

  const handleTabKeyDown = (e: React.KeyboardEvent, index: number) => {
    let nextIndex: number | null = null;
    if (e.key === "ArrowRight") nextIndex = (index + 1) % tabs.length;
    else if (e.key === "ArrowLeft") nextIndex = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === "Home") nextIndex = 0;
    else if (e.key === "End") nextIndex = tabs.length - 1;
    if (nextIndex === null) return;
    e.preventDefault();
    const next = tabs[nextIndex];
    setActiveTab(next.id);
    document.getElementById(`wiv2-tab-${next.id}`)?.focus();
  };

  // All panels stay mounted; visibility is toggled so selected run, expanded
  // nodes and scroll position survive tab switches.
  const renderPanels = () => (
    <>
      <section
        role="tabpanel"
        id="wiv2-panel-overview"
        aria-labelledby="wiv2-tab-overview"
        className="wiv2-tabpanel"
        hidden={activeTab !== "overview"}
      >
        <div className="wiv2-overview wiv2-overview-cols">
          <div className="wiv2-overview-main">
            {detail.shouldStream && <LiveStream text={detail.progressText} />}
            <span className="detail-label">Description</span>
            <DescriptionPanel workItem={workItem} />
          </div>
          <aside className="wiv2-overview-aside">
            <MetaPanel workItem={workItem} detail={detail} />
          </aside>
        </div>
      </section>
      <section
        role="tabpanel"
        id="wiv2-panel-runs"
        aria-labelledby="wiv2-tab-runs"
        className="wiv2-tabpanel"
        hidden={activeTab !== "runs"}
      >
        <RunsPanel
          workItem={workItem}
          runs={detail.runs}
          progressText={detail.progressText}
          onRunsChanged={detail.refreshRuns}
          onHalt={detail.handleHalt}
          onResumeSteer={detail.handleResumeSteer}
          onCleanupDone={detail.handleCleanupDone}
          onCleanupBacklog={detail.handleCleanupBacklog}
        />
      </section>
      <section
        role="tabpanel"
        id="wiv2-panel-conversation"
        aria-labelledby="wiv2-tab-conversation"
        className="wiv2-tabpanel"
        hidden={activeTab !== "conversation"}
      >
        <ConversationPanel workItem={workItem} />
      </section>
      <section
        role="tabpanel"
        id="wiv2-panel-preview"
        aria-labelledby="wiv2-tab-preview"
        className="wiv2-tabpanel"
        hidden={activeTab !== "preview"}
      >
        <PreviewPanel workItem={workItem} detail={detail} />
      </section>
    </>
  );

  const tabBar = (
    <div className="wiv2-tabbar" role="tablist" aria-label="Work item sections">
      {tabs.map((tab, i) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          id={`wiv2-tab-${tab.id}`}
          aria-selected={activeTab === tab.id}
          aria-controls={`wiv2-panel-${tab.id}`}
          tabIndex={activeTab === tab.id ? 0 : -1}
          className={`wiv2-tab ${activeTab === tab.id ? "wiv2-tab-active" : ""}`}
          onClick={() => setActiveTab(tab.id)}
          onKeyDown={(e) => handleTabKeyDown(e, i)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );

  return (
    <>
      <div className="modal-overlay" onMouseDown={requestClose}>
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
              <button className="modal-close" onClick={requestClose} aria-label="Close">
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
                onDone={exitEdit}
                onDirtyChange={setEditDirty}
              />
            </div>
          ) : (
            <>
              <FeedbackBanner workItem={workItem} detail={detail} prompt={feedbackPrompt} />
              <div className="wiv2-body wiv2-body-top">
                {tabBar}
                <div className="wiv2-content">{renderPanels()}</div>
              </div>
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
                <button type="button" className="btn btn-secondary" onClick={requestClose}>
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
      <ConfirmModal
        isOpen={showCloseConfirm}
        title="Discard unsaved changes?"
        message="You have unsaved changes. Close the dialog and discard them?"
        confirmText="Discard"
        onConfirm={() => {
          setShowCloseConfirm(false);
          onClose();
        }}
        onCancel={() => setShowCloseConfirm(false)}
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
