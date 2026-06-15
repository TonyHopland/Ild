import { useState, useEffect, useCallback } from "react";
import { Link } from "react-router-dom";
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
}: RunsPanelProps) {
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [runDetail, setRunDetail] = useState<LoopRun | null>(null);
  const [loading, setLoading] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [errorText, setErrorText] = useState("");

  const effectiveRunId = selectedRunId ?? workItem.currentLoopRunId ?? runs[0]?.id ?? null;

  const reloadRunDetail = useCallback(async () => {
    if (!effectiveRunId) return;
    const data = await loopRunService.getById(effectiveRunId);
    setRunDetail(normalizeRun(data));
  }, [effectiveRunId]);

  useEffect(() => {
    if (!effectiveRunId) {
      setRunDetail(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
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

  const handleRetry = async (runNodeId: string) => {
    if (!effectiveRunId) return;
    setErrorText("");
    setRetrying(true);
    try {
      await loopRunService.retryFromNode(effectiveRunId, runNodeId);
      await reloadRunDetail();
      onRunsChanged?.();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to retry from node.");
    } finally {
      setRetrying(false);
    }
  };

  if (runs.length === 0) {
    return <div className="wiv2-empty">No runs yet for this work item.</div>;
  }

  // Retrying restarts the run, so it is blocked while the run is actively
  // executing (a paused run can still be retried) or while a retry is in flight.
  const retryDisabled =
    retrying || (runDetail?.status === LoopRunStatus.Running && !runDetail.isPaused);

  const isLiveRun =
    runDetail?.id === workItem.currentLoopRunId && workItem.status === WorkItemStatus.Running;

  return (
    <div className="wiv2-runs">
      <div className="wiv2-runs-list">
        {runs.map((run) => {
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
                {run.id === workItem.currentLoopRunId && "current · "}
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
