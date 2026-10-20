import { useState, useEffect, useCallback } from "react";
import "./workitem-v2.css";
import { WorkItem, WorkItemStatus } from "../../types";
import { workItemService } from "../../services/auth";
import useRenderedPrompt from "../../hooks/useRenderedPrompt";
import ConfirmModal from "../ConfirmModal";
import LoopRunTerminal from "../LoopRunTerminal";
import { useWorkItemDetail } from "./useWorkItemDetail";
import { PreviewPanel, MetaPanel, DescriptionPanel } from "./panels";
import RunsPanel from "./RunsPanel";
import ActionThread from "./ActionThread";
import PendingEditProposals from "./PendingEditProposals";
import EditPanel from "./EditPanel";
import FilesPanel, { workItemKey } from "./FilesPanel";
import HistoryPanel from "./HistoryPanel";
import MenuButton, { type MenuItem } from "../MenuButton";
import Icon from "../Icon";
import { errorMessage } from "../../utils/errorMessage";
import {
  feedbackReasonLabel,
  feedbackReasonTone,
  pillClass,
  workItemStatusLabel,
  workItemStatusTone,
} from "../../utils/labels";
import { repositoryName } from "../../utils/repositoryName";

type TabId = "overview" | "action" | "runs" | "files" | "preview" | "terminal" | "history";

/**
 * An item is awaiting human action when it is parked in HumanFeedback with a
 * reason set — the same condition the Action tab's ● indicator and feedback pane
 * render on.
 */
function awaitingHumanAction(workItem: WorkItem | null): boolean {
  return workItem?.status === WorkItemStatus.HumanFeedback && !!workItem.humanFeedbackReason;
}

const OVERVIEW_STATUSES: ReadonlySet<WorkItemStatus> = new Set([
  WorkItemStatus.Backlog,
  WorkItemStatus.WorkQueue,
  WorkItemStatus.Ready,
  WorkItemStatus.Done,
]);

/**
 * Items at rest open on Overview; every other status, including any added later,
 * has work in flight and opens on Action. A link to a suggestion opens Action,
 * where its card is.
 */
function openingTab(workItem: WorkItem | null, revealGlobalProposalId?: string | null): TabId {
  if (workItem && revealGlobalProposalId) return "action";
  return !workItem || OVERVIEW_STATUSES.has(workItem.status) ? "overview" : "action";
}

interface WorkItemModalV2Props {
  /** The item to show, or null to render the new-item creation form. */
  workItem: WorkItem | null;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
  /** A global variable suggestion one of the item's runs made, to open the Action tab scrolled to. */
  revealGlobalProposalId?: string | null;
}

/**
 * Near-fullscreen work item detail dialog: a horizontal tab bar (Overview,
 * Action, Runs, Files, Preview, then Terminal when there is a worktree, and History
 * last) over the full width, with run
 * history shown inline rather than on a separate page. History is the record of
 * what changed on the item, by whom and why, kept apart from the Action thread's
 * dialogue with its runs. The Action tab is one chat
 * thread: the conversation, the live progress stream in the latest AI bubble,
 * and the human-feedback pane in the latest human bubble. It flags itself with an indicator
 * while the item waits on a human. An item opens on the Action tab while work on it
 * is in flight and on Overview otherwise. With a null
 * workItem the dialog drops the tabs and shows the creation form instead, so a
 * single dialog covers both creating and viewing/editing work items.
 */
