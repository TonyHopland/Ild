import { useState, useEffect, useRef, useCallback } from "react";
import { useParams, Link } from "react-router-dom";
import {
  LoopRun,
  LoopRunNode,
  LoopRunNodeStatus,
  LoopRunStatus,
  NodeType,
  EventLogEntry,
  LoopNode,
  LoopNodeEdge,
} from "../types";
import type { TypedSignalRMessage } from "../types/signalr";
import { loopRunService, loopTemplateService } from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";
import ErrorBanner from "../components/ErrorBanner";

const nodeTypeColors: Record<string, { bg: string; border: string; icon: string }> = {
  [NodeType.Start]: { bg: "#064e3b", border: "#10b981", icon: "\u25B6" },
  [NodeType.Cmd]: { bg: "#1e1b4b", border: "#6366f1", icon: "\u2699" },
  [NodeType.AI]: { bg: "#1c1917", border: "#f59e0b", icon: "\uD83E\uDD16" },
  [NodeType.Human]: { bg: "#1e1b4b", border: "#a855f7", icon: "\uD83D\uDC64" },
  [NodeType.PR]: { bg: "#0c4a6e", border: "#0ea5e9", icon: "\uD83D\uDD01" },
  [NodeType.Cleanup]: { bg: "#4c0519", border: "#ef4444", icon: "\uD83E\uDDD9" },
};

const nodeStatusColors: Record<string, string> = {
  [LoopRunNodeStatus.Pending]: "#6b7280",
  [LoopRunNodeStatus.Running]: "#3b82f6",
  [LoopRunNodeStatus.Succeeded]: "#22c55e",
  [LoopRunNodeStatus.Failed]: "#ef4444",
  [LoopRunNodeStatus.Skipped]: "#4b5563",
  [LoopRunNodeStatus.WaitingHuman]: "#f59e0b",
};

const nodeStatusIcons: Record<string, string> = {
  [LoopRunNodeStatus.Pending]: "\u25CB",
  [LoopRunNodeStatus.Running]: "\u25B8",
  [LoopRunNodeStatus.Succeeded]: "\u2713",
  [LoopRunNodeStatus.Failed]: "\u2717",
  [LoopRunNodeStatus.Skipped]: "\u25A0",
  [LoopRunNodeStatus.WaitingHuman]: "\u26A0",
};

const eventTypeColors: Record<string, string> = {
  NodeStarted: "#3b82f6",
  NodeCompleted: "#22c55e",
  NodeFailed: "#ef4444",
  EdgeTraversed: "#a855f7",
  LoopRunStarted: "#3b82f6",
  LoopRunCompleted: "#22c55e",
  LoopRunFailed: "#ef4444",
  LoopRunCancelled: "#6b7280",
  HumanFeedbackRequested: "#f59e0b",
  HumanFeedbackReceived: "#22c55e",
  RecoveryTriggered: "#f59e0b",
  Error: "#ef4444",
};

function normalizeLoopRunStatus(value: unknown): LoopRunStatus {
  if (typeof value === "string") return value as LoopRunStatus;
  if (typeof value === "number") {
    const map: Record<number, LoopRunStatus> = {
      0: LoopRunStatus.Running,
      1: LoopRunStatus.Completed,
      2: LoopRunStatus.Failed,
      3: LoopRunStatus.Cancelled,
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
    };
    return map[value] ?? LoopRunNodeStatus.Pending;
  }
  return LoopRunNodeStatus.Pending;
}

interface MergedNode {
  templateNode: LoopNode;
  runNode: LoopRunNode | null;
  status: LoopRunNodeStatus;
  label: string;
  type: NodeType;
  nodeId: string;
  startedAt: string | null;
  completedAt: string | null;
  executionCount: number;
  error: string | null;
}

