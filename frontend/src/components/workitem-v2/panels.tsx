import { Fragment, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { RemotePrSnapshot, WorkItem, WorkItemStatus, WorktreePreviewService } from "../../types";
import { makeLoopTagMatcher, parseConversation, parseTags } from "../../utils/workItemJson";
import { prStatusBadges } from "../../utils/prStatusBadges";
import MarkdownRenderer from "../MarkdownRenderer";
import FeedbackActions from "../FeedbackActions";
import type { WorkItemDetail } from "./useWorkItemDetail";

/** Prominent feedback banner shown in the Action tab while the item waits on a human. */
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
  const prSnapshot = isPr ? (detail.currentRun?.prSnapshot ?? null) : null;

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
      {prSnapshot && <PrView snapshot={prSnapshot} />}
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
        onMerge={isPr ? detail.handleMerge : undefined}
      />
      {isPr && detail.mergeError && (
        <div className="preview-message preview-error">{detail.mergeError}</div>
      )}
      {isPr && !detail.mergeError && detail.mergeMessage && (
        <div className="preview-message">{detail.mergeMessage}</div>
      )}
    </div>
  );
}

/**
 * Full PR view rendered from the persisted heartbeat snapshot: title, state,
 * CI/review badges, description, and the full conversation. Updates live as the
 * poller refreshes the snapshot over the run hub.
 */
