import { useState, useEffect, useCallback, useRef } from "react";
import { useParams, Link } from "react-router-dom";
import {
  LoopRun,
  LoopRunAvailableSession,
  LoopRunNode,
  LoopRunNodeStatus,
  LoopRunStatus,
  NodeType,
  EventLogEntry,
  LoopNode,
  EdgeType,
} from "../types";
import type { TypedSignalRMessage } from "../types/signalr";
import { loopRunService, loopTemplateService } from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";
import ErrorBanner from "../components/ErrorBanner";
import {
  NodeItem,
  NodeInputSection,
  NodeEventsSection,
  NodeOutputSection,
  LiveStream,
} from "../components/NodeTimeline";
import EdgeArrow from "../components/NodeTimeline/EdgeArrow";
import "../components/NodeTimeline/NodeTimeline.css";

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
      6: LoopRunNodeStatus.Responded,
    };
    return map[value] ?? LoopRunNodeStatus.Pending;
  }
  return LoopRunNodeStatus.Pending;
}

function formatSessionTimestamp(ts: string | null): string {
  if (!ts) return "Unknown";
  return new Date(ts).toLocaleString();
}

export default function EventLogViewer() {
  const { runId } = useParams<{ runId: string }>();
  const [run, setRun] = useState<LoopRun | null>(null);
  const [runNodes, setRunNodes] = useState<LoopRunNode[]>([]);
  const [templateNodes, setTemplateNodes] = useState<LoopNode[]>([]);
  const [events, setEvents] = useState<EventLogEntry[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [errorText, setErrorText] = useState("");
  const [progressLines, setProgressLines] = useState<string[]>([]);
  const [effectiveInputs, setEffectiveInputs] = useState<Record<string, EffectiveInput>>({});
  const [expandedNodeId, setExpandedNodeId] = useState<string | null>(null);
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

      const inputs: Record<string, EffectiveInput> = {};
      for (const entry of page.entries) {
        if (entry.eventType === "NodeStarted" && entry.runNodeId) {
          const jsonMatch = entry.payload.match(/\{[\s\S]*\}$/);
          if (jsonMatch) {
            try {
              inputs[entry.runNodeId] = JSON.parse(jsonMatch[0]) as EffectiveInput;
            } catch {
              // ignore parse errors
            }
          }
        }
        if (entry.eventType === "NodeCompleted" && entry.runNodeId) {
          const jsonMatch = entry.payload.match(/\{[\s\S]*\}$/);
          if (jsonMatch) {
            try {
              const data = JSON.parse(jsonMatch[0]);
              if (data.resolvedPrompt) {
                inputs[entry.runNodeId] = {
                  ...inputs[entry.runNodeId],
                  resolvedPrompt: data.resolvedPrompt,
                };
              }
            } catch {
              // ignore parse errors
            }
          }
        }
      }
      setEffectiveInputs(inputs);
    } catch (error) {
      console.error("Failed to load events:", error);
    }
  }, [runId]);

  const handleToggleNode = useCallback((nodeId: string) => {
    setExpandedNodeId((prev) => (prev === nodeId ? null : nodeId));
  }, []);

  const handleTimelineScroll = useCallback(() => {
    const el = timelineContainerRef.current;
    if (!el) return;
    isAtBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 50;
  }, []);

  const runningNode = runNodes.find((n) => n.status === LoopRunNodeStatus.Running);
  const scrollToBottom = useCallback(() => {
    const el = timelineContainerRef.current;
    if (!el) return;
    if (isAtBottomRef.current || runningNode) {
      el.scrollTop = el.scrollHeight;
    }
  }, [runningNode]);

  useEffect(() => {
    scrollToBottom();
  }, [runNodes.length, scrollToBottom]);

  useEffect(() => {
    const runningNode = runNodes.find((n) => n.status === LoopRunNodeStatus.Running);
    if (runningNode && expandedNodeId !== runningNode.id) {
      setExpandedNodeId(runningNode.id);
    }
  }, [runNodes, expandedNodeId]);

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
      setProgressLines((prev) => [...prev, line]);
    };

    const onEventLogged = async (message: TypedSignalRMessage<"EventLogged">) => {
      const { runId: msgRunId, eventType, runNodeId, message: eventMessage } = message.payload;
      if (msgRunId !== runId) return;
      if (eventType === "NodeStarted" && runNodeId) {
        const jsonMatch = eventMessage.match(/\{[\s\S]*\}$/);
        if (jsonMatch) {
          try {
            const input = JSON.parse(jsonMatch[0]) as EffectiveInput;
            setEffectiveInputs((prev) => ({ ...prev, [runNodeId]: input }));
          } catch {
            // ignore parse errors
          }
        }
      }
      if (eventType === "NodeCompleted" && runNodeId) {
        const jsonMatch = eventMessage.match(/\{[\s\S]*\}$/);
        if (jsonMatch) {
          try {
            const data = JSON.parse(jsonMatch[0]);
            if (data.resolvedPrompt) {
              setEffectiveInputs((prev) => ({
                ...prev,
                [runNodeId]: { ...prev[runNodeId], resolvedPrompt: data.resolvedPrompt },
              }));
            }
          } catch {
            // ignore parse errors
          }
        }
        // NodeCompleted is when Output / CompletedAt / Error get persisted.
        // Refresh so the timeline reflects them without waiting for the
        // user to refresh the page manually.
        void loadRun();
      }
    };

    on("LoopRunStateChanged", onLoopRunStateChanged);
    on("NodeStateChanged", onNodeStateChanged);
    on("NodeProgress", onNodeProgress);
    on("EventLogged", onEventLogged);

    return () => {
      off("LoopRunStateChanged", onLoopRunStateChanged);
      off("NodeStateChanged", onNodeStateChanged);
      off("NodeProgress", onNodeProgress);
      off("EventLogged", onEventLogged);
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
    return effectiveInputs[runNodeId];
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
                </div>
              </div>
            ))}
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
            } else if (prevNode.status === LoopRunNodeStatus.Succeeded) {
              edgeType = EdgeType.OnSuccess;
            } else if (prevNode.status === LoopRunNodeStatus.Responded) {
              edgeType = EdgeType.OnRespond;
            } else {
              edgeType = EdgeType.OnFailure;
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
                isExpanded={expandedNodeId === rn.id}
                onToggle={() => handleToggleNode(rn.id)}
                onRetry={handleRetryFromNode}
                retryDisabled={run.status === LoopRunStatus.Running && !run.isPaused}
              >
                {isRunning && <LiveStream lines={progressLines} />}
                {!isRunning && (
                  <>
                    <NodeInputSection nodeType={templateNodeType} effectiveInput={effectiveInput} />
                    <NodeEventsSection events={nodeEvents} />
                    <NodeOutputSection output={rn.output} error={rn.error} />
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
