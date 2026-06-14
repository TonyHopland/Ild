import { useState } from "react";
import { Link } from "react-router-dom";
import { WorkItem, WorkItemStatus } from "../../types";
import { makeLoopTagMatcher, parseConversation, parseTags } from "../../utils/workItemJson";
import MarkdownRenderer from "../MarkdownRenderer";
import FeedbackActions from "../FeedbackActions";
import type { WorkItemDetail } from "./useWorkItemDetail";

/** Prominent feedback banner pinned above the tab content while the item waits on a human. */
export function FeedbackBanner({
  workItem,
  detail,
  prompt,
}: {
  workItem: WorkItem;
  detail: WorkItemDetail;
  prompt: string | null;
}) {
  if (workItem.status !== WorkItemStatus.HumanFeedback || !workItem.humanFeedbackReason) {
    return null;
  }

  const isInput = workItem.humanFeedbackReason === "Human Input Needed";
  const isPr = workItem.humanFeedbackReason === "PR Awaiting Merge";

  if (!isInput && !isPr) {
    return (
      <div className="wiv2-feedback">
        <div className="wiv2-feedback-title">Human Feedback</div>
        <div className="feedback-reason">{workItem.humanFeedbackReason}</div>
      </div>
    );
  }

  return (
    <div className="wiv2-feedback">
      <div className="wiv2-feedback-title">
        {isPr ? "PR Feedback" : "Human Feedback"}
        {isPr && workItem.prUrl && (
          <a
            className="feedback-pr-link"
            href={workItem.prUrl}
            target="_blank"
            rel="noopener noreferrer"
          >
            Open PR
          </a>
        )}
      </div>
      {prompt && (
        <div className="markdown-container feedback-prompt">
          <MarkdownRenderer content={prompt} />
        </div>
      )}
      <textarea
        className="feedback-textarea"
        value={detail.feedbackInput}
        onChange={(e) => detail.setFeedbackInput(e.target.value)}
        placeholder={
          isPr
            ? detail.prCommentsLoading
              ? "Loading PR comments..."
              : "Optional feedback for the next node..."
            : "Optional input or context..."
        }
        rows={isPr ? 5 : 3}
      />
      <FeedbackActions
        actions={workItem.humanFeedbackActions}
        onApprove={detail.handleApprove}
        onReject={detail.handleReject}
        onEdge={detail.handleEdge}
      />
    </div>
  );
}