export default function EventLogViewer() {
  const { runId } = useParams<{ runId: string }>();
  const [run, setRun] = useState<LoopRun | null>(null);
  const [templateNodes, setTemplateNodes] = useState<LoopNode[]>([]);
  const [templateEdges, setTemplateEdges] = useState<LoopNodeEdge[]>([]);
  const [events, setEvents] = useState<EventLogEntry[]>([]);
  const [cursor, setCursor] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [expandedEvents, setExpandedEvents] = useState<Set<number>>(new Set());
  const [loadingPayloads, setLoadingPayloads] = useState<Set<number>>(new Set());
  const [loadedPayloads, setLoadedPayloads] = useState<Record<number, string>>({});
  const [errorText, setErrorText] = useState("");
  const [progressLines, setProgressLines] = useState<string[]>([]);
  const [showProgress, setShowProgress] = useState(true);
  const logRef = useRef<HTMLDivElement | null>(null);
  const { on, off, invoke, connectionState } = useSignalR("/hubs/loop-run");

  const loadRun = useCallback(async () => {
    if (!runId) return;
    try {
      const data = await loopRunService.getById(runId);
      const normalized: LoopRun = {
        ...data,
        status: normalizeLoopRunStatus(data.status),
        nodes: data.nodes.map((n) => ({ ...n, status: normalizeNodeStatus(n.status) })),
      };
      setRun(normalized);

      if (data.loopTemplateId && data.templateVersion) {
        try {
          const graph = await loopTemplateService.getVersionGraph(
            data.loopTemplateId,
            data.templateVersion,
          );
          setTemplateNodes(graph.nodes);
          setTemplateEdges(graph.edges);
        } catch {
          console.error("Failed to load template graph");
        }
      }
    } catch (error) {
      console.error("Failed to load run:", error);
      setErrorText(error instanceof Error ? error.message : "Failed to load run");
    } finally {
      setIsLoading(false);
    }
  }, [runId]);

  const fetchEvents = useCallback(
    async (cur: number) => {
      try {
        const page = await loopRunService.getEvents(runId!, cur, 50);
        if (cur === 0) {
          setEvents(page.entries);
        } else {
          setEvents((prev) => [...prev, ...page.entries]);
        }
        setCursor(page.nextCursor);
        setHasMore(page.hasMore);
      } catch (error) {
        console.error("Failed to load events:", error);
      } finally {
        setIsLoading(false);
        setLoadingMore(false);
      }
    },
    [runId],
  );

  useEffect(() => {
    void loadRun();
  }, [loadRun]);

  useEffect(() => {
    void fetchEvents(0);
  }, [fetchEvents]);

  useEffect(() => {
    if (connectionState !== "connected" || !runId) return;
    void invoke?.("SubscribeToRun", runId);
  }, [runId, invoke, connectionState]);

  useEffect(() => {
    const onLoopRunStateChanged = async (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      const { runId: msgRunId, newStatus } = message.payload;
      if (msgRunId !== runId) return;
      setRun((prev) => (prev ? { ...prev, status: normalizeLoopRunStatus(newStatus) } : null));
    };

    const onNodeStateChanged = async (message: TypedSignalRMessage<"NodeStateChanged">) => {
      const { runId: msgRunId, nodeId, newStatus } = message.payload;
      if (msgRunId !== runId) return;
      setRun((prev) => {
        if (!prev) return null;
        const existing = prev.nodes.find((n) => n.nodeId === nodeId);
        if (!existing) {
          void loadRun();
          return prev;
        }
        return {
          ...prev,
          nodes: prev.nodes.map((n) =>
            n.nodeId === nodeId ? { ...n, status: normalizeNodeStatus(newStatus) } : n,
          ),
        };
      });
    };

    const onNodeProgress = async (message: TypedSignalRMessage<"NodeProgress">) => {
      const { runId: msgRunId, line } = message.payload;
      if (msgRunId !== runId) return;
      setProgressLines((prev) => [...prev, line]);
    };

    on("LoopRunStateChanged", onLoopRunStateChanged);
    on("NodeStateChanged", onNodeStateChanged);
    on("NodeProgress", onNodeProgress);

    return () => {
      off("LoopRunStateChanged", onLoopRunStateChanged);
      off("NodeStateChanged", onNodeStateChanged);
      off("NodeProgress", onNodeProgress);
    };
  }, [on, off, runId]);

  useEffect(() => {
    if (logRef.current && progressLines.length > 0) {
      logRef.current.scrollTop = logRef.current.scrollHeight;
    }
  }, [progressLines]);

  const handleLoadMore = () => {
    setLoadingMore(true);
    void fetchEvents(cursor);
  };

  const toggleExpand = (sequence: number) => {
    setExpandedEvents((prev) => {
      const next = new Set(prev);
      if (next.has(sequence)) next.delete(sequence);
      else next.add(sequence);
      return next;
    });
  };

  const handleLoadPayload = async (sequence: number) => {
    setLoadingPayloads((prev) => new Set(prev).add(sequence));
    try {
      const { payload } = await loopRunService.getPayload(runId!, sequence);
      setLoadedPayloads((prev) => ({ ...prev, [sequence]: payload }));
    } catch (error) {
      console.error("Failed to load payload:", error);
    } finally {
      setLoadingPayloads((prev) => {
        const next = new Set(prev);
        next.delete(sequence);
        return next;
      });
    }
  };

  const handleCancel = async () => {
    if (!runId) return;
    try {
      await loopRunService.cancel(runId);
      setRun((prev) => (prev ? { ...prev, status: LoopRunStatus.Cancelled } : null));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to cancel run.");
    }
  };

  const handlePause = async () => {
    if (!runId) return;
    try {
      await loopRunService.pause(runId);
      setRun((prev) => (prev ? { ...prev, isPaused: true } : null));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to pause run.");
    }
  };

  const handleResume = async () => {
    if (!runId) return;
    try {
      await loopRunService.resume(runId);
      setRun((prev) => (prev ? { ...prev, isPaused: false } : null));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to resume run.");
    }
  };

  const mergedNodes: MergedNode[] = templateNodes.map((tn) => {
    const rn = run?.nodes.find((n) => n.nodeId === tn.id) ?? null;
    return {
      templateNode: tn,
      runNode: rn,
      status: rn?.status ?? LoopRunNodeStatus.Pending,
      label: tn.label,
      type: tn.type as NodeType,
      nodeId: tn.id,
      startedAt: rn?.startedAt ?? null,
      completedAt: rn?.completedAt ?? null,
      executionCount: rn?.executionCount ?? 0,
      error: rn?.error ?? null,
    };
  });

  const formatTimestamp = (ts: string) => new Date(ts).toLocaleString();
  const truncate = (text: string, maxLen: number) =>
    !text ? "" : text.length > maxLen ? text.slice(0, maxLen) + "..." : text;

  if (isLoading && !run) {
    return (
      <div className="page-container">
        <p>Loading run details...</p>
      </div>
    );
  }

  if (!run) {
    return (
      <div className="page-container">
        <p>Run not found.</p>
        <Link to="/loop-runs" className="back-link">
          &larr; Back to all runs
        </Link>
      </div>
    );
  }

  const runStatusColors: Record<string, string> = {
    [LoopRunStatus.Running]: "#3b82f6",
    [LoopRunStatus.Completed]: "#22c55e",
    [LoopRunStatus.Failed]: "#ef4444",
    [LoopRunStatus.Cancelled]: "#6b7280",
  };

  return (
    <div className="page-container">
      <div className="run-details-header">
        <div className="run-details-header-left">
          <Link to="/loop-runs" className="back-link">
            &larr; Back to all runs
          </Link>
          <h1 className="page-title">Run {run.id.slice(0, 8)}</h1>
        </div>
        <div className="run-details-header-right">
          <span
            className={`run-status-badge ${run.status.toLowerCase()}`}
            style={{ borderColor: runStatusColors[run.status] ?? "#6b7280" }}
          >
            {run.status}
            {run.isPaused && " (Paused)"}
          </span>
          {run.status === LoopRunStatus.Running && (
            <div className="run-controls">
              {run.isPaused ? (
                <button className="btn btn-primary btn-small" onClick={handleResume}>
                  Resume
                </button>
              ) : (
                <button className="btn btn-secondary btn-small" onClick={handlePause}>
                  Pause
                </button>
              )}
              <button className="btn btn-danger btn-small" onClick={handleCancel}>
                Cancel
              </button>
            </div>
          )}
        </div>
      </div>
      <div className="run-details-meta">
        <span>Started: {formatTimestamp(run.startedAt)}</span>
        <span>Executions: {run.nodeExecutionCount}</span>
        {run.completedAt && <span>Completed: {formatTimestamp(run.completedAt)}</span>}
      </div>
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />

      <div className="run-details-layout">
        <div className="node-flow-panel">
          <div className="node-flow-panel-title">Node Flow</div>
          <div className="node-flow-container">
            {mergedNodes.map((node, index) => {
              const style = nodeTypeColors[node.type] || nodeTypeColors[NodeType.Cmd];
              const isActive =
                run.currentNodeId === node.nodeId && run.status === LoopRunStatus.Running;
              const statusColor = nodeStatusColors[node.status] ?? "#6b7280";
              const hasEdges = templateEdges.some(
                (e) => e.sourceNodeId === node.nodeId || e.targetNodeId === node.nodeId,
              );

              return (
                <div key={node.nodeId} className="node-flow-row">
                  {hasEdges && index > 0 && (
                    <div className="node-flow-connector">
                      <div className="node-flow-line" />
                      <div className="node-flow-arrow" />
                    </div>
                  )}
                  <div
                    className={`node-flow-node ${isActive ? "node-active" : ""} ${
                      node.status === LoopRunNodeStatus.Running ? "node-running" : ""
                    }`}
                    style={{
                      background: style.bg,
                      borderColor: isActive ? statusColor : style.border,
                    }}
                  >
                    <div className="node-flow-node-status-indicator" style={{ color: statusColor }}>
                      {nodeStatusIcons[node.status] || "\u25CB"}
                    </div>
                    <div className="node-flow-node-content">
                      <div className="node-flow-node-type">
                        <span className="node-flow-node-icon">{style.icon}</span>
                        <span>{node.type}</span>
                      </div>
                      <div className="node-flow-node-label">{node.label}</div>
                      {node.startedAt && (
                        <div className="node-flow-node-time">
                          {node.completedAt
                            ? `${formatTimestamp(node.startedAt)} - ${formatTimestamp(node.completedAt)}`
                            : `Since ${formatTimestamp(node.startedAt)}`}
                        </div>
                      )}
                      {node.executionCount > 1 && (
                        <div className="node-flow-node-retries">{node.executionCount} attempts</div>
                      )}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>

        <div className="event-log-panel">
          <div className="event-log-panel-header">
            <span className="event-log-panel-title">Events</span>
            <span className="event-count">{events.length} events</span>
          </div>
          <div className="event-list">
            {events.map((event) => {
              const isExpanded = expandedEvents.has(event.sequence);
              const isPayloadLoading = loadingPayloads.has(event.sequence);
              const loadedPayload = loadedPayloads[event.sequence];

              return (
                <div
                  key={event.sequence}
                  className={`event-item ${isExpanded ? "expanded" : ""}`}
                  onClick={() => toggleExpand(event.sequence)}
                >
                  <div className="event-summary">
                    <span className="event-sequence">#{event.sequence}</span>
                    <span
                      className="event-type-badge"
                      style={{ backgroundColor: eventTypeColors[event.eventType] || "#6b7280" }}
                    >
                      {event.eventType}
                    </span>
                    {event.nodeId && (
                      <span className="event-node-id">
                        {mergedNodes.find((n) => n.nodeId === event.nodeId)?.label ||
                          event.nodeId.slice(0, 8)}
                      </span>
                    )}
                    <span className="event-timestamp">{formatTimestamp(event.timestamp)}</span>
                    <span className="event-message">{truncate(event.payload, 120)}</span>
                  </div>
                  {isExpanded && (
                    <div className="event-detail">
                      {event.payload && !event.hasPayload && (
                        <pre className="event-payload">{event.payload}</pre>
                      )}
                      {event.hasPayload && (
                        <div className="event-payload-actions">
                          {loadedPayload ? (
                            <pre className="event-payload">{loadedPayload}</pre>
                          ) : (
                            <button
                              className="btn btn-small btn-secondary load-payload-btn"
                              onClick={(e) => {
                                e.stopPropagation();
                                void handleLoadPayload(event.sequence);
                              }}
                              disabled={isPayloadLoading}
                            >
                              {isPayloadLoading ? "Loading..." : "Load Payload"}
                            </button>
                          )}
                        </div>
                      )}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
          {hasMore && (
            <div className="load-more-container">
              <button className="btn btn-secondary" onClick={handleLoadMore} disabled={loadingMore}>
                {loadingMore ? "Loading..." : "Load More"}
              </button>
            </div>
          )}

          {progressLines.length > 0 && (
            <div className="live-output-section">
              <button
                className="btn btn-secondary btn-small btn-toggle-log"
                onClick={() => setShowProgress((p) => !p)}
              >
                {showProgress ? "▼ Hide" : "▶ Show"} Live Output ({progressLines.length} lines)
              </button>
              {showProgress && (
                <div className="live-output-panel" ref={logRef}>
                  {progressLines.map((line, i) => (
                    <div key={i} className="live-output-line">
                      {line}
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      <style>{`
        .run-details-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 0.5rem;
          flex-wrap: wrap;
          gap: 0.5rem;
        }

        .run-details-header-left {
          display: flex;
          align-items: center;
          gap: 1rem;
        }

        .run-details-header-right {
          display: flex;
          align-items: center;
          gap: 0.75rem;
        }

        .back-link {
          color: #60a5fa;
          text-decoration: none;
          font-size: 0.85rem;
        }

        .back-link:hover {
          color: #93c5fd;
        }

        .run-status-badge {
          font-size: 0.75rem;
          padding: 0.25rem 0.75rem;
          border-radius: 0.35rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          border: 2px solid;
        }

        .run-status-badge.running {
          background-color: #1e3a5f;
          color: #60a5fa;
        }

        .run-status-badge.completed {
          background-color: #065f46;
          color: #6ee7b7;
        }

        .run-status-badge.failed {
          background-color: #5f1e1e;
          color: #f87171;
        }

        .run-status-badge.cancelled {
          background-color: #3a3a5c;
          color: #a0a0b0;
        }

        .run-controls {
          display: flex;
          gap: 0.5rem;
        }

        .run-details-meta {
          display: flex;
          gap: 1.5rem;
          font-size: 0.75rem;
          color: #707090;
          margin-bottom: 1rem;
        }

        .run-details-layout {
          display: grid;
          grid-template-columns: 320px 1fr;
          gap: 1rem;
        }

        .node-flow-panel {
          background-color: #1a1a2e;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          padding: 0.75rem;
          overflow-y: auto;
        }

        .node-flow-panel-title {
          font-size: 0.8rem;
          font-weight: 600;
          color: #a0a0c0;
          margin-bottom: 0.75rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .node-flow-container {
          display: flex;
          flex-direction: column;
        }

        .node-flow-row {
          display: flex;
          flex-direction: column;
          align-items: stretch;
        }

        .node-flow-connector {
          display: flex;
          justify-content: center;
          padding: 0.25rem 0;
        }

        .node-flow-line {
          width: 2px;
          height: 12px;
          background-color: #3d3d5c;
        }

        .node-flow-arrow {
          width: 0;
          height: 0;
          border-left: 5px solid transparent;
          border-right: 5px solid transparent;
          border-top: 6px solid #3d3d5c;
        }

        .node-flow-node {
          border: 2px solid;
          border-radius: 8px;
          padding: 0.6rem 0.75rem;
          display: flex;
          gap: 0.6rem;
          transition: box-shadow 0.3s, border-color 0.3s;
        }

        .node-flow-node.node-active {
          box-shadow: 0 0 12px rgba(59, 130, 246, 0.4);
        }

        .node-flow-node.node-running {
          animation: nodePulse 1.5s ease-in-out infinite;
        }

        @keyframes nodePulse {
          0%, 100% { box-shadow: 0 0 6px rgba(59, 130, 246, 0.3); }
          50% { box-shadow: 0 0 18px rgba(59, 130, 246, 0.7); }
        }

        .node-flow-node-status-indicator {
          font-size: 1.1rem;
          flex-shrink: 0;
          width: 1.5rem;
          text-align: center;
        }

        .node-flow-node-content {
          flex: 1;
          min-width: 0;
        }

        .node-flow-node-type {
          font-size: 0.7rem;
          font-weight: 600;
          display: flex;
          align-items: center;
          gap: 0.3rem;
          margin-bottom: 0.15rem;
        }

        .node-flow-node-icon {
          font-size: 0.85rem;
        }

        .node-flow-node-label {
          font-size: 0.85rem;
          font-weight: 500;
          color: #e0e0e0;
          margin-bottom: 0.15rem;
        }

        .node-flow-node-time {
          font-size: 0.65rem;
          color: #707090;
        }

        .node-flow-node-retries {
          font-size: 0.65rem;
          color: #f59e0b;
        }

        .event-log-panel {
          background-color: #1a1a2e;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          padding: 0.75rem;
          display: flex;
          flex-direction: column;
        }

        .event-log-panel-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 0.75rem;
          flex-shrink: 0;
        }

        .event-log-panel-title {
          font-size: 0.8rem;
          font-weight: 600;
          color: #a0a0c0;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .event-list {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
          overflow-y: auto;
          flex: 1;
          min-height: 0;
        }

        .event-item {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          cursor: pointer;
          overflow: hidden;
          transition: border-color 0.15s;
        }

        .event-item:hover {
          border-color: #4a4a6a;
        }

        .event-item.expanded {
          border-color: #5a5a8a;
        }

        .event-summary {
          display: flex;
          align-items: center;
          gap: 0.75rem;
          padding: 0.75rem 1rem;
          font-size: 0.8rem;
        }

        .event-sequence {
          color: #707090;
          font-size: 0.75rem;
          min-width: 2.5rem;
        }

        .event-type-badge {
          font-size: 0.65rem;
          padding: 0.125rem 0.5rem;
          border-radius: 0.25rem;
          color: #fff;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          min-width: 7rem;
          text-align: center;
        }

        .event-node-id {
          font-size: 0.65rem;
          color: #a0a0c0;
          background-color: #2a2a40;
          padding: 0.1rem 0.4rem;
          border-radius: 0.2rem;
        }

        .event-timestamp {
          color: #707090;
          font-size: 0.7rem;
          min-width: 9rem;
        }

        .event-message {
          color: #c0c0d0;
          flex: 1;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }

        .event-detail {
          padding: 0 1rem 0.75rem 1rem;
          border-top: 1px solid #2d2d44;
        }

        .event-payload {
          background-color: #16162a;
          border-radius: 0.25rem;
          padding: 0.75rem;
          margin-top: 0.5rem;
          font-size: 0.75rem;
          color: #a0a0c0;
          overflow-x: auto;
          white-space: pre-wrap;
          word-break: break-word;
          max-height: 300px;
          overflow-y: auto;
        }

        .event-payload-actions {
          margin-top: 0.5rem;
        }

        .load-more-container {
          display: flex;
          justify-content: center;
          padding: 1rem 0;
          flex-shrink: 0;
        }

        .live-output-section {
          margin-top: 0.75rem;
          flex-shrink: 0;
        }

        .btn-toggle-log {
          background-color: #1e3a5f;
          color: #60a5fa;
          padding: 0.25rem 0.5rem;
          border: none;
          border-radius: 0.25rem;
          cursor: pointer;
          font-size: 0.7rem;
          width: 100%;
        }

        .btn-toggle-log:hover {
          background-color: #2a4a7f;
        }

        .live-output-panel {
          margin-top: 0.5rem;
          background-color: #0d0d1a;
          border-radius: 0.25rem;
          padding: 0.5rem;
          max-height: 200px;
          overflow-y: auto;
          font-family: monospace;
          font-size: 0.7rem;
          line-height: 1.4;
          border: 1px solid #2d2d44;
        }

        .live-output-line {
          color: #a0a0c0;
          white-space: pre-wrap;
          word-break: break-all;
        }

        .btn-secondary {
          background-color: #2d2d44;
          color: #c0c0d0;
          padding: 0.375rem 1rem;
          border: none;
          border-radius: 0.25rem;
          cursor: pointer;
          font-size: 0.8rem;
        }

        .btn-secondary:hover {
          background-color: #3d3d54;
        }

        .btn-secondary:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        .load-payload-btn {
          background-color: #1e3a5f;
          color: #60a5fa;
        }

        .load-payload-btn:hover {
          background-color: #2a4a7f;
        }

        .btn-danger {
          background-color: #5f1e1e;
          color: #f87171;
          padding: 0.25rem 0.5rem;
          border: none;
          border-radius: 0.25rem;
          cursor: pointer;
          font-size: 0.7rem;
        }

        .btn-primary {
          background-color: #1e3a5f;
          color: #60a5fa;
          padding: 0.25rem 0.5rem;
          border: none;
          border-radius: 0.25rem;
          cursor: pointer;
          font-size: 0.7rem;
        }
      `}</style>
    </div>
  );
}
