import { useState, useEffect } from "react";
import { Link } from "react-router";
import {
  WorkItem,
  WorkItemStatus,
  LoopRun,
  LoopRunNode,
  LoopRunStatus,
  LoopRunNodeStatus,
} from "../../types";
import { loopRunService } from "../../services/auth";
import { formatDuration } from "../../utils/duration";
import LiveStream from "../NodeTimeline/LiveStream";
import HaltSteerControls from "./HaltSteerControls";
import RunCostSummary from "./RunCostSummary";

interface EffectiveInput {
  nodeType?: string;
  command?: string;
  prompt?: string;
  resolvedPrompt?: string;
  message?: string;
}

function normalizeRunStatus(value: unknown): LoopRunStatus {
  if (typeof value === "string") return value as LoopRunStatus;
  if (typeof value === "number") {
    const map: Record<number, LoopRunStatus> = {
      0: LoopRunStatus.Running,
      1: LoopRunStatus.Completed,
      2: LoopRunStatus.Failed,
      3: LoopRunStatus.Cancelled,
      4: LoopRunStatus.WaitingHuman,
    };
    return map[value] ?? LoopRunStatus.Running;
  }
  return LoopRunStatus.Running;
}

function normalizeNodeStatus(value: unknown): LoopRunNodeStatus {
  if (typeof value === "string") return value as LoopRunNodeStatus;
  if (typeof value === "number") {
    const map: Record<number, LoopRunNodeStatus> = {
      0: LoopRunNodeStatus.Pending,
      1: LoopRunNodeStatus.Running,
      2: LoopRunNodeStatus.Succeeded,
      3: LoopRunNodeStatus.Failed,
      4: LoopRunNodeStatus.Skipped,
      5: LoopRunNodeStatus.WaitingHuman,
      6: LoopRunNodeStatus.Interrupted,
    };
    return map[value] ?? LoopRunNodeStatus.Pending;
  }
  return LoopRunNodeStatus.Pending;
}

function normalizeRun(data: LoopRun): LoopRun {
  return {
    ...data,
    status: normalizeRunStatus(data.status),
    nodes: data.nodes.map((n) => ({ ...n, status: normalizeNodeStatus(n.status) })),
  };
}

// The API client rejects with a plain { status, message } object, not an Error.
function failureMessage(error: unknown, fallback: string): string {
  const message = (error as { message?: unknown } | null)?.message;
  return typeof message === "string" && message ? message : fallback;
}

function parseEffectiveInput(node: LoopRunNode): EffectiveInput | null {
  if (!node.effectiveInput) return null;
  try {
    return JSON.parse(node.effectiveInput) as EffectiveInput;
  } catch {
    return null;
  }
}