export function PrView({ snapshot }: { snapshot: RemotePrSnapshot }) {
  return (
    <div className="pr-view">
      {snapshot.title && <div className="pr-view-title">{snapshot.title}</div>}
      <div className="pr-view-badges">
        {prStatusBadges(snapshot).map((badge) => (
          <span key={badge.label} className={`preview-state preview-state--${badge.tone}`}>
            {badge.label}
          </span>
        ))}
      </div>
      {snapshot.body && (
        <div className="markdown-container pr-view-description">
          <MarkdownRenderer content={snapshot.body} />
        </div>
      )}
      <div className="pr-view-conversation">
        {snapshot.conversation.length === 0 ? (
          <div className="wiv2-empty">No conversation yet.</div>
        ) : (
          snapshot.conversation.map((entry, i) => (
            <div key={i} className={`conversation-message conversation-${entry.kind}`}>
              <div className="conversation-message-header">
                <strong className="conversation-message-role">{entry.author || "unknown"}</strong>
                <span>
                  {entry.kind === "review" && entry.state ? `${entry.state} · ` : ""}
                  {new Date(entry.createdAt).toLocaleString()}
                </span>
              </div>
              {entry.body && (
                <div className="conversation-message-content">
                  <MarkdownRenderer content={entry.body} />
                </div>
              )}
            </div>
          ))
        )}
      </div>
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

/** Maps a service's raw status to the State column's Stopped/Running/Error label. */
function serviceState(status: string): { label: "Stopped" | "Running" | "Error"; tone: string } {
  if (status === "running") return { label: "Running", tone: "running" };
  if (status === "exited" || status === "failed" || status === "error")
    return { label: "Error", tone: "error" };
  return { label: "Stopped", tone: "stopped" };
}

/** One row per service: State, Name, Port (editable), Link, and a Log toggle. */
function PreviewServiceTable({
  services,
  isRunning,
  portInputs,
  onPortChange,
  detail,
}: {
  services: WorktreePreviewService[];
  isRunning: boolean;
  portInputs: Record<string, string>;
  onPortChange: (alias: string, value: string) => void;
  detail: WorkItemDetail;
}) {
  // The service whose log row is expanded, plus the fetched content keyed by
  // service name so re-opening a row shows the last load until refreshed.
  const [openLog, setOpenLog] = useState<string | null>(null);
  const [logs, setLogs] = useState<
    Record<string, { loading: boolean; error: string | null; content: string }>
  >({});

  const loadLog = (service: string) => {
    setLogs((prev) => ({
      ...prev,
      [service]: { loading: true, error: null, content: prev[service]?.content ?? "" },
    }));
    detail
      .fetchPreviewLogs(service)
      .then((content) =>
        setLogs((prev) => ({ ...prev, [service]: { loading: false, error: null, content } })),
      )
      .catch((error: { message?: string }) =>
        setLogs((prev) => ({
          ...prev,
          [service]: {
            loading: false,
            error: error?.message ?? "Failed to load log.",
            content: prev[service]?.content ?? "",
          },
        })),
      );
  };

  const toggleLog = (service: string) => {
    if (openLog === service) {
      setOpenLog(null);
      return;
    }
    setOpenLog(service);
    if (!logs[service]) loadLog(service);
  };

  return (
    <table className="preview-table">
      <thead>
        <tr>
          <th>State</th>
          <th>Name</th>
          <th>Port</th>
          <th>Link</th>
          <th>Log</th>
        </tr>
      </thead>
      <tbody>
        {services.map((service) => {
          const state = serviceState(service.status);
          const portValue =
            portInputs[service.portAlias] ?? String(service.port ?? service.suggestedPort ?? "");
          const log = logs[service.name];
          return (
            <Fragment key={service.name}>
              <tr>
                <td>
                  <span className={`preview-state preview-state--${state.tone}`}>
                    {state.label}
                  </span>
                  {service.exitCode != null && (
                    <span className="preview-exit-code"> (exit {service.exitCode})</span>
                  )}
                </td>
                <td className="preview-cell-name">{service.name}</td>
                <td>
                  {isRunning ? (
                    <span className="detail-value">{service.port ?? "—"}</span>
                  ) : (
                    <input
                      type="number"
                      className="preview-port-input"
                      value={portValue}
                      min={1}
                      aria-label={`Port for ${service.name}`}
                      onChange={(e) => onPortChange(service.portAlias, e.target.value)}
                    />
                  )}
                </td>
                <td>
                  {service.publicUrl ? (
                    <a
                      href={service.publicUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="preview-url"
                    >
                      {service.publicUrl}
                    </a>
                  ) : (
                    <span className="detail-value">—</span>
                  )}
                </td>
                <td>
                  <button
                    type="button"
                    className="btn btn-sm btn-secondary"
                    aria-expanded={openLog === service.name}
                    onClick={() => toggleLog(service.name)}
                  >
                    {openLog === service.name ? "Hide log" : "Log"}
                  </button>
                </td>
              </tr>
              {openLog === service.name && (
                <tr className="preview-log-row">
                  <td colSpan={5}>
                    <div className="preview-log-toolbar">
                      <button
                        type="button"
                        className="btn btn-sm btn-secondary"
                        onClick={() => loadLog(service.name)}
                        disabled={log?.loading}
                      >
                        {log?.loading ? "Loading..." : "Refresh log"}
                      </button>
                    </div>
                    {log?.error && <div className="preview-message preview-error">{log.error}</div>}
                    <pre className="preview-log-terminal">
                      {log?.content?.length
                        ? log.content
                        : log?.loading
                          ? "Loading..."
                          : "No log output yet."}
                    </pre>
                  </td>
                </tr>
              )}
            </Fragment>
          );
        })}
      </tbody>
    </table>
  );
}

/** Preview tab: worktree preview state, services and start/stop controls. */
export function PreviewPanel({ workItem, detail }: { workItem: WorkItem; detail: WorkItemDetail }) {
  const { preview, previewLoading, previewError } = detail;

  // User-edited port overrides keyed by port alias. Only touched aliases live
  // here so untouched services keep the backend's automatic port allocation.
  const [portInputs, setPortInputs] = useState<Record<string, string>>({});
  const serviceAliases = preview?.services?.map((s) => s.portAlias).join(",") ?? "";
  // Reset edits when the set of services changes (e.g. a different profile).
  useEffect(() => {
    setPortInputs({});
  }, [serviceAliases]);

  const isRunning = preview?.state === "running";
  const primaryPreviewUrl = useMemo(
    () => preview?.services?.find((service) => !!service.publicUrl)?.publicUrl ?? null,
    [preview],
  );

  if (!workItem.worktreePath) {
    return (
      <div className="wiv2-empty">
        No worktree — QA preview is only available for items with an active worktree.
      </div>
    );
  }

  const startPreview = () => {
    const overrides: Record<string, number> = {};
    for (const [alias, raw] of Object.entries(portInputs)) {
      const parsed = Number.parseInt(raw, 10);
      if (Number.isFinite(parsed) && parsed > 0) overrides[alias] = parsed;
    }
    void detail.handleStartPreview(overrides);
  };

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
      {preview?.services?.length ? (
        <PreviewServiceTable
          services={preview.services}
          isRunning={isRunning}
          portInputs={portInputs}
          onPortChange={(alias, value) => setPortInputs((prev) => ({ ...prev, [alias]: value }))}
          detail={detail}
        />
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
        {isRunning ? (
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
            onClick={startPreview}
            disabled={previewLoading}
          >
            Start Preview
          </button>
        ) : null}
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
