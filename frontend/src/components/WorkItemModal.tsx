import { useState, useEffect, useCallback } from "react";
import { Link } from "react-router-dom";
import "./WorkItemModal.css";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  Repository,
  LoopTemplate,
  LoopRun,
  WorktreePreview,
} from "../types";
import type { TypedSignalRMessage } from "../types/signalr";
import { workItemService, repositoryService, loopTemplateService } from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";
import useRenderedPrompt from "../hooks/useRenderedPrompt";
import LiveStream from "./NodeTimeline/LiveStream";
import ConfirmModal from "./ConfirmModal";
import LoopRunTerminal from "./LoopRunTerminal";
import TagAutocomplete from "./TagAutocomplete";
import { makeLoopTagMatcher, parseConversation, parseTags } from "../utils/workItemJson";
import Accordion from "./Accordion";
import MarkdownRenderer from "./MarkdownRenderer";
import FeedbackActions from "./FeedbackActions";

interface WorkItemModalProps {
  workItem: WorkItem | null;
  isOpen: boolean;
  onClose: () => void;
  onSave: (workItem: WorkItem) => void;
  onDelete?: (id: string) => void;
}

export default function WorkItemModal({
  workItem,
  isOpen,
  onClose,
  onSave,
  onDelete,
}: WorkItemModalProps) {
  const [editMode, setEditMode] = useState(workItem === null);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [status, setStatus] = useState<WorkItemStatus>(WorkItemStatus.Backlog);
  const [priority, setPriority] = useState<WorkItemPriority>(WorkItemPriority.Medium);
  const [tags, setTags] = useState("");
  const [repositoryId, setRepositoryId] = useState("");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [dependencies, setDependencies] = useState<WorkItem[]>([]);
  const [allWorkItems, setAllWorkItems] = useState<WorkItem[]>([]);
  const [showAddDep, setShowAddDep] = useState(false);
  const [selectedDepId, setSelectedDepId] = useState("");
  const [showLinkPr, setShowLinkPr] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [prUrlInput, setPrUrlInput] = useState("");
  const [feedbackInput, setFeedbackInput] = useState("");
  const [prCommentsLoading, setPrCommentsLoading] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const [mergeMessage, setMergeMessage] = useState<string | null>(null);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [progressText, setProgressText] = useState<string>("");
  const [preview, setPreview] = useState<WorktreePreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewPortInputs, setPreviewPortInputs] = useState<Record<string, string>>({});
  const [showTerminal, setShowTerminal] = useState(false);

  useEffect(() => {
    repositoryService
      .getAll()
      .then(setRepositories)
      .catch(() => {});
    loopTemplateService
      .getAll()
      .then(setTemplates)
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (workItem && !editMode) {
      workItemService
        .getRuns(workItem.id)
        .then((r) => setRuns(Array.isArray(r) ? r : []))
        .catch(() => {});
      workItemService
        .getDependencies(workItem.id)
        .then((d) => setDependencies(Array.isArray(d) ? d : []))
        .catch(() => {});
      workItemService
        .getAll()
        .then((w) => setAllWorkItems(Array.isArray(w) ? w : []))
        .catch(() => {});
    }
  }, [workItem?.id, editMode]);

  const refreshPreview = useCallback(async () => {
    if (!workItem?.id || !workItem.worktreePath || editMode) {
      setPreview(null);
      setPreviewError(null);
      return;
    }

    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const result = await workItemService.getPreview(workItem.id);
      setPreview(result);
    } catch (error) {
      const message = (error as { message?: string })?.message ?? "Failed to load preview status.";
      setPreviewError(message);
      setPreview(null);
    } finally {
      setPreviewLoading(false);
    }
  }, [workItem?.id, workItem?.worktreePath, editMode]);

  useEffect(() => {
    void refreshPreview();
  }, [refreshPreview]);

  useEffect(() => {
    if (!preview?.services.length) return;

    setPreviewPortInputs((prev) => {
      const next = { ...prev };
      for (const service of preview.services) {
        if (
          (!next[service.portAlias] || next[service.portAlias].length === 0) &&
          service.suggestedPort
        ) {
          next[service.portAlias] = String(service.suggestedPort);
        }
      }
      return next;
    });
  }, [preview]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Fetch the rendered prompt for the currently-suspended node (Human or PR).
  // NodeStarted is universal and carries the rendered prompt as its payload.
  // Hooks must be called unconditionally, so we always invoke the hook and
  // filter the result at the JSX level based on the current feedback reason.
  const runId = workItem?.currentLoopRunId
    ? (workItem as { currentLoopRunId?: string }).currentLoopRunId
    : undefined;
  const humanPrompt = useRenderedPrompt(
    workItem?.status === WorkItemStatus.HumanFeedback &&
      workItem.humanFeedbackReason === "Human Input Needed"
      ? runId
      : undefined,
    "NodeStarted",
  );
  const prPrompt = useRenderedPrompt(
    workItem?.status === WorkItemStatus.HumanFeedback &&
      workItem.humanFeedbackReason === "PR Awaiting Merge"
      ? runId
      : undefined,
    "NodeStarted",
  );

  useEffect(() => {
    // When parked at a PR node, prefill the feedback textarea with any
    // unread PR comments so the human can edit them before approving or
    // rejecting. Best-effort: failures leave the textarea empty.
    if (
      !workItem ||
      workItem.status !== WorkItemStatus.HumanFeedback ||
      workItem.humanFeedbackReason !== "PR Awaiting Merge"
    ) {
      return;
    }
    let cancelled = false;
    setPrCommentsLoading(true);
    void (async () => {
      try {
        const comments = await workItemService.getPrComments(workItem.id);
        if (cancelled) return;
        if (Array.isArray(comments) && comments.length > 0) {
          const text = comments
            .map((c) => `${c.author}: ${c.body}`.trim())
            .filter(Boolean)
            .join("\n\n");
          setFeedbackInput((prev) => (prev.length === 0 ? text : prev));
        }
      } catch {
        // Ignore — empty textarea is fine.
      } finally {
        if (!cancelled) setPrCommentsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [workItem?.id, workItem?.status, workItem?.humanFeedbackReason]);

  // Re-initialize the form whenever the dialog opens (or the item it shows
  // changes). The create modal is kept mounted with workItem === null and only
  // toggled via isOpen, so isOpen must be a dependency — otherwise the fields
  // typed for one new item would still be present the next time it is opened.
  useEffect(() => {
    if (!isOpen) return;
    if (workItem) {
      setTitle(workItem.title);
      setDescription(workItem.description);
      setStatus(workItem.status);
      setPriority(workItem.priority);
      setTags(parseTags(workItem).join(", "));
      setRepositoryId(workItem.repositoryId);
      setShowLinkPr(false);
      setPrUrlInput("");
      setFeedbackInput("");
      setShowDeleteConfirm(false);
      setDeleteError(null);
      setEditMode(false);
    } else {
      setTitle("");
      setDescription("");
      setStatus(WorkItemStatus.Backlog);
      setPriority(WorkItemPriority.Medium);
      setTags("");
      setRepositoryId("");
      setSubmitError(null);
      setShowDeleteConfirm(false);
      setDeleteError(null);
      setEditMode(true);
    }
  }, [isOpen, workItem?.id, workItem?.status]);

  // Live stream for running work items
  const shouldStream =
    workItem &&
    workItem.status === WorkItemStatus.Running &&
    workItem.currentLoopRunId &&
    !editMode;

  const {
    on: runOn,
    off: runOff,
    invoke: runInvoke,
    connectionState: runConnectionState,
  } = useSignalR("/hubs/loop-run");

  const refetchWorkItem = useCallback(() => {
    if (!workItem) return;
    void workItemService
      .getById(workItem.id)
      .then((updated) => onSave(updated))
      .catch(() => {});
  }, [workItem?.id, onSave]);

  useEffect(() => {
    if (runConnectionState !== "connected" || !shouldStream || !workItem?.currentLoopRunId) return;
    void runInvoke?.("SubscribeToRun", workItem.currentLoopRunId);
  }, [shouldStream, runInvoke, runConnectionState, workItem?.currentLoopRunId]);

  useEffect(() => {
    if (!shouldStream) {
      setProgressText("");
      return;
    }

    const delayedTimers: number[] = [];

    const onNodeProgress = (message: TypedSignalRMessage<"NodeProgress">) => {
      const { runId: msgRunId, line } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      setProgressText((prev) => prev + line);
    };

    const onLoopRunStateChanged = (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      const { runId: msgRunId } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      refetchWorkItem();
      // Delayed refetch to catch conversation data that may not be persisted yet
      delayedTimers.push(setTimeout(refetchWorkItem, 500));
    };

    const onNodeStateChanged = (message: TypedSignalRMessage<"NodeStateChanged">) => {
      const { runId: msgRunId } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      refetchWorkItem();
      // Delayed refetch to catch conversation data that may not be persisted yet
      delayedTimers.push(setTimeout(refetchWorkItem, 500));
    };

    const onEventLogged = (message: TypedSignalRMessage<"EventLogged">) => {
      const { runId: msgRunId } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      refetchWorkItem();
    };

    runOn("NodeProgress", onNodeProgress);
    runOn("LoopRunStateChanged", onLoopRunStateChanged);
    runOn("NodeStateChanged", onNodeStateChanged);
    runOn("EventLogged", onEventLogged);

    return () => {
      runOff("NodeProgress", onNodeProgress);
      runOff("LoopRunStateChanged", onLoopRunStateChanged);
      runOff("NodeStateChanged", onNodeStateChanged);
      runOff("EventLogged", onEventLogged);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [shouldStream, runOn, runOff, workItem?.currentLoopRunId, refetchWorkItem]);

  if (!isOpen) return null;

  const handleLinkPr = async () => {
    if (!workItem || !prUrlInput.trim()) return;
    try {
      await workItemService.linkPr(workItem.id, prUrlInput.trim());
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
      setShowLinkPr(false);
      setPrUrlInput("");
    } catch (error) {
      console.error("Failed to link PR:", error);
    }
  };

  // The PR/feedback/cleanup actions all share the same shape: invoke a service
  // call, refetch the work item, and hand it to onSave (logging on failure).
  const runAction = async (action: (id: string) => Promise<unknown>, errorLabel: string) => {
    if (!workItem) return;
    try {
      await action(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error(`Failed to ${errorLabel}:`, error);
    }
  };

  const handleContinue = () =>
    runAction(
      (id) => workItemService.humanFeedbackInput(id, feedbackInput || ""),
      "submit feedback",
    );

  // Pass any typed feedback through to the OnFailure successor as {{PreviousNode.Output}}.
  const handleReject = () =>
    runAction(
      (id) => workItemService.humanFeedbackReject(id, feedbackInput || undefined),
      "reject",
    );

  // Route the parked node to one of its named custom edges (a Human/PR button).
  const handleEdge = (name: string) =>
    runAction((id) => workItemService.humanFeedbackEdge(id, name, feedbackInput || ""), "respond");

  // Merge the linked PR on the remote (and optionally delete the branch), then
  // continue along OnSuccess. A merge failure leaves the item parked, so surface
  // the error instead of swallowing it like the other actions.
  const handleMerge = async (deleteBranch: boolean) => {
    if (!workItem) return;
    setMergeError(null);
    setMergeMessage(null);
    try {
      const result = await workItemService.mergePr(workItem.id, deleteBranch);
      setMergeMessage(
        result.warning ?? (deleteBranch ? "PR merged and branch deleted." : "PR merged."),
      );
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      setMergeError((error as { message?: string })?.message ?? "Failed to merge PR.");
    }
  };

  const handleCleanupDone = () =>
    runAction((id) => workItemService.cleanupToDone(id), "cleanup to done");

  const handleCleanupBacklog = () =>
    runAction((id) => workItemService.cleanupToBacklog(id), "cleanup to backlog");

  const handleStartPreview = async () => {
    if (!workItem) return;
    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const portOverrides: Record<string, number> = {};
      for (const [alias, value] of Object.entries(previewPortInputs)) {
        const trimmed = value.trim();
        if (trimmed.length === 0) continue;
        const port = Number.parseInt(trimmed, 10);
        if (!Number.isInteger(port) || port <= 0) {
          setPreviewError(`Invalid port '${value}' for ${alias}.`);
          setPreviewLoading(false);
          return;
        }
        portOverrides[alias] = port;
      }

      const result = await workItemService.startPreview(workItem.id, {
        portOverrides: Object.keys(portOverrides).length > 0 ? portOverrides : undefined,
      });
      setPreview(result);
    } catch (error) {
      const message = (error as { message?: string })?.message ?? "Failed to start preview.";
      setPreviewError(message);
    } finally {
      setPreviewLoading(false);
    }
  };

  const handleStopPreview = async () => {
    if (!workItem) return;
    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const result = await workItemService.stopPreview(workItem.id);
      setPreview(result);
    } catch (error) {
      const message = (error as { message?: string })?.message ?? "Failed to stop preview.";
      setPreviewError(message);
    } finally {
      setPreviewLoading(false);
    }
  };

  const primaryPreviewUrl =
    preview?.services.find((service) => !!service.publicUrl)?.publicUrl ?? null;

  const handleDelete = async () => {
    if (!workItem) return;
    setShowDeleteConfirm(true);
  };

  const handleDeleteConfirm = async () => {
    if (!workItem) return;
    try {
      await workItemService.delete(workItem.id);
      onDelete?.(workItem.id);
      onClose();
    } catch (error) {
      const msg = (error as { message?: string })?.message ?? "Failed to delete work item";
      setDeleteError(msg);
    } finally {
      setShowDeleteConfirm(false);
    }
  };

  const handleAddDependency = async () => {
    if (!workItem || !selectedDepId) return;
    try {
      await workItemService.addDependency(workItem.id, selectedDepId);
      const deps = await workItemService.getDependencies(workItem.id);
      setDependencies(deps);
      setShowAddDep(false);
      setSelectedDepId("");
    } catch (error) {
      console.error("Failed to add dependency:", error);
    }
  };

  const handleRemoveDependency = async (depId: string) => {
    if (!workItem) return;
    try {
      await workItemService.removeDependency(workItem.id, depId);
      setDependencies((prev) => prev.filter((d) => d.id !== depId));
    } catch (error) {
      console.error("Failed to remove dependency:", error);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setSubmitError(null);

    const parsedTags = tags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    const data: Record<string, unknown> = {
      title,
      description,
      status,
      priority,
      tags: parsedTags,
      repositoryId,
    };

    try {
      let saved: WorkItem;
      if (workItem) {
        saved = await workItemService.update(workItem.id, data as Partial<WorkItem>);
        if (workItem.status !== status) {
          try {
            await workItemService.transition(workItem.id, status);
            saved = await workItemService.getById(workItem.id);
          } catch (err) {
            console.error("Failed to transition status:", err);
            setSubmitError(
              `Status transition failed: ${err instanceof Error ? err.message : "Unknown error"}`,
            );
          }
        }
      } else {
        saved = await workItemService.create(data as Partial<WorkItem>);
      }
      onSave(saved);
      if (workItem) {
        setEditMode(false);
      } else {
        onClose();
      }
    } catch (error) {
      console.error("Failed to save work item:", error);
      setSubmitError(
        `Failed to save: ${(error as { message?: string })?.message ?? "Unknown error"}`,
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <>
      {workItem && !editMode ? (
        <div className="modal-overlay" onMouseDown={onClose}>
          <div
            className="modal-content modal-content-detail"
            onMouseDown={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-label="Work item details"
          >
            {/* Header with status badge */}
            <div className="modal-header">
              <div className="modal-header-copy">
                <div className="modal-work-item-id">#{workItem.id}</div>
                <div className="modal-header-title-row">
                  <h2>{workItem.title}</h2>
                  <span className={`status-badge status-${workItem.status.toLowerCase()}`}>
                    {workItem.status}
                  </span>
                </div>
              </div>
              <button className="modal-close" onClick={onClose}>
                &times;
              </button>
            </div>
            <div className="modal-body">
              {/* Worktree terminal — available for any run that has stopped
                  executing and still has a live worktree (parked for review,
                  failed, or cancelled), so the diff can be inspected or work
                  recovered. Hidden while the run is active (Running, or
                  WaitingForIld where the run is still Running under the hood)
                  since its executor is writing the worktree. */}
              {workItem.status !== WorkItemStatus.Running &&
                workItem.status !== WorkItemStatus.WaitingForIld &&
                workItem.currentLoopRunId &&
                workItem.worktreePath && (
                  <div className="detail-section worktree-terminal-row">
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setShowTerminal(true)}
                      title={`Open a shell in ${workItem.worktreePath}`}
                    >
                      Open Terminal
                    </button>
                  </div>
                )}

              {/* Human Feedback — always visible when applicable */}
              {workItem.status === WorkItemStatus.HumanFeedback &&
                workItem.humanFeedbackReason === "Human Input Needed" && (
                  <div className="detail-section human-feedback-section">
                    <span className="detail-label">Human Feedback</span>
                    {humanPrompt && (
                      <div className="markdown-container feedback-prompt">
                        <MarkdownRenderer content={humanPrompt} />
                      </div>
                    )}
                    <textarea
                      className="feedback-textarea"
                      value={feedbackInput}
                      onChange={(e) => setFeedbackInput(e.target.value)}
                      placeholder="Optional input or context..."
                      rows={3}
                    />
                    <FeedbackActions
                      actions={workItem.humanFeedbackActions}
                      onApprove={handleContinue}
                      onReject={handleReject}
                      onEdge={handleEdge}
                    />
                  </div>
                )}
              {workItem.status === WorkItemStatus.HumanFeedback &&
                workItem.humanFeedbackReason === "PR Awaiting Merge" && (
                  <div className="detail-section human-feedback-section">
                    <span className="detail-label">PR Feedback</span>
                    {workItem.prUrl && (
                      <a
                        className="feedback-pr-link"
                        href={workItem.prUrl}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        Open PR
                      </a>
                    )}
                    {prPrompt && (
                      <div className="markdown-container feedback-prompt">
                        <MarkdownRenderer content={prPrompt} />
                      </div>
                    )}
                    <textarea
                      className="feedback-textarea"
                      value={feedbackInput}
                      onChange={(e) => setFeedbackInput(e.target.value)}
                      placeholder={
                        prCommentsLoading
                          ? "Loading PR comments..."
                          : "Optional feedback for the next node..."
                      }
                      rows={5}
                    />
                    <FeedbackActions
                      actions={workItem.humanFeedbackActions}
                      onApprove={handleContinue}
                      onReject={handleReject}
                      onEdge={handleEdge}
                      onMerge={handleMerge}
                    />
                    {mergeError && (
                      <div className="preview-message preview-error">{mergeError}</div>
                    )}
                    {!mergeError && mergeMessage && (
                      <div className="preview-message">{mergeMessage}</div>
                    )}
                  </div>
                )}
              {workItem.status === WorkItemStatus.HumanFeedback &&
                workItem.humanFeedbackReason &&
                workItem.humanFeedbackReason !== "Human Input Needed" &&
                workItem.humanFeedbackReason !== "PR Awaiting Merge" && (
                  <div className="detail-section human-feedback-section">
                    <span className="detail-label">Human Feedback</span>
                    <div className="feedback-reason">{workItem.humanFeedbackReason}</div>
                  </div>
                )}

              {/* Live Stream — always visible when running */}
              {shouldStream && <LiveStream text={progressText} />}

              {/* Details Accordion */}
              <Accordion title="Details">
                {workItem.description && (
                  <div className="detail-section">
                    <span className="detail-label">Description</span>
                    <div className="markdown-container detail-description">
                      <MarkdownRenderer content={workItem.description} />
                    </div>
                  </div>
                )}
                {(() => {
                  const tagList = parseTags(workItem);
                  if (tagList.length === 0) return null;
                  const isLoopTag = makeLoopTagMatcher(templates.map((t) => t.name));
                  return (
                    <div className="detail-row">
                      <span className="detail-label">Tags</span>
                      <span className="detail-value">
                        {tagList.map((t) => (
                          <span
                            key={t}
                            className={`work-item-tag${isLoopTag(t) ? " work-item-tag--loop" : ""}`}
                            style={{ marginRight: 4 }}
                          >
                            {t}
                          </span>
                        ))}
                      </span>
                    </div>
                  );
                })()}
                <div className="detail-section">
                  <span className="detail-label">Pull Request</span>
                  {workItem.prUrl ? (
                    <div className="pr-section">
                      <a
                        href={workItem.prUrl}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="pr-link"
                      >
                        {workItem.prUrl}
                      </a>
                    </div>
                  ) : (
                    <span className="detail-value pr-none">No PR linked</span>
                  )}
                  {showLinkPr ? (
                    <div className="link-pr-form">
                      <input
                        type="url"
                        value={prUrlInput}
                        onChange={(e) => setPrUrlInput(e.target.value)}
                        placeholder="https://forgejo/pr/..."
                        className="pr-input"
                      />
                      <div className="link-pr-actions">
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          onClick={handleLinkPr}
                        >
                          Link
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-secondary"
                          onClick={() => setShowLinkPr(false)}
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  ) : (
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setShowLinkPr(true)}
                    >
                      Link PR
                    </button>
                  )}
                </div>
                <div className="detail-section">
                  <span className="detail-label">Dependencies</span>
                  <div className="dependency-list">
                    {dependencies.length === 0 && (
                      <span className="detail-value">No dependencies</span>
                    )}
                    {dependencies.map((dep) => (
                      <span key={dep.id} className="dependency-tag">
                        <Link to={`/workitems/${dep.id}`} className="dependency-link">
                          {dep.title}
                        </Link>
                        <button
                          type="button"
                          className="dependency-remove-btn"
                          onClick={() => handleRemoveDependency(dep.id)}
                          aria-label={`Remove dependency ${dep.title}`}
                        >
                          ×
                        </button>
                      </span>
                    ))}
                  </div>
                  {showAddDep ? (
                    <div className="link-pr-form">
                      <select
                        value={selectedDepId}
                        onChange={(e) => setSelectedDepId(e.target.value)}
                        className="pr-input"
                      >
                        <option value="">Select work item...</option>
                        {allWorkItems
                          .filter(
                            (w) => w.id !== workItem.id && !dependencies.some((d) => d.id === w.id),
                          )
                          .map((w) => (
                            <option key={w.id} value={w.id}>
                              {w.title}
                            </option>
                          ))}
                      </select>
                      <div className="link-pr-actions">
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          onClick={handleAddDependency}
                        >
                          Add
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-secondary"
                          onClick={() => setShowAddDep(false)}
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  ) : (
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setShowAddDep(true)}
                    >
                      Add Dependency
                    </button>
                  )}
                </div>
                {runs.length > 0 && (
                  <div className="detail-section">
                    <span className="detail-label">Run History</span>
                    <div className="run-history">
                      {runs.map((run) => (
                        <Link key={run.id} to={`/loop-runs/${run.id}`} className="run-history-item">
                          <span className={`status-badge status-${run.status.toLowerCase()}`}>
                            {run.status}
                          </span>
                          <span className="run-time">
                            {new Date(run.startedAt).toLocaleString()}
                          </span>
                        </Link>
                      ))}
                    </div>
                  </div>
                )}
                <div className="detail-section detail-actions-row">
                  <button
                    type="button"
                    className="btn btn-sm btn-edit"
                    onClick={() => setEditMode(true)}
                  >
                    Edit
                  </button>
                  <button type="button" className="btn btn-sm btn-danger" onClick={handleDelete}>
                    Delete
                  </button>
                  {workItem.status === WorkItemStatus.HumanFeedback &&
                    workItem.humanFeedbackReason !== "Human Input Needed" &&
                    workItem.humanFeedbackReason !== "PR Awaiting Merge" && (
                      <>
                        <button
                          type="button"
                          className="btn btn-sm btn-warning"
                          onClick={handleCleanupDone}
                        >
                          Cleanup -&gt; Done
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-secondary"
                          onClick={handleCleanupBacklog}
                        >
                          Cleanup -&gt; Backlog
                        </button>
                      </>
                    )}
                </div>
              </Accordion>

              {/* QA Accordion */}
              {workItem.worktreePath && (
                <Accordion
                  title="QA"
                  status={`(${(preview?.state ?? "stopped").charAt(0).toUpperCase() + (preview?.state ?? "stopped").slice(1)})`}
                >
                  <div className="detail-section">
                    <div className="preview-summary">
                      <span className="detail-value">
                        {previewLoading ? "Checking preview..." : (preview?.state ?? "stopped")}
                      </span>
                      {preview?.profileName && (
                        <span className="run-time">profile: {preview.profileName}</span>
                      )}
                    </div>
                    {previewError && (
                      <div className="preview-message preview-error">{previewError}</div>
                    )}
                    {!previewError && preview?.message && (
                      <div className="preview-message">{preview.message}</div>
                    )}
                    {preview?.services.length ? (
                      <div className="preview-service-list">
                        {preview.services.map((service) => (
                          <div key={service.name} className="preview-service-item">
                            <span className="detail-value">
                              {service.name}: {service.status}
                              {service.port ? ` on :${service.port}` : ""}
                            </span>
                            <label className="preview-port-label">
                              Port for {service.portAlias}
                              <input
                                type="number"
                                min="1"
                                className="pr-input preview-port-input"
                                value={previewPortInputs[service.portAlias] ?? ""}
                                onChange={(e) =>
                                  setPreviewPortInputs((prev) => ({
                                    ...prev,
                                    [service.portAlias]: e.target.value,
                                  }))
                                }
                                disabled={previewLoading || preview?.state === "running"}
                                placeholder={
                                  service.suggestedPort ? String(service.suggestedPort) : "auto"
                                }
                              />
                            </label>
                            {service.publicUrl && (
                              <a
                                href={service.publicUrl}
                                target="_blank"
                                rel="noopener noreferrer"
                                className="preview-url"
                              >
                                {service.publicUrl}
                              </a>
                            )}
                          </div>
                        ))}
                      </div>
                    ) : null}
                    <div className="preview-message">
                      Leave a port blank to let ILD choose one automatically. In Docker, only
                      published ports are reachable from the host.
                    </div>
                    <div className="feedback-actions">
                      <button
                        type="button"
                        className="btn btn-sm btn-secondary"
                        onClick={() => void refreshPreview()}
                        disabled={previewLoading}
                      >
                        Refresh
                      </button>
                      {preview?.state === "running" ? (
                        <>
                          {primaryPreviewUrl && (
                            <a
                              href={primaryPreviewUrl}
                              target="_blank"
                              rel="noopener noreferrer"
                              className="btn btn-sm btn-primary preview-open-link"
                            >
                              Open App
                            </a>
                          )}
                          <button
                            type="button"
                            className="btn btn-sm btn-warning"
                            onClick={handleStopPreview}
                            disabled={previewLoading}
                          >
                            Stop Preview
                          </button>
                        </>
                      ) : preview?.configured !== false ? (
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          onClick={handleStartPreview}
                          disabled={previewLoading}
                        >
                          Start Preview
                        </button>
                      ) : null}
                    </div>
                  </div>
                </Accordion>
              )}

              {/* Conversation Accordion */}
              {(() => {
                const messages = parseConversation(workItem);
                if (messages.length === 0) return null;
                return (
                  <Accordion title="Conversation" status={`(${messages.length})`}>
                    <div className="conversation-thread">
                      {[...messages].reverse().map((m, i) => (
                        <div
                          key={i}
                          className={`conversation-message conversation-${m.role.toLowerCase()}`}
                        >
                          <div className="conversation-message-header">
                            <strong className="conversation-message-role">
                              {m.name ?? (m.role.toLowerCase() === "human" ? "You" : "AI")}
                            </strong>
                            <span>{new Date(m.timestamp).toLocaleString()}</span>
                          </div>
                          <div className="conversation-message-content">
                            <MarkdownRenderer content={m.content} />
                          </div>
                        </div>
                      ))}
                    </div>
                  </Accordion>
                );
              })()}
            </div>

            {/* Footer */}
            <div className="modal-footer">
              <button type="button" className="btn btn-secondary" onClick={onClose}>
                Close
              </button>
            </div>
          </div>
        </div>
      ) : (
        <div className="modal-overlay" onMouseDown={onClose}>
          <div
            className="modal-content"
            onMouseDown={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-label={workItem ? "Edit work item" : "New work item"}
          >
            <div className="modal-header">
              <h2>{workItem ? "Edit Work Item" : "New Work Item"}</h2>
              <button className="modal-close" onClick={onClose}>
                &times;
              </button>
            </div>
            <form onSubmit={handleSubmit}>
              <div className="modal-body">
                <div className="form-group">
                  <label htmlFor="title">Title</label>
                  <input
                    id="title"
                    type="text"
                    value={title}
                    onChange={(e) => setTitle(e.target.value)}
                    required
                  />
                </div>
                <div className="form-group">
                  <label htmlFor="description">Description</label>
                  <textarea
                    id="description"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    rows={3}
                  />
                </div>
                <div className="form-row">
                  <div className="form-group">
                    <label htmlFor="repository">Repository</label>
                    <select
                      id="repository"
                      value={repositoryId}
                      onChange={(e) => setRepositoryId(e.target.value)}
                      required
                    >
                      <option value="">Select repository...</option>
                      {repositories.map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.name}
                        </option>
                      ))}
                    </select>
                  </div>
                </div>
                <div className="form-row">
                  <div className="form-group">
                    <label htmlFor="status">Status</label>
                    <select
                      id="status"
                      value={status}
                      onChange={(e) => setStatus(e.target.value as WorkItemStatus)}
                    >
                      <option value={WorkItemStatus.Backlog}>Backlog</option>
                      <option value={WorkItemStatus.WorkQueue}>Work Queue</option>
                      <option value={WorkItemStatus.Ready}>Ready</option>
                      <option value={WorkItemStatus.Running}>Running</option>
                      <option value={WorkItemStatus.HumanFeedback}>Human Feedback</option>
                      <option value={WorkItemStatus.Done}>Done</option>
                    </select>
                  </div>
                  <div className="form-group">
                    <label htmlFor="priority">Priority</label>
                    <select
                      id="priority"
                      value={priority}
                      onChange={(e) => setPriority(e.target.value as WorkItemPriority)}
                    >
                      <option value={WorkItemPriority.Low}>Low</option>
                      <option value={WorkItemPriority.Medium}>Medium</option>
                      <option value={WorkItemPriority.High}>High</option>
                      <option value={WorkItemPriority.Critical}>Critical</option>
                    </select>
                  </div>
                </div>
                <div className="form-group">
                  <label htmlFor="tags">
                    Tags (comma separated) — each tag must match a loop template name
                  </label>
                  <TagAutocomplete
                    id="tags"
                    value={tags}
                    onChange={setTags}
                    options={templates.map((t) => t.name)}
                    placeholder="e.g. build, deploy"
                  />
                </div>
                {submitError && (
                  <div role="alert" className="form-error">
                    {submitError}
                  </div>
                )}
              </div>
              <div className="modal-footer">
                <button
                  type="button"
                  className="btn btn-secondary"
                  onClick={onClose}
                  disabled={submitting}
                >
                  Cancel
                </button>
                <button type="submit" className="btn btn-primary" disabled={submitting}>
                  {submitting ? "Saving..." : workItem ? "Update" : "Create"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
      {deleteError && (
        <div className="delete-error-banner" role="alert">
          {deleteError}
          <button type="button" className="delete-error-close" onClick={() => setDeleteError(null)}>
            ×
          </button>
        </div>
      )}
      <ConfirmModal
        isOpen={showDeleteConfirm && !!workItem}
        title="Delete Work Item"
        message={`Are you sure you want to delete "${workItem?.title}"?`}
        onConfirm={handleDeleteConfirm}
        onCancel={() => setShowDeleteConfirm(false)}
      />
      {showTerminal && workItem?.currentLoopRunId && (
        <LoopRunTerminal
          loopRunId={workItem.currentLoopRunId}
          title={`#${workItem.id} — ${workItem.title}`}
          onClose={() => setShowTerminal(false)}
        />
      )}
    </>
  );
}
