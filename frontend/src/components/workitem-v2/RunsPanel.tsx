import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import {
  WorkItem,
  WorkItemStatus,
  LoopRun,
  LoopRunNode,
  LoopRunStatus,
  LoopRunNodeStatus,
  NodeType,
} from "../../types";
import { loopRunService } from "../../services/auth";
import { formatDuration } from "../../utils/duration";
import { nodeIconOf } from "../../utils/nodeStyles";
import LiveStream from "../NodeTimeline/LiveStream";
import MarkdownRenderer from "../MarkdownRenderer";
import EdgeArrow from "../NodeTimeline/EdgeArrow";
import {
  failureMessage,
  NodeEvents,
  RunSessions,
  RunVariables,
  useRunEvents,
  type RunEvents,
} from "./RunDetailSections";
import type { VersionGraph } from "./useWorkItemDetail";
import { useRunActionLock, type RunAction, type RunActionLock } from "./useRunActionLock";
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

// Nodes record the plain text they started with (the rendered prompt, the
// command, the message shown). Older runs recorded a JSON object carrying that
// text, or only the node type when there was none.
function inputTextOf(node: LoopRunNode): string | null {
  const raw = node.effectiveInput;
  if (!raw) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return raw;
  }
  if (!parsed || typeof parsed !== "object") return raw;
  const input = parsed as EffectiveInput;
  const text = input.resolvedPrompt ?? input.prompt ?? input.command ?? input.message;
  if (text !== undefined) return text;
  return "nodeType" in input ? null : raw;
}

// An AI node's prompt and answer are Markdown. Rendered as such only when the
// loop graph says the node is an AI node; otherwise shown as recorded.
function NodeText({ text, nodeType }: { text: string; nodeType: NodeType | undefined }) {
  return nodeType === NodeType.AI ? (
    <MarkdownRenderer content={text} className="wiv2-node-markdown" />
  ) : (
    <pre className="wiv2-node-pre">{text}</pre>
  );
}

