import { useState, useEffect, useCallback, useRef } from "react";
import { useParams, Link } from "react-router-dom";
import {
  LoopRun,
  LoopRunAvailableSession,
  LoopRunSessionPreview,
  LoopRunNode,
  LoopRunNodeStatus,
  LoopRunStatus,
  NodeType,
  EventLogEntry,
  LoopNode,
  EdgeType,
} from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import { loopRunService, loopTemplateService } from "../../services/auth";
import { useSignalR } from "../../hooks/useSignalR";
import { useNodeExpansion } from "../../hooks/useNodeExpansion";
import ErrorBanner from "../../components/ErrorBanner";
import {
  NodeItem,
  NodeInputSection,
  NodeEventsSection,
  NodeOutputSection,
  LiveStream,
} from "../../components/NodeTimeline";
import EdgeArrow from "../../components/NodeTimeline/EdgeArrow";
import "../../components/NodeTimeline/NodeTimeline.css";

interface EffectiveInput {
  nodeType?: string;
  command?: string;
  prompt?: string;
  resolvedPrompt?: string;
  context?: Record<string, unknown>;
  message?: string;
}

function normalizeLoopRunStatus(value: unknown): LoopRunStatus {
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

function formatSessionTimestamp(ts: string | null): string {
  if (!ts) return "Unknown";
  return new Date(ts).toLocaleString();
}

function formatSessionJson(sessionJson: string): string {
  try {
    return JSON.stringify(JSON.parse(sessionJson), null, 2);
  } catch {
    return sessionJson;
  }
}

function buildSessionSummary(preview: LoopRunSessionPreview): string[] {
  const lines = [
    `Adapter: ${preview.adapterName}`,
    `Session: ${preview.sessionId}`,
    `Updated: ${formatSessionTimestamp(preview.updatedAt ?? preview.createdAt)}`,
  ];

  try {
    const parsed = JSON.parse(preview.sessionJson) as Record<string, unknown>;
    if (Array.isArray(parsed.messages)) {
      lines.push(`Messages: ${parsed.messages.length}`);
    }
    if (typeof parsed.id === "string") {
      lines.push(`Snapshot Id: ${parsed.id}`);
    }
    if (Array.isArray(parsed.tools)) {
      lines.push(`Tools: ${parsed.tools.length}`);
    }
    lines.push(`Root: ${Array.isArray(parsed) ? "array" : typeof parsed}`);
  } catch {
    lines.push("Root: raw text");
  }

  return lines;
}

export default function EventLogViewer() {
  const { runId } = useParams<{ runId: string }>();
  const [run, setRun] = useState<LoopRun | null>(null);
  const [runNodes, setRunNodes] = useState<LoopRunNode[]>([]);
  const [templateNodes, setTemplateNodes] = useState<LoopNode[]>([]);
  const [events, setEvents] = useState<EventLogEntry[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [errorText, setErrorText] = useState("");
  const [progressText, setProgressText] = useState<string>("");
  const { expandedNodeIds, handleToggleNode, runningNodeId } = useNodeExpansion(runNodes);
  const [selectedSessionPreview, setSelectedSessionPreview] =
    useState<LoopRunSessionPreview | null>(null);
  const [isSessionPreviewLoading, setIsSessionPreviewLoading] = useState(false);
  const timelineContainerRef = useRef<HTMLDivElement | null>(null);
  const isAtBottomRef = useRef(true);
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
      setRunNodes(normalized.nodes);

      if (data.loopTemplateId && data.templateVersion) {
        try {
          const graph = await loopTemplateService.getVersionGraph(
            data.loopTemplateId,
            data.templateVersion,
          );
          setTemplateNodes(graph.nodes);
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

  const loadEvents = useCallback(async () => {
    if (!runId) return;
    try {
      const page = await loopRunService.getEvents(runId, 0, 200);
      setEvents(page.entries);
    } catch (error) {
      console.error("Failed to load events:", error);
    }
  }, [runId]);

  const handleTimelineScroll = useCallback(() => {
    const el = timelineContainerRef.current;
    if (!el) return;
    isAtBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 50;
  }, []);

  const scrollToBottom = useCallback(() => {
    const el = timelineContainerRef.current;
    if (!el) return;
    if (isAtBottomRef.current || runningNodeId) {
      el.scrollTop = el.scrollHeight;
    }
  }, [runningNodeId]);

  useEffect(() => {
    scrollToBottom();
  }, [runNodes.length, scrollToBottom]);

  useEffect(() => {
    void loadRun();
  }, [loadRun]);

  useEffect(() => {
    void loadEvents();
  }, [loadEvents]);

  useEffect(() => {
    if (connectionState !== "connected" || !runId) return;
    void invoke?.("SubscribeToRun", runId);
  }, [runId, invoke, connectionState]);

  useEffect(() => {
    const onLoopRunStateChanged = async (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      const { runId: msgRunId, newStatus } = message.payload;
      if (msgRunId !== runId) return;
      const status = normalizeLoopRunStatus(newStatus);
      setRun((prev) => (prev ? { ...prev, status } : null));
      if (status === LoopRunStatus.Completed) {
        void loadRun();
      }
    };

    // NodeStateChanged carries only the LoopNode (template) id + new status.
    // We can't reliably patch the timeline locally because: (a) the same
    // template node can produce multiple LoopRunNode rows when a loop
    // re-visits it, so matching by nodeId rewrites the wrong row, and
    // (b) the message has no Output / CompletedAt / Error / RetryCount.
    // Refetch from REST instead — it's the source of truth and the
    // payloads are small.
    const onNodeStateChanged = async (message: TypedSignalRMessage<"NodeStateChanged">) => {
      const { runId: msgRunId } = message.payload;
      if (msgRunId !== runId) return;
      void loadRun();
    };

    const onNodeProgress = async (message: TypedSignalRMessage<"NodeProgress">) => {
      const { runId: msgRunId, line } = message.payload;
      if (msgRunId !== runId) return;
      setProgressText((prev) => prev + line);
    };

    on("LoopRunStateChanged", onLoopRunStateChanged);
    on("NodeStateChanged", onNodeStateChanged);
    on("NodeProgress", onNodeProgress);

    return () => {
      off("LoopRunStateChanged", onLoopRunStateChanged);
      off("NodeStateChanged", onNodeStateChanged);
      off("NodeProgress", onNodeProgress);
    };
  }, [on, off, runId, loadRun]);

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

  const handleRetryFromNode = async (runNodeId: string) => {
    if (!runId) return;
    try {
      await loopRunService.retryFromNode(runId, runNodeId);
      await loadRun();
      await loadEvents();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to retry from node.");
    }
  };

  const handleToggleRetain = async () => {
    if (!runId || !run) return;
    const next = !run.retain;
    try {
      await loopRunService.setRetain(runId, next);
      setRun((prev) => (prev ? { ...prev, retain: next } : null));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to update retain.");
    }
  };

  const handlePreviewSession = useCallback(
    async (session: LoopRunAvailableSession) => {
      if (!runId) return;
      try {
        setIsSessionPreviewLoading(true);
        const preview = await loopRunService.getSessionPreview(
          runId,
          session.adapterName,
          session.sessionId,
        );
        setSelectedSessionPreview(preview);
      } catch (error) {
        setErrorText(error instanceof Error ? error.message : "Failed to load session preview.");
      } finally {
        setIsSessionPreviewLoading(false);
      }
    },
    [runId],
  );

  const getTemplateNode = (nodeId: string): LoopNode | undefined => {
    return templateNodes.find((n) => n.id === nodeId);
  };

  const getTemplateNodeType = (nodeId: string): NodeType => {
    return getTemplateNode(nodeId)?.type ?? NodeType.Cmd;
  };

  const getEventsForRunNode = (runNodeId: string): EventLogEntry[] => {
    return events.filter((e) => e.runNodeId === runNodeId);
  };

  const getEffectiveInputForRunNode = (runNodeId: string): EffectiveInput | undefined => {
    const runNode = runNodes.find((n) => n.id === runNodeId);
    if (!runNode?.effectiveInput) return undefined;
    try {
      return JSON.parse(runNode.effectiveInput) as EffectiveInput;
    } catch {
      return undefined;
    }
  };

  const formatTimestamp = (ts: string) => new Date(ts).toLocaleString();

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

  const availableSessions = run.availableSessions ?? [];

  const runStatusColors: Record<string, string> = {
    [LoopRunStatus.Running]: "#3b82f6",
    [LoopRunStatus.Completed]: "#22c55e",
    [LoopRunStatus.Failed]: "#ef4444",
    [LoopRunStatus.Cancelled]: "#6b7280",
  };

  return (
    <div className="page-container run-details-page">
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
          <button
            className={`btn btn-small ${run.retain ? "btn-primary" : "btn-secondary"}`}
            onClick={handleToggleRetain}
            title={
              run.retain
                ? "Pinned: this run is kept and never auto-deleted. Click to unpin."
                : "Pin this run so its worktree, branch, and history are never auto-deleted."
            }
          >
            {run.retain ? "📌 Retained" : "Retain"}
          </button>
        </div>
      </div>
      <div className="run-details-meta">
        <span>Started: {formatTimestamp(run.startedAt)}</span>
        <span>Executions: {run.nodeExecutionCount}</span>
        {run.completedAt && <span>Completed: {formatTimestamp(run.completedAt)}</span>}
      </div>
      {availableSessions.length > 0 && (
        <div
          style={{
            border: "1px solid #334155",
            borderRadius: "12px",
            padding: "0.9rem 1rem",
            marginBottom: "1rem",
            background: "#0f172a",
          }}
        >
          <div style={{ fontWeight: 600, marginBottom: "0.5rem" }}>Available AI Sessions</div>
          <div style={{ color: "#94a3b8", marginBottom: "0.75rem", fontSize: "0.95rem" }}>
            These are the saved adapter sessions for this run. Use these generated ids when routing
            an AI node to a specific session.
          </div>
          <div style={{ display: "grid", gap: "0.5rem" }}>
            {availableSessions.map((session: LoopRunAvailableSession) => (
              <div
                key={`${session.adapterName}:${session.sessionId}`}
                style={{
                  display: "flex",
                  justifyContent: "space-between",
                  alignItems: "center",
                  gap: "1rem",
                  padding: "0.65rem 0.75rem",
                  borderRadius: "10px",
                  background: "#111827",
                  border: "1px solid #1f2937",
                  flexWrap: "wrap",
                }}
              >
                <div style={{ display: "grid", gap: "0.2rem" }}>
                  <div style={{ fontWeight: 600 }}>{session.adapterName}</div>
                  <div style={{ fontFamily: "monospace", fontSize: "0.95rem" }}>
                    {session.sessionId}
                  </div>
                  {session.placeholders.length > 0 && (
                    <div style={{ color: "#cbd5e1", fontSize: "0.85rem" }}>
                      Placeholders: {session.placeholders.join(", ")}
                    </div>
                  )}
                </div>
                <div style={{ display: "grid", gap: "0.2rem", justifyItems: "end" }}>
                  {session.isCurrent && (
                    <span style={{ color: "#22c55e", fontSize: "0.9rem", fontWeight: 600 }}>
                      Current
                    </span>
                  )}
                  <span style={{ color: "#94a3b8", fontSize: "0.85rem" }}>
                    Updated: {formatSessionTimestamp(session.updatedAt ?? session.createdAt)}
                  </span>
                  <button
                    className="btn btn-secondary btn-small"
                    onClick={() => void handlePreviewSession(session)}
                    disabled={isSessionPreviewLoading}
                  >
                    {isSessionPreviewLoading &&
                    selectedSessionPreview?.sessionId === session.sessionId
                      ? "Loading..."
                      : "Preview"}
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
      {selectedSessionPreview && (
        <div
          onMouseDown={() => setSelectedSessionPreview(null)}
          role="dialog"
          aria-modal="true"
          aria-label="Session preview"
          style={{
            position: "fixed",
            inset: 0,
            background: "rgba(2, 6, 23, 0.78)",
            display: "grid",
            placeItems: "center",
            padding: "1rem",
            zIndex: 40,
          }}
        >
          <div
            onMouseDown={(event) => event.stopPropagation()}
            style={{
              width: "min(920px, 100%)",
              maxHeight: "85vh",
              overflow: "hidden",
              borderRadius: "18px",
              border: "1px solid #1e293b",
              background: "linear-gradient(180deg, rgba(15,23,42,0.98), rgba(2,6,23,0.98))",
              boxShadow: "0 30px 80px rgba(0, 0, 0, 0.45)",
            }}
          >
            <div
              style={{
                display: "flex",
                justifyContent: "space-between",
                alignItems: "flex-start",
                gap: "1rem",
                padding: "1.1rem 1.2rem",
                borderBottom: "1px solid #1e293b",
              }}
            >
              <div style={{ display: "grid", gap: "0.35rem" }}>
                <div style={{ fontSize: "1.1rem", fontWeight: 700 }}>Session Preview</div>
                <div style={{ color: "#cbd5e1", fontFamily: "monospace", fontSize: "0.95rem" }}>
                  {selectedSessionPreview.sessionId}
                </div>
              </div>
              <button
                className="btn btn-secondary btn-small"
                onClick={() => setSelectedSessionPreview(null)}
              >
                Close
              </button>
            </div>
            <div
              style={{
                display: "grid",
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                gap: "0.75rem",
                padding: "1rem 1.2rem 0",
              }}
            >
              {buildSessionSummary(selectedSessionPreview).map((line) => (
                <div
                  key={line}
                  style={{
                    padding: "0.8rem 0.9rem",
                    borderRadius: "12px",
                    background: "rgba(15, 23, 42, 0.88)",
                    border: "1px solid #1e293b",
                    color: "#e2e8f0",
                    fontSize: "0.92rem",
                  }}
                >
                  {line}
                </div>
              ))}
            </div>
            <div style={{ padding: "1rem 1.2rem 1.2rem", overflow: "auto", maxHeight: "55vh" }}>
              <pre
                style={{
                  margin: 0,
                  padding: "1rem 1.1rem",
                  borderRadius: "14px",
                  background: "#020617",
                  border: "1px solid #1e293b",
                  color: "#dbeafe",
                  fontSize: "0.9rem",
                  lineHeight: 1.55,
                  whiteSpace: "pre-wrap",
                  wordBreak: "break-word",
                }}
              >
                {formatSessionJson(selectedSessionPreview.sessionJson)}
              </pre>
            </div>
          </div>
        </div>
      )}
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />

      <div
        ref={timelineContainerRef}
        className="run-details-timeline-container"
        onScroll={handleTimelineScroll}
      >
        {runNodes.map((rn, index) => {
          const isRunning = rn.status === LoopRunNodeStatus.Running;
          const templateNodeType = getTemplateNodeType(rn.nodeId);
          const nodeEvents = getEventsForRunNode(rn.id);
          const effectiveInput = getEffectiveInputForRunNode(rn.id);

          let edgeType: EdgeType | undefined;
          let edgeVariant: "retry" | undefined;
          if (index > 0) {
            const prevNode = runNodes[index - 1];
            // A RetryFromNode event whose runNodeId matches the previous
            // run node means the next entry was produced by a manual retry.
            const isRetryBoundary = events.some(
              (e) => e.eventType === "RetryFromNode" && e.runNodeId === prevNode.id,
            );
            if (isRetryBoundary) {
              edgeType = EdgeType.OnSuccess;
              edgeVariant = "retry";
            } else {
              const prevType = getTemplateNodeType(prevNode.nodeId);
              const prevHuman = prevType === NodeType.Human;
              if (prevNode.status === LoopRunNodeStatus.Succeeded) {
                edgeType = prevHuman ? EdgeType.Custom : EdgeType.OnSuccess;
              } else {
                edgeType = EdgeType.OnFailure;
              }
            }
          }

          const templateNode = getTemplateNode(rn.nodeId);

          return (
            <div key={rn.id} className="node-execution-block">
              {edgeType !== undefined && <EdgeArrow edgeType={edgeType} variant={edgeVariant} />}
              <NodeItem
                runNode={rn}
                templateNodeType={templateNodeType}
                templateNodeLabel={templateNode?.label}
                isRunning={isRunning}
                isExpanded={expandedNodeIds.includes(rn.id)}
                onToggle={() => handleToggleNode(rn.id)}
                onRetry={handleRetryFromNode}
                retryDisabled={run.status === LoopRunStatus.Running && !run.isPaused}
              >
                {isRunning && <LiveStream text={progressText} />}
                {!isRunning && (
                  <>
                    <NodeInputSection nodeType={templateNodeType} effectiveInput={effectiveInput} />
                    <NodeEventsSection events={nodeEvents} />
                    <NodeOutputSection
                      output={rn.output}
                      error={rn.error}
                      nodeType={templateNodeType}
                    />
                  </>
                )}
              </NodeItem>
            </div>
          );
        })}

        {runNodes.length === 0 && !isLoading && (
          <div className="node-timeline-empty">No nodes executed yet.</div>
        )}
      </div>
    </div>
  );
}