/** Full-height conversation thread, newest message first. */
export function ConversationPanel({ workItem }: { workItem: WorkItem }) {
  const messages = parseConversation(workItem);
  if (messages.length === 0) {
    return <div className="wiv2-empty">No conversation yet.</div>;
  }
  return (
    <div className="conversation-thread wiv2-conversation">
      {[...messages].reverse().map((m, i) => (
        <div key={i} className={`conversation-message conversation-${m.role.toLowerCase()}`}>
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
  );
}

/** Preview tab: worktree preview state, services and start/stop controls. */
export function PreviewPanel({ workItem, detail }: { workItem: WorkItem; detail: WorkItemDetail }) {
  const { preview, previewLoading, previewError } = detail;

  if (!workItem.worktreePath) {
    return (
      <div className="wiv2-empty">
        No worktree — QA preview is only available for items with an active worktree.
      </div>
    );
  }

  const primaryPreviewUrl =
    preview?.services.find((service) => !!service.publicUrl)?.publicUrl ?? null;

  return (
    <div className="wiv2-preview">
      <div className="preview-summary">
        <span className="detail-value">
          {previewLoading ? "Checking preview..." : (preview?.state ?? "stopped")}
        </span>
        {preview?.profileName && <span className="run-time">profile: {preview.profileName}</span>}
      </div>
      {previewError && <div className="preview-message preview-error">{previewError}</div>}
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
      <div className="feedback-actions">
        <button
          type="button"
          className="btn btn-sm btn-secondary"
          onClick={() => void detail.refreshPreview()}
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
              onClick={() => void detail.handleStopPreview()}
              disabled={previewLoading}
            >
              Stop Preview
            </button>
          </>
        ) : preview?.configured !== false ? (
          <button
            type="button"
            className="btn btn-sm btn-primary"
            onClick={() => void detail.handleStartPreview()}
            disabled={previewLoading}
          >
            Start Preview
          </button>
        ) : null}
      </div>
      <div className="preview-message">
        Ports are chosen automatically in the mockups — use the classic dialog for port overrides.
      </div>
    </div>
  );
}

/**
 * Metadata block: status, priority, repo, tags, PR, dependencies, dates.
 * Used as the persistent sidebar in the split variant and as an overview
 * column in the tab/rail variants.
 */
export function MetaPanel({ workItem, detail }: { workItem: WorkItem; detail: WorkItemDetail }) {
  const [showLinkPr, setShowLinkPr] = useState(false);
  const [prUrlInput, setPrUrlInput] = useState("");
  const [showAddDep, setShowAddDep] = useState(false);
  const [selectedDepId, setSelectedDepId] = useState("");

  const repoName =
    detail.repositories.find((r) => r.id === workItem.repositoryId)?.name ?? workItem.repositoryId;
  const tagList = parseTags(workItem);
  const isLoopTag = makeLoopTagMatcher(detail.templates.map((t) => t.name));
  const fmt = (d: string | null) => (d ? new Date(d).toLocaleString() : "—");

  // While the item is mid run, surface the pinned loop's name and the node the
  // engine is currently on. Both come from the current run's detail.
  const { currentRun } = detail;
  const loopName = detail.templates.find((t) => t.id === currentRun?.loopTemplateId)?.name ?? null;
  const currentNodeLabel =
    currentRun?.nodes.find((n) => n.nodeId === currentRun.currentNodeId)?.nodeLabel ?? null;
  const isRunning = workItem.status === WorkItemStatus.Running;

  return (
    <div className="wiv2-meta">
      <div className="wiv2-meta-row">
        <span className="detail-label">Status</span>
        <span className={`status-badge status-${workItem.status.toLowerCase()}`}>
          {workItem.status}
        </span>
      </div>
      <div className="wiv2-meta-row">
        <span className="detail-label">Priority</span>
        <span className="detail-value">{workItem.priority}</span>
      </div>
      <div className="wiv2-meta-row">
        <span className="detail-label">Repository</span>
        <span className="detail-value">{repoName}</span>
      </div>
      {isRunning && loopName && (
        <div className="wiv2-meta-row">
          <span className="detail-label">Loop</span>
          <span className="detail-value">
            {loopName}
            {currentNodeLabel && <span className="wiv2-meta-node"> · {currentNodeLabel}</span>}
          </span>
        </div>
      )}
      {tagList.length > 0 && (
        <div className="wiv2-meta-row">
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
      )}
      <div className="wiv2-meta-row wiv2-meta-col">
        <span className="detail-label">Pull Request</span>
        {workItem.prUrl ? (
          <a href={workItem.prUrl} target="_blank" rel="noopener noreferrer" className="pr-link">
            {workItem.prUrl}
          </a>
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
                onClick={() => {
                  if (!prUrlInput.trim()) return;
                  void detail.handleLinkPr(prUrlInput.trim()).then(() => {
                    setShowLinkPr(false);
                    setPrUrlInput("");
                  });
                }}
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
            className="btn btn-sm btn-secondary wiv2-meta-btn"
            onClick={() => setShowLinkPr(true)}
          >
            Link PR
          </button>
        )}
      </div>
      <div className="wiv2-meta-row wiv2-meta-col">
        <span className="detail-label">Dependencies</span>
        <div className="dependency-list">
          {detail.dependencies.length === 0 && <span className="detail-value">None</span>}
          {detail.dependencies.map((dep) => (
            <span key={dep.id} className="dependency-tag">
              <Link to={`/workitems/${dep.id}`} className="dependency-link">
                {dep.title}
              </Link>
              <button
                type="button"
                className="dependency-remove-btn"
                onClick={() => void detail.handleRemoveDependency(dep.id)}
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
              {detail.allWorkItems
                .filter(
                  (w) => w.id !== workItem.id && !detail.dependencies.some((d) => d.id === w.id),
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
                onClick={() => {
                  if (!selectedDepId) return;
                  void detail.handleAddDependency(selectedDepId).then(() => {
                    setShowAddDep(false);
                    setSelectedDepId("");
                  });
                }}
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
            className="btn btn-sm btn-secondary wiv2-meta-btn"
            onClick={() => setShowAddDep(true)}
          >
            Add Dependency
          </button>
        )}
      </div>
      <div className="wiv2-meta-row">
        <span className="detail-label">Created</span>
        <span className="detail-value wiv2-meta-date">{fmt(workItem.createdAt)}</span>
      </div>
      <div className="wiv2-meta-row">
        <span className="detail-label">Started</span>
        <span className="detail-value wiv2-meta-date">{fmt(workItem.startedAt)}</span>
      </div>
      <div className="wiv2-meta-row">
        <span className="detail-label">Completed</span>
        <span className="detail-value wiv2-meta-date">{fmt(workItem.completedAt)}</span>
      </div>
      {workItem.branchName && (
        <div className="wiv2-meta-row wiv2-meta-col">
          <span className="detail-label">Branch</span>
          <span className="detail-value wiv2-meta-mono">{workItem.branchName}</span>
          {workItem.worktreePath && (
            <>
              <button
                type="button"
                className="btn btn-sm btn-secondary wiv2-meta-btn"
                onClick={() => void detail.handlePushBranch()}
                disabled={detail.pushBranchLoading}
                title="Commit all changes and push this branch to origin"
              >
                {detail.pushBranchLoading ? "Pushing..." : "Push branch"}
              </button>
              {detail.pushBranchError && (
                <span className="preview-message preview-error">{detail.pushBranchError}</span>
              )}
              {!detail.pushBranchError && detail.pushBranchMessage && (
                <span className="preview-message">{detail.pushBranchMessage}</span>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

/** Description as rendered markdown, used by the Overview tab. */
export function DescriptionPanel({ workItem }: { workItem: WorkItem }) {
  if (!workItem.description) {
    return <div className="wiv2-empty">No description.</div>;
  }
  return (
    <div className="markdown-container wiv2-description">
      <MarkdownRenderer content={workItem.description} />
    </div>
  );
}
