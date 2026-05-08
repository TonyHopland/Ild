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
} from "../types";
import type { TypedSignalRMessage } from "../types/signalr";
import {
  workItemService,
  repositoryService,
  loopTemplateService,
  loopRunService,
} from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";
import LiveStream from "./NodeTimeline/LiveStream";
import ConfirmModal from "./ConfirmModal";
import { parseConversation, parseTags } from "../utils/workItemJson";

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
  const [loopTemplateId, setLoopTemplateId] = useState("");
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
  const [humanPrompt, setHumanPrompt] = useState<string | null>(null);
  const [prCommentsLoading, setPrCommentsLoading] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [progressLines, setProgressLines] = useState<string[]>([]);

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

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  useEffect(() => {
    if (
      !workItem ||
      workItem.status !== WorkItemStatus.HumanFeedback ||
      workItem.humanFeedbackReason !== "Human Input Needed"
    ) {
      setHumanPrompt(null);
      return;
    }
    const runId = (workItem as { currentLoopRunId?: string }).currentLoopRunId;
    if (!runId) {
      setHumanPrompt(null);
      return;
    }
    let cancelled = false;
    void (async () => {
      try {
        // Walk the event log backwards to find the prompt rendered for the
        // currently-suspended Human node. Use a generous page size; humans
        // pause infrequently relative to total events.
        const page = await loopRunService.getEvents(runId, 0, 500);
        if (cancelled) return;
        const rendered = [...(page.entries || [])]
          .reverse()
          .find((e) => e.eventType === "HumanPromptRendered");
        setHumanPrompt(rendered?.payload ?? null);
      } catch {
        if (!cancelled) setHumanPrompt(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [workItem?.id, workItem?.status, workItem?.humanFeedbackReason]);

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

  useEffect(() => {
    if (workItem) {
      setTitle(workItem.title);
      setDescription(workItem.description);
      setStatus(workItem.status);
      setPriority(workItem.priority);
      setTags(
        Array.isArray(workItem.labels)
          ? workItem.labels.join(", ")
          : typeof workItem.labels === "string"
            ? workItem.labels
            : "",
      );
      setRepositoryId(workItem.repositoryId);
      setLoopTemplateId(
        workItem.loopTemplateId ?? (workItem as any).loopTemplateVersion?.loopTemplateId ?? "",
      );
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
      setLoopTemplateId("");
      setShowDeleteConfirm(false);
      setDeleteError(null);
      setEditMode(true);
    }
  }, [workItem?.id, workItem?.status]);

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
      setProgressLines([]);
      return;
    }

    const onNodeProgress = (message: TypedSignalRMessage<"NodeProgress">) => {
      const { runId: msgRunId, line } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      setProgressLines((prev) => [...prev, line]);
    };

    const onLoopRunStateChanged = (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      const { runId: msgRunId } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      refetchWorkItem();
    };

    const onNodeStateChanged = (message: TypedSignalRMessage<"NodeStateChanged">) => {
      const { runId: msgRunId } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      refetchWorkItem();
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
    };
  }, [shouldStream, runOn, runOff, workItem?.currentLoopRunId, refetchWorkItem]);

  if (!isOpen) return null;

  const handleStart = async () => {
    if (!workItem) return;
    try {
      await loopRunService.trigger(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to start work item:", error);
    }
  };

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

  const handleMarkMerged = async () => {
    if (!workItem) return;
    try {
      await workItemService.markMerged(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to mark merged:", error);
    }
  };

  const handleContinue = async () => {
    if (!workItem) return;
    try {
      await workItemService.humanFeedbackInput(workItem.id, feedbackInput || "");
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to submit feedback:", error);
    }
  };

  const handleReject = async () => {
    if (!workItem) return;
    try {
      // Pass any typed feedback through to the OnFailure successor as
      // {{PreviousNode.Output}}.
      await workItemService.humanFeedbackReject(workItem.id, feedbackInput || undefined);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to reject:", error);
    }
  };

  const handleRespond = async () => {
    if (!workItem) return;
    try {
      await workItemService.humanFeedbackRespond(workItem.id, feedbackInput || "");
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to respond:", error);
    }
  };

  const handleCleanupDone = async () => {
    if (!workItem) return;
    try {
      await workItemService.cleanupToDone(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to cleanup to done:", error);
    }
  };

  const handleCleanupBacklog = async () => {
    if (!workItem) return;
    try {
      await workItemService.cleanupToBacklog(workItem.id);
      const updated = await workItemService.getById(workItem.id);
      onSave(updated);
    } catch (error) {
      console.error("Failed to cleanup to backlog:", error);
    }
  };

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
      const msg = error instanceof Error ? error.message : "Failed to delete work item";
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

    const parsedLabels = tags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    const data: Partial<WorkItem> = {
      title,
      description,
      status,
      priority,
      labels: parsedLabels,
      repositoryId,
      loopTemplateId,
    };

    try {
      let saved: WorkItem;
      if (workItem) {
        saved = await workItemService.update(workItem.id, data);
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
        saved = await workItemService.create(data);
      }
      onSave(saved);
      if (workItem) {
        setEditMode(false);
      } else {
        onClose();
      }
    } catch (error) {
      console.error("Failed to save work item:", error);
      setSubmitError(`Failed to save: ${error instanceof Error ? error.message : "Unknown error"}`);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <>
      {workItem && !editMode ? (
        <div className="modal-overlay" onClick={onClose}>
          <div
            className="modal-content modal-content-detail"
            onClick={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-label="Work item details"
          >
            <div className="modal-header">
              <h2>{workItem.title}</h2>
              <button className="modal-close" onClick={onClose}>
                &times;
              </button>
            </div>
            <div className="modal-body">
              <div className="detail-row">
                <span className="detail-label">Status</span>
                <span className={`status-badge status-${workItem.status.toLowerCase()}`}>
                  {workItem.status}
                </span>
              </div>
              {workItem.description && (
                <div className="detail-row">
                  <span className="detail-label">Description</span>
                  <span className="detail-value">{workItem.description}</span>
                </div>
              )}
              {(() => {
                const tagList = parseTags(workItem);
                if (tagList.length === 0) return null;
                return (
                  <div className="detail-row">
                    <span className="detail-label">Tags</span>
                    <span className="detail-value">
                      {tagList.map((t) => (
                        <span key={t} className="work-item-tag" style={{ marginRight: 4 }}>
                          {t}
                        </span>
                      ))}
                    </span>
                  </div>
                );
              })()}
              {(() => {
                const messages = parseConversation(workItem);
                if (messages.length === 0) return null;
                return (
                  <div className="detail-section">
                    <span className="detail-label">Conversation</span>
                    <div
                      className="conversation-thread"
                      style={{
                        display: "flex",
                        flexDirection: "column",
                        gap: 8,
                        marginTop: 4,
                        maxHeight: 320,
                        overflowY: "auto",
                      }}
                    >
                      {messages.map((m, i) => (
                        <div
                          key={i}
                          className={`conversation-message conversation-${m.role.toLowerCase()}`}
                          style={{
                            border: "1px solid #2a2a3a",
                            borderRadius: 4,
                            padding: 8,
                            background: "#1a1a24",
                          }}
                        >
                          <div
                            style={{
                              display: "flex",
                              justifyContent: "space-between",
                              fontSize: "0.75rem",
                              color: "#9ca3af",
                              marginBottom: 4,
                            }}
                          >
                            <strong style={{ color: "#e5e7eb" }}>{m.role}</strong>
                            <span>{new Date(m.timestamp).toLocaleString()}</span>
                          </div>
                          <div style={{ whiteSpace: "pre-wrap" }}>{m.content}</div>
                        </div>
                      ))}
                    </div>
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
                        <span className="run-time">{new Date(run.startedAt).toLocaleString()}</span>
                      </Link>
                    ))}
                  </div>
                </div>
              )}
              {shouldStream && <LiveStream lines={progressLines} />}
              {workItem.status === WorkItemStatus.HumanFeedback &&
                workItem.humanFeedbackReason === "Human Input Needed" && (
                  <div className="detail-section human-feedback-section">
                    <span className="detail-label">Human Feedback</span>
                    {humanPrompt && <pre className="feedback-prompt">{humanPrompt}</pre>}
                    <textarea
                      className="feedback-textarea"
                      value={feedbackInput}
                      onChange={(e) => setFeedbackInput(e.target.value)}
                      placeholder="Optional input or context..."
                      rows={3}
                    />
                    {(() => {
                      const actions = workItem.humanFeedbackActions
                        ? workItem.humanFeedbackActions.split(",").map((a) => a.trim())
                        : ["OnSuccess", "OnFailure"];
                      return (
                        <div className="feedback-actions">
                          {actions.includes("OnSuccess") && (
                            <button
                              type="button"
                              className="btn btn-sm btn-primary"
                              onClick={handleContinue}
                            >
                              Approve
                            </button>
                          )}
                          {actions.includes("OnRespond") && (
                            <button
                              type="button"
                              className="btn btn-sm btn-warning"
                              onClick={handleRespond}
                            >
                              Respond
                            </button>
                          )}
                          {actions.includes("OnFailure") && (
                            <button
                              type="button"
                              className="btn btn-sm btn-danger"
                              onClick={handleReject}
                            >
                              Reject
                            </button>
                          )}
                        </div>
                      );
                    })()}
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
                    <div className="feedback-actions">
                      <button
                        type="button"
                        className="btn btn-sm btn-primary"
                        onClick={handleContinue}
                      >
                        Approve
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-danger"
                        onClick={handleReject}
                      >
                        Reject
                      </button>
                    </div>
                  </div>
                )}
              {workItem.status === WorkItemStatus.HumanFeedback &&
                workItem.humanFeedbackReason &&
                workItem.humanFeedbackReason !== "Human Input Needed" &&
                workItem.humanFeedbackReason !== "PR Awaiting Merge" && (
                  <div className="detail-section human-feedback-section">
                    <span className="detail-label">Human Feedback</span>
                    <div className="feedback-reason">{workItem.humanFeedbackReason}</div>
                    <div className="feedback-actions">
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
                    </div>
                  </div>
                )}
            </div>
            <div className="modal-footer">
              {workItem.status === WorkItemStatus.Ready && (
                <button type="button" className="btn btn-primary" onClick={handleStart}>
                  Start
                </button>
              )}
              {workItem.prUrl && (
                <button type="button" className="btn btn-success" onClick={handleMarkMerged}>
                  Mark Merged
                </button>
              )}
              <button type="button" className="btn btn-edit" onClick={() => setEditMode(true)}>
                Edit
              </button>
              <button type="button" className="btn btn-danger" onClick={handleDelete}>
                Delete
              </button>
              <button type="button" className="btn btn-secondary" onClick={onClose}>
                Close
              </button>
            </div>
          </div>
        </div>
      ) : (
        <div className="modal-overlay" onClick={onClose}>
          <div
            className="modal-content"
            onClick={(e) => e.stopPropagation()}
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
                    >
                      <option value="">Select repository...</option>
                      {repositories.map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.name}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="form-group">
                    <label htmlFor="loopTemplate">Loop Template</label>
                    <select
                      id="loopTemplate"
                      value={loopTemplateId}
                      onChange={(e) => setLoopTemplateId(e.target.value)}
                    >
                      <option value="">Select template...</option>
                      {templates.map((t) => (
                        <option key={t.id} value={t.id}>
                          {t.name}
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
                  <label htmlFor="tags">Labels (comma separated)</label>
                  <input
                    id="tags"
                    type="text"
                    value={tags}
                    onChange={(e) => setTags(e.target.value)}
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
    </>
  );
}
