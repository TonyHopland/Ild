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

export type WorkItemUiVariant = "classic" | "tabs" | "split" | "rail";

export const WORK_ITEM_UI_VARIANT_KEY = "ild_workitem_ui_variant";
export const WORK_ITEM_SIDEBAR_COLLAPSED_KEY = "ild_workitem_sidebar_collapsed";

export const WORK_ITEM_UI_VARIANTS: { value: WorkItemUiVariant; label: string }[] = [
  { value: "classic", label: "Classic" },
  { value: "tabs", label: "Mockup A — Full tabs" },
  { value: "split", label: "Mockup B — Tabs + sidebar" },
  { value: "rail", label: "Mockup C — Side rail" },
];

type TabId = "overview" | "runs" | "conversation" | "preview";

interface WorkItemModalV2Props {
  workItem: WorkItem;
  variant: Exclude<WorkItemUiVariant, "classic">;
  onVariantChange: (variant: WorkItemUiVariant) => void;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
}

/** Below this width the persistent sidebar can't fit, so split folds its
 *  metadata into the Overview tab instead. Kept in sync with the CSS. */
const NARROW_QUERY = "(max-width: 900px)";

function matchesNarrow(): boolean {
  return (
    typeof window !== "undefined" &&
    typeof window.matchMedia === "function" &&
    window.matchMedia(NARROW_QUERY).matches
  );
}

/**
 * Near-fullscreen work item dialog mockups. Three layout variants share the
 * same data hook and panels so only the navigation chrome differs:
 *  - "tabs":  horizontal tab bar, content uses the full width
 *  - "split": horizontal tab bar plus an always-visible metadata sidebar
 *             (collapsible, and folded into Overview when there's no room)
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
  const [editDirty, setEditDirty] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showCloseConfirm, setShowCloseConfirm] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [showTerminal, setShowTerminal] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(
    () => localStorage.getItem(WORK_ITEM_SIDEBAR_COLLAPSED_KEY) === "true",
  );
  const [isNarrow, setIsNarrow] = useState(matchesNarrow);

  useEffect(() => {
    localStorage.setItem(WORK_ITEM_SIDEBAR_COLLAPSED_KEY, String(sidebarCollapsed));
  }, [sidebarCollapsed]);

  useEffect(() => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") return;
    const mq = window.matchMedia(NARROW_QUERY);
    const onChange = (e: MediaQueryListEvent) => setIsNarrow(e.matches);
    mq.addEventListener("change", onChange);
    setIsNarrow(mq.matches);
    return () => mq.removeEventListener("change", onChange);
  }, []);

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
    { id: "preview", label: `Preview${detail.preview?.state === "running" ? " ●" : ""}` },
  ];

  // The metadata sidebar is the defining trait of the split variant, but it
  // only fits when expanded and the viewport is wide enough. Whenever it is
  // hidden, MetaPanel is folded back into the Overview tab so nothing — Link
  // PR, dependency editing, dates — is ever unreachable.
  const sidebarShown = variant === "split" && !sidebarCollapsed && !isNarrow;
  const showOverviewMeta = !sidebarShown;
  const fullBodyEdit = editMode && variant !== "split";
  const inlineEdit = editMode && variant === "split";

  const showCleanupButtons =
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason !== "Human Input Needed" &&
    workItem.humanFeedbackReason !== "PR Awaiting Merge";

  const handleTabKeyDown = (
    e: React.KeyboardEvent,
    index: number,
    orientation: "horizontal" | "vertical",
  ) => {
    const nextKey = orientation === "horizontal" ? "ArrowRight" : "ArrowDown";
    const prevKey = orientation === "horizontal" ? "ArrowLeft" : "ArrowUp";
    let nextIndex: number | null = null;
    if (e.key === nextKey) nextIndex = (index + 1) % tabs.length;
    else if (e.key === prevKey) nextIndex = (index - 1 + tabs.length) % tabs.length;
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
        <div className={showOverviewMeta ? "wiv2-overview wiv2-overview-cols" : "wiv2-overview"}>
          <div className="wiv2-overview-main">
            {detail.shouldStream && <LiveStream text={detail.progressText} />}
            <span className="detail-label">Description</span>
            <DescriptionPanel workItem={workItem} />
          </div>
          {showOverviewMeta && (
            <aside className="wiv2-overview-aside">
              <MetaPanel workItem={workItem} detail={detail} />
            </aside>
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
        <RunsPanel workItem={workItem} runs={detail.runs} progressText={detail.progressText} />
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

  const tabBar = (orientation: "horizontal" | "vertical") => (
    <div
      className={orientation === "vertical" ? "wiv2-rail" : "wiv2-tabbar"}
      role="tablist"
      aria-orientation={orientation}
      aria-label="Work item sections"
    >
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
          onKeyDown={(e) => handleTabKeyDown(e, i, orientation)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );

  const editForm = (
    <EditPanel
      workItem={workItem}
      detail={detail}
      onSave={onSave}
      onDone={exitEdit}
      onDirtyChange={setEditDirty}
    />
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
              {variant === "split" && !isNarrow && (
                <button
                  type="button"
                  className="btn btn-sm btn-secondary"
                  onClick={() => setSidebarCollapsed((c) => !c)}
                  aria-pressed={sidebarCollapsed}
                  title={sidebarCollapsed ? "Show the details panel" : "Hide the details panel"}
                >
                  {sidebarCollapsed ? "Show panel" : "Hide panel"}
                </button>
              )}
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

          {fullBodyEdit ? (
            <div className="wiv2-body wiv2-body-edit">{editForm}</div>
          ) : (
            <>
              {!editMode && (
                <FeedbackBanner workItem={workItem} detail={detail} prompt={feedbackPrompt} />
              )}
              {variant === "rail" ? (
                <div className="wiv2-body wiv2-body-rail">
                  {tabBar("vertical")}
                  <div className="wiv2-content">{renderPanels()}</div>
                </div>
              ) : (
                <div className="wiv2-body wiv2-body-top">
                  {tabBar("horizontal")}
                  <div className="wiv2-content-row">
                    <div className="wiv2-content">
                      {inlineEdit ? (
                        <div className="wiv2-tabpanel">{editForm}</div>
                      ) : (
                        renderPanels()
                      )}
                    </div>
                    {sidebarShown && (
                      <aside className="wiv2-sidebar">
                        <MetaPanel workItem={workItem} detail={detail} />
                      </aside>
                    )}
                  </div>
                </div>
              )}
              {!editMode && (
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
              )}
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
