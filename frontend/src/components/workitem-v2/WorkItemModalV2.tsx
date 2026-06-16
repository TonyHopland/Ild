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
import HaltSteerControls from "./HaltSteerControls";
import EditPanel from "./EditPanel";
import FilesPanel from "./FilesPanel";

type TabId = "overview" | "action" | "runs" | "conversation" | "files" | "preview" | "terminal";

interface WorkItemModalV2Props {
  /** The item to show, or null to render the new-item creation form. */
  workItem: WorkItem | null;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
}

/**
 * Near-fullscreen work item detail dialog: a horizontal tab bar (Overview,
 * Action, Runs, Conversation, Files, Preview) over the full width, with run
 * history shown inline rather than on a separate page. The Action tab holds the
 * halt-and-steer control, the live progress stream, and the human-feedback pane
 * — all space-hungry — and flags itself with an indicator while the item waits
 * on a human. With a null
 * workItem the dialog drops the tabs and shows the creation form instead, so a
 * single dialog covers both creating and viewing/editing work items.
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

  // Closing must not silently discard an in-progress edit, the create form, or
  // typed feedback. The create form (null workItem) is always an open form, so
  // its dirty flag counts the same as an edit's.
  const isCreate = workItem === null;
  const hasUnsavedChanges =
    ((editMode || isCreate) && editDirty) || detail.feedbackInput.trim().length > 0;

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
    // A terminal session belongs to the item it was opened on; switching items
    // must tear it down rather than leak the previous worktree's shell.
    setShowTerminal(false);
    // Only reset when switching to a different item — status changes on the
    // same item must not yank the user away from the tab they are reading.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workItem?.id]);

  // The terminal connects to the run's worktree, so the tab only exists once a
  // run has created one. Gating on the worktree (not the status) keeps the tab —
  // and any live session — stable as the item moves between statuses.
  const canUseTerminal = Boolean(workItem?.currentLoopRunId && workItem?.worktreePath);

  // If the worktree is reclaimed while the Terminal tab is open, the tab (and its
  // panel) disappear — fall back to Overview so the content area is never blank.
  useEffect(() => {
    if (activeTab === "terminal" && !canUseTerminal) setActiveTab("overview");
  }, [activeTab, canUseTerminal]);

  // Rendered prompt for the currently-suspended Human/PR node, same as classic.
  const runId = workItem?.currentLoopRunId ?? undefined;
  const humanPrompt = useRenderedPrompt(
    workItem?.status === WorkItemStatus.HumanFeedback &&
      workItem?.humanFeedbackReason === "Human Input Needed"
      ? runId
      : undefined,
    "NodeStarted",
  );
  const prPrompt = useRenderedPrompt(
    workItem?.status === WorkItemStatus.HumanFeedback &&
      workItem?.humanFeedbackReason === "PR Awaiting Merge"
      ? runId
      : undefined,
    "NodeStarted",
  );
  const feedbackPrompt =
    workItem?.humanFeedbackReason === "PR Awaiting Merge" ? prPrompt : humanPrompt;

  const handleDeleteConfirm = async () => {
    if (!workItem) return;
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

  // A new item has no runs, conversation or live state, so it skips the tabbed
  // layout entirely and shows only the creation form. Placed after every hook so
  // the detail view below can treat workItem as non-null.
  if (!workItem) {
    return (
      <>
        <div className="modal-overlay" onMouseDown={requestClose}>
          <div
            className="wiv2-dialog"
            onMouseDown={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-label="New work item"
          >
            <div className="wiv2-header">
              <div className="wiv2-header-copy">
                <div className="wiv2-header-title-row">
                  <h2>New Work Item</h2>
                </div>
              </div>
              <div className="wiv2-header-actions">
                <button className="modal-close" onClick={requestClose} aria-label="Close">
                  &times;
                </button>
              </div>
            </div>
            <div className="wiv2-body wiv2-body-edit">
              <EditPanel
                workItem={null}
                detail={detail}
                onSave={onSave}
                onDone={onClose}
                onDirtyChange={setEditDirty}
              />
            </div>
          </div>
        </div>
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
      </>
    );
  }

  const conversationCount = parseConversation(workItem).length;

  // "Action required" mirrors the FeedbackBanner's render condition: the item is
  // waiting on a human. The Action tab flags this with the same ● indicator the
  // Preview tab uses for a running preview.
  const actionRequired =
    workItem.status === WorkItemStatus.HumanFeedback && !!workItem.humanFeedbackReason;

  const tabs: { id: TabId; label: string }[] = [
    { id: "overview", label: "Overview" },
    { id: "action", label: `Action${actionRequired ? " ●" : ""}` },
    { id: "runs", label: `Runs${detail.runs.length > 0 ? ` (${detail.runs.length})` : ""}` },
    {
      id: "conversation",
      label: `Conversation${conversationCount > 0 ? ` (${conversationCount})` : ""}`,
    },
    { id: "files", label: "Files" },
    { id: "preview", label: `Preview${detail.preview?.state === "running" ? " ●" : ""}` },
    ...(canUseTerminal
      ? [{ id: "terminal" as TabId, label: `Terminal${showTerminal ? " ●" : ""}` }]
      : []),
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
        id="wiv2-panel-action"
        aria-labelledby="wiv2-tab-action"
        className="wiv2-tabpanel"
        hidden={activeTab !== "action"}
      >
        <div className="wiv2-action">
          <HaltSteerControls
            run={detail.currentRun}
            workItemStatus={workItem.status}
            onHalt={detail.handleHalt}
            onResumeSteer={detail.handleResumeSteer}
            onCleanupDone={detail.handleCleanupDone}
            onCleanupBacklog={detail.handleCleanupBacklog}
          />
          {detail.shouldStream && <LiveStream text={detail.progressText} />}
          <FeedbackBanner workItem={workItem} detail={detail} prompt={feedbackPrompt} />
          {!detail.shouldStream && !actionRequired && (
            <div className="wiv2-empty">No action required.</div>
          )}
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
        id="wiv2-panel-files"
        aria-labelledby="wiv2-tab-files"
        className="wiv2-tabpanel wiv2-tabpanel-files"
        hidden={activeTab !== "files"}
      >
        <FilesPanel workItem={workItem} />
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
      {canUseTerminal && workItem.currentLoopRunId && (
        <section
          role="tabpanel"
          id="wiv2-panel-terminal"
          aria-labelledby="wiv2-tab-terminal"
          className="wiv2-tabpanel wiv2-tabpanel-terminal"
          hidden={activeTab !== "terminal"}
        >
          {/* The session stays mounted while the panel is merely hidden, so
              switching tabs never tears down the shell — only the Close button
              (or closing the dialog) does. */}
          {showTerminal ? (
            <LoopRunTerminal
              loopRunId={workItem.currentLoopRunId}
              title={`#${workItem.id} — ${workItem.title}`}
              embedded
              onClose={() => setShowTerminal(false)}
            />
          ) : (
            <div className="wiv2-terminal-empty">
              <p>Open a shell in {workItem.worktreePath}</p>
              <button
                type="button"
                className="btn btn-sm btn-secondary"
                onClick={() => setShowTerminal(true)}
              >
                Open Terminal
              </button>
            </div>
          )}
        </section>
      )}
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
                onRequestDelete={() => setShowDeleteConfirm(true)}
              />
            </div>
          ) : (
            <>
              <div className="wiv2-body wiv2-body-top">
                {tabBar}
                <div className="wiv2-content">{renderPanels()}</div>
              </div>
              <div className="wiv2-footer">
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
                <button
                  type="button"
                  className="btn btn-sm btn-edit"
                  onClick={() => setEditMode(true)}
                >
                  Edit
                </button>
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
    </>
  );
}