function NodeRow({
  node,
  nodeType,
  isLive,
  progressText,
  events,
  onRetry,
  retryDisabled,
}: {
  node: LoopRunNode;
  /** The node's type in the run's loop version; unknown when the graph is not loaded. */
  nodeType: NodeType | undefined;
  isLive: boolean;
  progressText: string;
  events: RunEvents;
  onRetry: (runNodeId: string) => void;
  retryDisabled: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const inputText = inputTextOf(node);
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
          {nodeType && (
            <span className="wiv2-node-type" title={nodeType}>
              {nodeIconOf(nodeType)} {nodeType}
            </span>
          )}
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
          <div className="wiv2-node-section">
            <span className="detail-label">Input</span>
            {inputText ? (
              <NodeText text={inputText} nodeType={nodeType} />
            ) : (
              <div className="wiv2-empty">No input recorded.</div>
            )}
          </div>
          {isLive ? (
            <div className="wiv2-node-section">
              <LiveStream text={progressText} />
            </div>
          ) : (
            <div className="wiv2-node-section">
              <span className="detail-label">Output</span>
              {node.output ? (
                <NodeText text={node.output} nodeType={nodeType} />
              ) : (
                <div className="wiv2-empty">No output recorded.</div>
              )}
            </div>
          )}
          {node.error && (
            <div className="wiv2-node-section">
              <span className="detail-label">Error</span>
              <pre className="wiv2-node-pre wiv2-node-error">{node.error}</pre>
            </div>
          )}
          <div className="wiv2-node-section">
            <NodeEvents runNodeId={node.id} events={events} />
          </div>
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
  /**
   * Read a loop version's graph, for each node's type and the edge a run
   * recorded into it. Without it neither is shown.
   */
  readVersionGraph?: (loopTemplateId: string, templateVersion: number) => Promise<VersionGraph>;
  /**
   * The dialog's one-action-at-a-time-per-run lock, shared with its other
   * controls that act on a run. Without it the panel keeps a lock of its own.
   */
  runLock?: RunActionLock;
}

type RunDetailProps = Omit<RunsPanelProps, "runs" | "runLock"> & {
  runId: string;
  runLock: RunActionLock;
  /** Called once a delete of this run went through. */
  onDeleted: (runId: string) => void;
};

/**
 * One run's detail, actions and node timeline. Rendered keyed by run id, so
 * everything here (the run as read, loading, the error, the confirmations,
 * the event log, the graph) belongs to that one run and is discarded when
 * another run is shown; nothing of one run can be shown or acted on as
 * another's (ADR-0021).
 */
function RunDetail({
  runId,
  workItem,
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
  readVersionGraph,
  runLock,
  onDeleted,
}: RunDetailProps) {
  const [runDetail, setRunDetail] = useState<LoopRun | null>(null);
  const [loading, setLoading] = useState(true);
  const [confirmingReclaim, setConfirmingReclaim] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [errorText, setErrorText] = useState("");
  // Best effort: without the graph no node types or edges are shown.
  const [graph, setGraph] = useState<VersionGraph | null>(null);
  // Reads of this run, in the order they started: only the latest one started
  // is applied, so a read begun before an action settled never overwrites the
  // read made after it.
  const reads = useRef(0);

  const runEvents = useRunEvents(runDetail);

  const readRun = useCallback(async () => {
    const read = ++reads.current;
    const data = await loopRunService.getById(runId).then(normalizeRun, () => null);
    if (read !== reads.current) return;
    setRunDetail(data);
    setLoading(false);
  }, [runId]);

  // workItem identity doubles as a refresh trigger: the parent refetches the
  // work item on every node/run state change, so the inline timeline stays
  // current without its own SignalR subscription.
  useEffect(() => {
    void readRun();
  }, [readRun, workItem]);

  const templateId = runDetail?.loopTemplateId;
  const templateVersion = runDetail?.templateVersion;
  useEffect(() => {
    if (!readVersionGraph || !templateId || !templateVersion) return;
    let cancelled = false;
    readVersionGraph(templateId, templateVersion).then(
      (read) => {
        if (!cancelled) setGraph(read);
      },
      () => {
        if (!cancelled) setGraph(null);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [readVersionGraph, templateId, templateVersion]);

  // Shows a refused action's reason. Once it went through, refreshes the run
  // list and says so, for the caller to apply the action's effect.
  const settleAction = async (action: Promise<unknown>, fallback: string) => {
    try {
      await action;
    } catch (error) {
      setErrorText(failureMessage(error, fallback));
      return false;
    }
    onRunsChanged?.();
    return true;
  };

  const actOnRun = async (
    kind: RunAction,
    action: (runId: string) => Promise<unknown>,
    fallback: string,
    applied: () => void | Promise<void> = readRun,
  ) => {
    setErrorText("");
    await runLock.hold(runId, kind, async () => {
      if (await settleAction(action(runId), fallback)) await applied();
    });
  };
  const pending = runLock.pendingOf(runId);
  const busy = pending !== null;

  // Halt, steer and abandon act on the work item's current run, which need not
  // be the run shown here; they hold that run's slot. Their handlers come from
  // the dialog and handle their own failures.
  const currentRunId = workItem.currentLoopRunId;
  const holdingCurrentRun = <A extends unknown[]>(
    kind: RunAction,
    handler?: (...args: A) => void | Promise<unknown>,
  ) => handler && ((...args: A) => runLock.hold(currentRunId, kind, () => handler(...args)));

  const handleRetry = (runNodeId: string) =>
    actOnRun(
      "retry",
      (id) => loopRunService.retryFromNode(id, runNodeId),
      "Failed to retry from node.",
    );

  const handleReclaim = (reclaimRun: (runId: string) => Promise<unknown>) =>
    actOnRun("reclaim", reclaimRun, "Failed to free the run's worktree and branch.", async () => {
      setConfirmingReclaim(false);
      await readRun();
    });

  const handleDelete = async (deleteRun: (runId: string) => Promise<unknown>) => {
    await actOnRun("delete", deleteRun, "Failed to delete run.", () => onDeleted(runId));
    setConfirmingDelete(false);
  };

  if (!runDetail) {
    return (
      <div className="wiv2-empty">
        {loading ? "Loading run..." : "This run could not be loaded."}
      </div>
    );
  }

  // Retrying restarts the run, so it is blocked while the run is actively
  // executing (a paused run can still be retried) or while any action on it is
  // in flight.
  const retryDisabled = busy || (runDetail.status === LoopRunStatus.Running && !runDetail.isPaused);

  const isLiveRun =
    runDetail.id === workItem.currentLoopRunId && workItem.status === WorkItemStatus.Running;

  // A finished run keeps its worktree and branch so it stays inspectable
  // (ADR-0008), which is what blocks a later run wanting the same branch name.
  // Offered only once there is something left to reclaim.
  const canReclaim =
    (runDetail.status === LoopRunStatus.Completed ||
      runDetail.status === LoopRunStatus.Failed ||
      runDetail.status === LoopRunStatus.Cancelled) &&
    !!runDetail.hasLocalGitState;

  return (
    <>
      {errorText && (
        <div className="wiv2-error" role="alert">
          {errorText}
          <button type="button" className="wiv2-error-close" onClick={() => setErrorText("")}>
            ✕
          </button>
        </div>
      )}
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
        <button
          type="button"
          className={`btn btn-sm ${runDetail.retain ? "btn-primary" : "btn-secondary"}`}
          aria-pressed={!!runDetail.retain}
          disabled={busy}
          onClick={() =>
            void actOnRun(
              "retain",
              (id) => loopRunService.setRetain(id, !runDetail.retain),
              "Failed to update retain.",
            )
          }
          title={
            runDetail.retain
              ? "Pinned: this run is kept and never auto-deleted. Click to unpin."
              : "Pin this run so its worktree, branch, and history are never auto-deleted."
          }
        >
          {runDetail.retain ? "📌 Retained" : "Retain"}
        </button>
        {onReclaimRun &&
          canReclaim &&
          (confirmingReclaim ? (
            <span className="wiv2-abandon-confirm" role="group" aria-label="Confirm clean up run">
              <span className="wiv2-abandon-prompt">
                Delete this run&rsquo;s worktree and local branch? The run and its history are kept.
              </span>
              <button
                type="button"
                className="btn btn-sm btn-danger"
                onClick={() => void handleReclaim(onReclaimRun)}
                disabled={busy}
              >
                {pending === "reclaim" ? "Cleaning up…" : "Confirm clean up"}
              </button>
              <button
                type="button"
                className="btn btn-sm btn-secondary"
                onClick={() => setConfirmingReclaim(false)}
                disabled={busy}
              >
                Cancel
              </button>
            </span>
          ) : (
            <button
              type="button"
              className="btn btn-sm btn-secondary"
              onClick={() => setConfirmingReclaim(true)}
              disabled={busy}
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
                    disabled={busy}
                    onClick={() => void actOnRun("resume", onResumeRun, "Failed to resume run.")}
                  >
                    Resume run
                  </button>
                )
              : onPauseRun && (
                  <button
                    type="button"
                    className="btn btn-sm btn-secondary"
                    disabled={busy}
                    onClick={() => void actOnRun("pause", onPauseRun, "Failed to pause run.")}
                  >
                    Pause run
                  </button>
                )}
            {onCancelRun && (
              <button
                type="button"
                className="btn btn-sm btn-danger"
                disabled={busy}
                onClick={() => void actOnRun("cancel", onCancelRun, "Failed to cancel run.")}
              >
                Cancel run
              </button>
            )}
          </>
        ) : (
          onDeleteRun &&
          (confirmingDelete ? (
            <span className="wiv2-abandon-confirm" role="group" aria-label="Confirm delete run">
              <span className="wiv2-abandon-prompt">
                Delete this loop run and all its event history?
              </span>
              <button
                type="button"
                className="btn btn-sm btn-danger"
                onClick={() => void handleDelete(onDeleteRun)}
                disabled={busy}
              >
                {pending === "delete" ? "Deleting…" : "Confirm delete"}
              </button>
              <button
                type="button"
                className="btn btn-sm btn-secondary"
                onClick={() => setConfirmingDelete(false)}
                disabled={busy}
              >
                Cancel
              </button>
            </span>
          ) : (
            <button
              type="button"
              className="btn btn-sm btn-secondary"
              onClick={() => setConfirmingDelete(true)}
              disabled={busy}
            >
              Delete run
            </button>
          ))
        )}
      </div>
      <RunCostSummary run={runDetail} />
      <RunVariables variables={runDetail.availableVariables ?? []} />
      <RunSessions runId={runDetail.id} sessions={runDetail.availableSessions ?? []} />
      <HaltSteerControls
        run={runDetail}
        workItemStatus={workItem.status}
        onHalt={holdingCurrentRun("halt", onHalt)}
        onResumeSteer={holdingCurrentRun("steer", onResumeSteer)}
        onCleanupDone={holdingCurrentRun("abandon", onCleanupDone)}
        onCleanupBacklog={holdingCurrentRun("abandon", onCleanupBacklog)}
        blocked={busy || runLock.pendingOf(currentRunId) !== null}
      />
      <div className="wiv2-node-list">
        {runDetail.nodes.length === 0 && <div className="wiv2-empty">No nodes executed yet.</div>}
        {runDetail.nodes.map((node, i) => {
          const incomingEdge =
            i > 0
              ? graph?.edges.find(
                  (e) => e.id === node.incomingEdgeId && e.targetNodeId === node.nodeId,
                )
              : undefined;
          return (
            <Fragment key={node.id}>
              {incomingEdge && (
                <EdgeArrow edgeType={incomingEdge.edgeType} edgeName={incomingEdge.name} />
              )}
              <NodeRow
                node={node}
                nodeType={graph?.nodes.find((n) => n.id === node.nodeId)?.type}
                isLive={
                  isLiveRun &&
                  i === runDetail.nodes.length - 1 &&
                  normalizeNodeStatus(node.status) === LoopRunNodeStatus.Running
                }
                progressText={progressText}
                events={runEvents}
                onRetry={(runNodeId) => void handleRetry(runNodeId)}
                retryDisabled={retryDisabled}
              />
            </Fragment>
          );
        })}
      </div>
    </>
  );
}

/**
 * Run history tab: run list on the left, the selected run on the right — its
 * actions, variables, AI sessions and node timeline with each node's events.
 * The only place runs are shown and managed.
 */
export default function RunsPanel({ runs, runLock, ...detailProps }: RunsPanelProps) {
  const { workItem } = detailProps;
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const ownRunLock = useRunActionLock();
  // Hides a deleted run until the parent's refetched `runs` drop it, and keeps
  // the selection from falling back to it. Run ids are never reused.
  const [deletedRunIds, setDeletedRunIds] = useState<ReadonlySet<string>>(new Set());

  const visibleRuns = runs.filter((run) => !deletedRunIds.has(run.id));
  const effectiveRunId =
    [selectedRunId, workItem.currentLoopRunId, visibleRuns[0]?.id].find(
      (id): id is string => !!id && !deletedRunIds.has(id),
    ) ?? null;

  const handleDeleted = useCallback((runId: string) => {
    setDeletedRunIds((prev) => new Set(prev).add(runId));
    setSelectedRunId((prev) => (prev === runId ? null : prev));
  }, []);

  if (visibleRuns.length === 0) {
    return <div className="wiv2-empty">No runs yet for this work item.</div>;
  }

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
        {effectiveRunId ? (
          <RunDetail
            key={effectiveRunId}
            {...detailProps}
            runId={effectiveRunId}
            runLock={runLock ?? ownRunLock}
            onDeleted={handleDeleted}
          />
        ) : (
          <div className="wiv2-empty">Select a run.</div>
        )}
      </div>
    </div>
  );
}