function NodeRow({
  node,
  isLive,
  progressText,
  onRetry,
  retryDisabled,
}: {
  node: LoopRunNode;
  isLive: boolean;
  progressText: string;
  onRetry: (runNodeId: string) => void;
  retryDisabled: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const input = parseEffectiveInput(node);
  const inputText = input?.resolvedPrompt ?? input?.prompt ?? input?.command ?? input?.message;
  const duration = formatDuration(node.startedAt, node.completedAt);
  const status = normalizeNodeStatus(node.status);

  return (
    <div className={`wiv2-node ${expanded ? "wiv2-node-expanded" : ""}`}>
      <div className="wiv2-node-header-row">
        <button type="button" className="wiv2-node-header" onClick={() => setExpanded((p) => !p)}>
          <span
            className={`wiv2-node-dot wiv2-node-dot-${status.toLowerCase()}`}
            aria-hidden="true"
          />
          <span className="wiv2-node-label">{node.nodeLabel}</span>
          {node.executionCount > 1 && (
            <span className="wiv2-node-count">×{node.executionCount}</span>
          )}
          <span className="wiv2-node-meta">
            {status}
            {duration ? ` · ${duration}` : ""}
          </span>
          <span className="wiv2-node-chevron">{expanded ? "▾" : "▸"}</span>
        </button>
        {status !== LoopRunNodeStatus.Running && (
          <button
            type="button"
            className="wiv2-node-retry"
            disabled={retryDisabled}
            title="Retry from this node with the same input as last time"
            aria-label="Retry from this node"
            onClick={() => onRetry(node.id)}
          >
            ↻ Retry
          </button>
        )}
      </div>
      {expanded && (
        <div className="wiv2-node-body">
          {isLive ? (
            <LiveStream text={progressText} />
          ) : (
            <>
              {inputText && (
                <div className="wiv2-node-section">
                  <span className="detail-label">Input</span>
                  <pre className="wiv2-node-pre">{inputText}</pre>
                </div>
              )}
              {node.output && (
                <div className="wiv2-node-section">
                  <span className="detail-label">Output</span>
                  <pre className="wiv2-node-pre">{node.output}</pre>
                </div>
              )}
              {node.error && (
                <div className="wiv2-node-section">
                  <span className="detail-label">Error</span>
                  <pre className="wiv2-node-pre wiv2-node-error">{node.error}</pre>
                </div>
              )}
              {!inputText && !node.output && !node.error && (
                <div className="wiv2-empty">No input or output recorded.</div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

interface RunsPanelProps {
  workItem: WorkItem;
  runs: LoopRun[];
  progressText: string;
  /** Called after a retry so the parent can refresh the run list. */
  onRunsChanged?: () => void;
  /** Halt the in-flight AI node of the current run. */
  onHalt?: () => void | Promise<unknown>;
  /** Resume a halted run with optional steering guidance. */
  onResumeSteer?: (note: string) => void | Promise<unknown>;
  /** Cleanup handlers reused from the work item dialog for a halted run. */
  onCleanupDone?: () => void | Promise<unknown>;
  onCleanupBacklog?: () => void | Promise<unknown>;
  /**
   * Free a finished run's worktree and branch, keeping the run and its history.
   * Rejects (409) when the git state survives, so its error is shown.
   */
  onReclaimRun?: (runId: string) => Promise<unknown>;
  /** Pause a running run. Rejects when refused, so its error is shown. */
  onPauseRun?: (runId: string) => Promise<unknown>;
  /** Resume a paused run. Rejects when refused, so its error is shown. */
  onResumeRun?: (runId: string) => Promise<unknown>;
  /** Cancel a running run. Rejects when refused, so its error is shown. */
  onCancelRun?: (runId: string) => Promise<unknown>;
  /**
   * Delete a run that is not running, with its event history. Rejects when
   * refused (400/409/503), so its error is shown.
   */
  onDeleteRun?: (runId: string) => Promise<unknown>;
}

/**
 * Run history tab: run list on the left, the selected run's node timeline
 * inline on the right — no navigation to a separate page needed. A link to
 * the full run page is kept for the deep-dive cases (events, sessions).
 */
export default function RunsPanel({
  workItem,
  runs,
  progressText,
  onRunsChanged,
  onHalt,
  onResumeSteer,
  onCleanupDone,
  onCleanupBacklog,
  onReclaimRun,
  onPauseRun,
  onResumeRun,
  onCancelRun,
  onDeleteRun,
}: RunsPanelProps) {
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [runDetail, setRunDetail] = useState<LoopRun | null>(null);
  const [loading, setLoading] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [reclaiming, setReclaiming] = useState(false);
  const [confirmingReclaim, setConfirmingReclaim] = useState(false);
  const [errorText, setErrorText] = useState("");
  // Runs with a pause/resume/cancel/delete in flight, so switching runs never
  // frees or blocks another run's controls.
  const [busyRunIds, setBusyRunIds] = useState<ReadonlySet<string>>(new Set());
  const [confirmingDeleteRunId, setConfirmingDeleteRunId] = useState<string | null>(null);
  // Hides a deleted run until the parent's refetched `runs` drop it, and keeps
  // the selection from falling back to it. Run ids are never reused.
  const [deletedRunIds, setDeletedRunIds] = useState<ReadonlySet<string>>(new Set());

  const visibleRuns = runs.filter((run) => !deletedRunIds.has(run.id));
  const effectiveRunId =
    [selectedRunId, workItem.currentLoopRunId, visibleRuns[0]?.id].find(
      (id): id is string => !!id && !deletedRunIds.has(id),
    ) ?? null;

  useEffect(() => {
    if (!effectiveRunId) {
      setRunDetail(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setConfirmingReclaim(false);
    setConfirmingDeleteRunId(null);
    loopRunService
      .getById(effectiveRunId)
      .then((data) => {
        if (!cancelled) setRunDetail(normalizeRun(data));
      })
      .catch(() => {
        if (!cancelled) setRunDetail(null);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // workItem identity doubles as a refresh trigger: the parent refetches the
    // work item on every node/run state change, so the inline timeline stays
    // current without its own SignalR subscription.
  }, [effectiveRunId, workItem]);

  // Re-reads a run after an action on it. Best effort: the action already
  // happened, so a failed read is not reported as its failure; like the detail
  // effect's failed load it shows no detail rather than a stale one. Only
  // touches the detail while that run is still the one shown, since the user
  // may have selected another run meanwhile.
  const rereadRun = async (runId: string) => {
    try {
      const data = normalizeRun(await loopRunService.getById(runId));
      setRunDetail((prev) => (prev?.id === runId ? data : prev));
    } catch {
      setRunDetail((prev) => (prev?.id === runId ? null : prev));
    }
  };

  // Shows a refused action's reason; once it went through, refreshes the run
  // list and then applies its effect on the panel.
  const settleAction = async (
    action: Promise<unknown>,
    fallback: string,
    applied: () => void | Promise<void>,
  ) => {
    try {
      await action;
    } catch (error) {
      setErrorText(failureMessage(error, fallback));
      return;
    }
    onRunsChanged?.();
    await applied();
  };

  const handleRetry = async (runNodeId: string) => {
    if (!effectiveRunId) return;
    const runId = effectiveRunId;
    setErrorText("");
    setRetrying(true);
    try {
      await settleAction(
        loopRunService.retryFromNode(runId, runNodeId),
        "Failed to retry from node.",
        () => rereadRun(runId),
      );
    } finally {
      setRetrying(false);
    }
  };

  const handleReclaim = async () => {
    if (!onReclaimRun || !effectiveRunId) return;
    const runId = effectiveRunId;
    setErrorText("");
    setReclaiming(true);
    try {
      await settleAction(
        onReclaimRun(runId),
        "Failed to free the run's worktree and branch.",
        async () => {
          setConfirmingReclaim(false);
          await rereadRun(runId);
        },
      );
    } finally {
      setReclaiming(false);
    }
  };

  const actOnRun = async (
    runId: string,
    action: (runId: string) => Promise<unknown>,
    fallback: string,
    applied: () => void | Promise<void>,
  ) => {
    setErrorText("");
    setBusyRunIds((prev) => new Set(prev).add(runId));
    try {
      await settleAction(action(runId), fallback, applied);
    } finally {
      setBusyRunIds((prev) => {
        const next = new Set(prev);
        next.delete(runId);
        return next;
      });
    }
  };

  const handleDelete = async (runId: string, deleteRun: (runId: string) => Promise<unknown>) => {
    await actOnRun(runId, deleteRun, "Failed to delete run.", () => {
      setDeletedRunIds((prev) => new Set(prev).add(runId));
      setSelectedRunId((prev) => (prev === runId ? null : prev));
      setRunDetail((prev) => (prev?.id === runId ? null : prev));
    });
    setConfirmingDeleteRunId((prev) => (prev === runId ? null : prev));
  };

  if (visibleRuns.length === 0) {
    return <div className="wiv2-empty">No runs yet for this work item.</div>;
  }

  // Retrying restarts the run, so it is blocked while the run is actively
  // executing (a paused run can still be retried) or while a retry is in flight.
  const retryDisabled =
    retrying || (runDetail?.status === LoopRunStatus.Running && !runDetail.isPaused);

  const isLiveRun =
    runDetail?.id === workItem.currentLoopRunId && workItem.status === WorkItemStatus.Running;

  // A finished run keeps its worktree and branch so it stays inspectable
  // (ADR-0008), which is what blocks a later run wanting the same branch name.
  // Offered only once there is something left to reclaim.
  const canReclaim =
    !!onReclaimRun &&
    !!runDetail &&
    (runDetail.status === LoopRunStatus.Completed ||
      runDetail.status === LoopRunStatus.Failed ||
      runDetail.status === LoopRunStatus.Cancelled) &&
    !!runDetail.hasLocalGitState;

  const runBusy = !!runDetail && busyRunIds.has(runDetail.id);

  return (
    <div className="wiv2-runs">
      <div className="wiv2-runs-list">
        {visibleRuns.map((run) => {
          const status = normalizeRunStatus(run.status);
          return (
            <button
              key={run.id}
              type="button"
              className={`wiv2-run-item ${run.id === effectiveRunId ? "wiv2-run-item-active" : ""}`}
              onClick={() => setSelectedRunId(run.id)}
            >
              <span className={`status-badge status-${status.toLowerCase()}`}>{status}</span>
              <span className="wiv2-run-item-time">{new Date(run.startedAt).toLocaleString()}</span>
              <span className="wiv2-run-item-sub">
                Run {run.id.slice(0, 8)} · {run.id === workItem.currentLoopRunId && "current · "}
                {run.retain && "📌 "}
                {run.nodeExecutionCount} node executions
              </span>
            </button>
          );
        })}
      </div>
      <div className="wiv2-runs-detail">
        {errorText && (
          <div className="wiv2-error" role="alert">
            {errorText}
            <button type="button" className="wiv2-error-close" onClick={() => setErrorText("")}>
              ✕
            </button>
          </div>
        )}
        {loading && !runDetail && <div className="wiv2-empty">Loading run...</div>}
        {!loading && !runDetail && <div className="wiv2-empty">Select a run.</div>}
        {runDetail && (
          <>
            <div className="wiv2-runs-detail-header">
              <span className={`status-badge status-${runDetail.status.toLowerCase()}`}>
                {runDetail.status}
                {runDetail.isPaused && " (Paused)"}
              </span>
              <span className="run-time">
                Started {new Date(runDetail.startedAt).toLocaleString()}
                {runDetail.completedAt &&
                  ` · finished ${new Date(runDetail.completedAt).toLocaleString()}`}
              </span>
              <Link
                to={`/loop-runs/${runDetail.id}`}
                className="wiv2-run-full-link"
                target="_blank"
                rel="noopener noreferrer"
              >
                Open full run view ↗
              </Link>
              {canReclaim &&
                (confirmingReclaim ? (
                  <span
                    className="wiv2-abandon-confirm"
                    role="group"
                    aria-label="Confirm clean up run"
                  >
                    <span className="wiv2-abandon-prompt">
                      Delete this run&rsquo;s worktree and local branch? The run and its history are
                      kept.
                    </span>
                    <button
                      type="button"
                      className="btn btn-sm btn-danger"
                      onClick={() => void handleReclaim()}
                      disabled={reclaiming}
                    >
                      {reclaiming ? "Cleaning up…" : "Confirm clean up"}
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setConfirmingReclaim(false)}
                      disabled={reclaiming}
                    >
                      Cancel
                    </button>
                  </span>
                ) : (
                  <button
                    type="button"
                    className="btn btn-sm btn-secondary"
                    onClick={() => setConfirmingReclaim(true)}
                    title="Free this run's worktree and local branch so a new run can reuse the branch name. The run and its history are kept."
                  >
                    Clean up worktree
                  </button>
                ))}
              {runDetail.status === LoopRunStatus.Running ? (
                <>
                  {runDetail.isPaused
                    ? onResumeRun && (
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          disabled={runBusy}
                          onClick={() =>
                            void actOnRun(runDetail.id, onResumeRun, "Failed to resume run.", () =>
                              rereadRun(runDetail.id),
                            )
                          }
                        >
                          Resume run
                        </button>
                      )
                    : onPauseRun && (
                        <button
                          type="button"
                          className="btn btn-sm btn-secondary"
                          disabled={runBusy}
                          onClick={() =>
                            void actOnRun(runDetail.id, onPauseRun, "Failed to pause run.", () =>
                              rereadRun(runDetail.id),
                            )
                          }
                        >
                          Pause run
                        </button>
                      )}
                  {onCancelRun && (
                    <button
                      type="button"
                      className="btn btn-sm btn-danger"
                      disabled={runBusy}
                      onClick={() =>
                        void actOnRun(runDetail.id, onCancelRun, "Failed to cancel run.", () =>
                          rereadRun(runDetail.id),
                        )
                      }
                    >
                      Cancel run
                    </button>
                  )}
                </>
              ) : (
                onDeleteRun &&
                (confirmingDeleteRunId === runDetail.id ? (
                  <span
                    className="wiv2-abandon-confirm"
                    role="group"
                    aria-label="Confirm delete run"
                  >
                    <span className="wiv2-abandon-prompt">
                      Delete this loop run and all its event history?
                    </span>
                    <button
                      type="button"
                      className="btn btn-sm btn-danger"
                      onClick={() => void handleDelete(runDetail.id, onDeleteRun)}
                      disabled={runBusy}
                    >
                      {runBusy ? "Deleting…" : "Confirm delete"}
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => setConfirmingDeleteRunId(null)}
                      disabled={runBusy}
                    >
                      Cancel
                    </button>
                  </span>
                ) : (
                  <button
                    type="button"
                    className="btn btn-sm btn-secondary"
                    onClick={() => setConfirmingDeleteRunId(runDetail.id)}
                    disabled={runBusy}
                  >
                    Delete run
                  </button>
                ))
              )}
            </div>
            <RunCostSummary run={runDetail} />
            <HaltSteerControls
              run={runDetail}
              workItemStatus={workItem.status}
              onHalt={onHalt}
              onResumeSteer={onResumeSteer}
              onCleanupDone={onCleanupDone}
              onCleanupBacklog={onCleanupBacklog}
            />
            <div className="wiv2-node-list">
              {runDetail.nodes.length === 0 && (
                <div className="wiv2-empty">No nodes executed yet.</div>
              )}
              {runDetail.nodes.map((node, i) => (
                <NodeRow
                  key={node.id}
                  node={node}
                  isLive={
                    isLiveRun &&
                    i === runDetail.nodes.length - 1 &&
                    normalizeNodeStatus(node.status) === LoopRunNodeStatus.Running
                  }
                  progressText={progressText}
                  onRetry={handleRetry}
                  retryDisabled={retryDisabled}
                />
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