export default function WorkItemModalV2({
  workItem,
  onClose,
  onSave,
  onDelete,
  revealGlobalProposalId,
}: WorkItemModalV2Props) {
  const detail = useWorkItemDetail(workItem, onSave);
  // Chosen from the item's status, regardless of which tab was last viewed on a
  // previously-opened item.
  const [activeTab, setActiveTab] = useState<TabId>(openingTab(workItem, revealGlobalProposalId));
  // A link to another suggestion while the dialog is open goes to its card too.
  const [linkedProposal, setLinkedProposal] = useState(revealGlobalProposalId);
  if (revealGlobalProposalId !== linkedProposal) {
    setLinkedProposal(revealGlobalProposalId);
    if (workItem && revealGlobalProposalId) setActiveTab("action");
  }
  const [editMode, setEditMode] = useState(false);
  const [editDirty, setEditDirty] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showCloseConfirm, setShowCloseConfirm] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [showTerminal, setShowTerminal] = useState(false);
  // Backlog to Work queue without dragging the card, which a phone cannot do.
  const [moving, setMoving] = useState(false);
  const [moveError, setMoveError] = useState<string | null>(null);
  // A refusal was about the item as it was: once it has moved or changed state,
  // the message no longer applies.
  useEffect(() => setMoveError(null), [workItem?.id, workItem?.status]);
  const moveToWorkQueue = async () => {
    setMoving(true);
    setMoveError(null);
    try {
      onSave(await workItemService.moveTo(workItem!.id, WorkItemStatus.WorkQueue));
    } catch (error) {
      setMoveError(errorMessage(error, "Could not move it to the work queue."));
    } finally {
      setMoving(false);
    }
  };

  // Leaving the edit view without saving discards the files staged in it.
  const clearStaged = detail.editAttachments.clear;
  const exitEdit = useCallback(() => {
    setEditMode(false);
    setEditDirty(false);
    clearStaged();
  }, [clearStaged]);

  // Closing must not silently discard an in-progress edit, the create form,
  // typed feedback, or a file staged in the form. The create form (null
  // workItem) is always an open form, so its dirty flag counts the same as an
  // edit's.
  const isCreate = workItem === null;
  const hasUnsavedChanges =
    ((editMode || isCreate) && editDirty) ||
    detail.replyFor(workItem?.latestLoopRunId ?? null).input.trim().length > 0 ||
    detail.editAttachments.staged.length > 0;

  const requestClose = useCallback(() => {
    // A write already on its way cannot be called back, so closing over it
    // would land files the human is in the middle of discarding — the save that
    // is still running would upload them. Closing waits for whatever the dialog
    // is doing, not for the uploading part of it alone.
    // A move in flight is such a write too: its answer updates this item.
    if (detail.busy || moving) return;
    if (hasUnsavedChanges) {
      setShowCloseConfirm(true);
    } else {
      onClose();
    }
  }, [detail.busy, moving, hasUnsavedChanges, onClose]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // The chat stays usable over the dialog, so an Escape typed in it is the
      // chat's, not a request to close the work item.
      if (e.target instanceof Element && e.target.closest(".chat-panel")) return;
      if (e.key === "Escape") requestClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [requestClose]);

  useEffect(() => {
    setEditMode(false);
    setEditDirty(false);
    setActiveTab(openingTab(workItem, revealGlobalProposalId));
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
  // The updater re-checks the tab because an item switch in the same commit has
  // already queued the new item's opening tab, which must win.
  useEffect(() => {
    if (activeTab === "terminal" && !canUseTerminal)
      setActiveTab((tab) => (tab === "terminal" ? "overview" : tab));
  }, [activeTab, canUseTerminal]);

  // Files and Preview are disabled without a worktree; one reclaimed while either
  // is open sends the dialog back to Overview the same way.
  const worktreeGone = !workItem?.worktreePath;
  useEffect(() => {
    if (worktreeGone && (activeTab === "files" || activeTab === "preview"))
      setActiveTab((tab) => (tab === "files" || tab === "preview" ? "overview" : tab));
  }, [activeTab, worktreeGone]);

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
            className="wiv2-dialog wiv2-dialog-form"
            onMouseDown={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-label="New work item"
          >
            <div className="wiv2-header">
              <div className="wiv2-header-copy">
                <div className="wiv2-header-title-row">
                  <h2>New work item</h2>
                </div>
              </div>
              <div className="wiv2-header-actions">
                <button
                  type="button"
                  className="btn btn-ghost btn-icon"
                  onClick={requestClose}
                  aria-label="Close"
                >
                  <Icon name="close" size={18} />
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

  // "Action required" mirrors the FeedbackBanner's render condition: the item is
  // waiting on a human. The Action tab flags this with the same ● indicator the
  // Preview tab uses for a running preview.
  const actionRequired = awaitingHumanAction(workItem);

  // Files and Preview read the run's worktree; without one they stay in the
  // strip, disabled with a reason, instead of opening onto an empty panel.
  const hasWorktree = Boolean(workItem.worktreePath);
  const noWorktree = "No worktree yet: available once a run has started";
  // A tab's dot says what is going on there; screen readers hear it in words.
  const tabs: { id: TabId; label: string; dot?: string | false; disabledReason?: string }[] = [
    { id: "overview", label: "Overview" },
    { id: "action", label: "Action", dot: actionRequired && "needs you" },
    { id: "runs", label: `Runs${detail.runs.length > 0 ? ` (${detail.runs.length})` : ""}` },
    { id: "files", label: "Files", disabledReason: hasWorktree ? undefined : noWorktree },
    {
      id: "preview",
      label: "Preview",
      dot: detail.preview?.state === "running" && "running",
      disabledReason: hasWorktree ? undefined : noWorktree,
    },
    ...(canUseTerminal
      ? [{ id: "terminal" as TabId, label: "Terminal", dot: showTerminal && "open" }]
      : []),
    { id: "history", label: "History" },
  ];
  const enabledTabs = tabs.filter((t) => !t.disabledReason);

  const showCleanupButtons =
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason !== "Human Input Needed" &&
    workItem.humanFeedbackReason !== "PR Awaiting Merge";
  // The footer's cleanup acts on the current run, so it waits for any other
  // action on that run, wherever in the dialog that was started.
  const currentRunBlocked = detail.runLock.pendingOf(workItem.currentLoopRunId) !== null;
  // Secondary actions live behind ⋯ so the footer keeps one obvious button. The
  // header's × is the only close.
  const footerMenu: MenuItem[] = showCleanupButtons
    ? [
        {
          id: "cleanup-done",
          label: "Clean up → Done",
          disabled: currentRunBlocked,
          onSelect: () =>
            void detail.runLock.hold(
              workItem.currentLoopRunId,
              "abandon",
              detail.handleCleanupDone,
            ),
        },
        {
          id: "cleanup-backlog",
          label: "Clean up → Backlog",
          disabled: currentRunBlocked,
          onSelect: () =>
            void detail.runLock.hold(
              workItem.currentLoopRunId,
              "abandon",
              detail.handleCleanupBacklog,
            ),
        },
      ]
    : [];

  // Arrow keys move between the tabs that can be opened, skipping disabled ones.
  const handleTabKeyDown = (e: React.KeyboardEvent, id: TabId) => {
    const index = enabledTabs.findIndex((t) => t.id === id);
    const count = enabledTabs.length;
    let nextIndex: number | null = null;
    if (e.key === "ArrowRight") nextIndex = (index + 1) % count;
    else if (e.key === "ArrowLeft") nextIndex = (index - 1 + count) % count;
    else if (e.key === "Home") nextIndex = 0;
    else if (e.key === "End") nextIndex = count - 1;
    if (nextIndex === null) return;
    e.preventDefault();
    const next = enabledTabs[nextIndex];
    setActiveTab(next.id);
    const nextTab = document.getElementById(`wiv2-tab-${next.id}`);
    nextTab?.focus();
    // The tab strip scrolls sideways when the tabs outgrow the dialog, so
    // arrowing onto a tab that is off to the side has to bring it into view.
    nextTab?.scrollIntoView?.({ block: "nearest", inline: "nearest" });
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
            <PendingEditProposals workItemId={workItem.id} />
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
        <ActionThread
          workItem={workItem}
          detail={detail}
          feedbackPrompt={feedbackPrompt}
          active={activeTab === "action"}
          revealGlobalProposalId={revealGlobalProposalId}
          onShowRuns={() => setActiveTab("runs")}
        />
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
          onHalt={() => detail.handleHalt(workItem.currentLoopRunId)}
          onResumeSteer={(note) => detail.handleResumeSteer(workItem.currentLoopRunId, note)}
          onCleanupDone={detail.handleCleanupDone}
          onCleanupBacklog={detail.handleCleanupBacklog}
          onReclaimRun={detail.handleReclaimRun}
          onPauseRun={detail.handlePauseRun}
          onResumeRun={detail.handleResumeRun}
          onCancelRun={detail.handleCancelRun}
          onDeleteRun={detail.handleDeleteRun}
          readVersionGraph={detail.readVersionGraph}
          runLock={detail.runLock}
        />
      </section>
      <section
        role="tabpanel"
        id="wiv2-panel-files"
        aria-labelledby="wiv2-tab-files"
        className="wiv2-tabpanel wiv2-tabpanel-files"
        hidden={activeTab !== "files"}
      >
        <FilesPanel key={workItemKey(workItem)} workItem={workItem} />
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
              <p>Open a shell in this worktree</p>
              <div className="wiv2-terminal-path">
                <code title={workItem.worktreePath ?? undefined}>{workItem.worktreePath}</code>
                <button
                  type="button"
                  className="btn btn-sm btn-ghost"
                  onClick={() => void navigator.clipboard?.writeText(workItem.worktreePath ?? "")}
                >
                  <Icon name="copy" size={14} />
                  Copy path
                </button>
              </div>
              <button
                type="button"
                className="btn btn-primary"
                onClick={() => setShowTerminal(true)}
              >
                Open Terminal
              </button>
            </div>
          )}
        </section>
      )}
      <section
        role="tabpanel"
        id="wiv2-panel-history"
        aria-labelledby="wiv2-tab-history"
        className="wiv2-tabpanel"
        hidden={activeTab !== "history"}
      >
        <HistoryPanel
          key={workItem.id}
          workItem={workItem}
          active={activeTab === "history"}
          repositoryName={(id) =>
            repositoryName(id, detail.repositories, detail.repositoriesLoaded)
          }
        />
      </section>
    </>
  );

  const tabBar = (
    <div className="wiv2-tabbar" role="tablist" aria-label="Work item sections">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          id={`wiv2-tab-${tab.id}`}
          aria-selected={activeTab === tab.id}
          aria-controls={`wiv2-panel-${tab.id}`}
          aria-disabled={tab.disabledReason ? true : undefined}
          title={tab.disabledReason}
          tabIndex={activeTab === tab.id ? 0 : -1}
          className={`wiv2-tab ${activeTab === tab.id ? "wiv2-tab-active" : ""}`}
          onClick={() => {
            if (!tab.disabledReason) setActiveTab(tab.id);
          }}
          onKeyDown={(e) => handleTabKeyDown(e, tab.id)}
        >
          {tab.label}
          {tab.dot && (
            <>
              <span className="wiv2-tab-dot" aria-hidden="true" />
              <span className="sr-only">, {tab.dot}</span>
            </>
          )}
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
              <h2 className="wiv2-title">{workItem.title}</h2>
              <div className="wiv2-header-pills">
                <span className={`${pillClass(workItemStatusTone(workItem.status))} pill-dot`}>
                  {workItemStatusLabel(workItem.status)}
                </span>
                {actionRequired && workItem.humanFeedbackReason && (
                  <span className={pillClass(feedbackReasonTone(workItem.humanFeedbackReason))}>
                    {feedbackReasonLabel(workItem.humanFeedbackReason)}
                  </span>
                )}
                <span className={pillClass("muted")}>{workItem.priority} priority</span>
              </div>
            </div>
            <div className="wiv2-header-actions">
              <button
                type="button"
                className="btn btn-ghost btn-icon"
                onClick={requestClose}
                aria-label="Close"
              >
                <Icon name="close" size={18} />
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
                <MenuButton label="More actions" items={footerMenu} />
                {moveError && (
                  <span className="wiv2-footer-error" role="alert">
                    {moveError}
                  </span>
                )}
                <span className="wiv2-footer-spacer" />
                <button
                  type="button"
                  className="btn btn-secondary"
                  onClick={() => setEditMode(true)}
                  // Leaving an edit discards its staged files; an edit started
                  // on top of another write could discard them mid-upload.
                  disabled={detail.busy}
                >
                  Edit
                </button>
                {workItem.status === WorkItemStatus.Backlog && (
                  <button
                    type="button"
                    className="btn btn-primary"
                    onClick={() => void moveToWorkQueue()}
                    disabled={moving || detail.busy}
                  >
                    Move to Work queue
                  </button>
                )}
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
