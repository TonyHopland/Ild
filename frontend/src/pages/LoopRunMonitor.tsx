import { useState, useEffect, useRef } from "react";
import { Link } from "react-router-dom";
import { LoopRun, LoopRunNodeStatus, LoopRunStatus } from "../types";
import type { TypedSignalRMessage } from "../types/signalr";
import { loopRunService } from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";
import ErrorBanner from "../components/ErrorBanner";

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
}

export default function LoopRunMonitor() {
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [errorText, setErrorText] = useState("");
  const [progressLines, setProgressLines] = useState<Record<string, string[]>>({});
  const [expandedRuns, setExpandedRuns] = useState<Set<string>>(new Set());
  const logRefs = useRef<Record<string, HTMLDivElement | null>>({});
  const { on, off, invoke, connectionState } = useSignalR("/hubs/loop-run");

  useEffect(() => {
    void loadRuns();
  }, []);

  useEffect(() => {
    if (connectionState !== "connected") return;
    runs
      .filter((r) => r.status === LoopRunStatus.Running)
      .forEach((r) => invoke?.("SubscribeToRun", r.id));
  }, [runs, invoke, connectionState]);

  useEffect(() => {
    const onLoopRunStateChanged = async (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      const { runId, newStatus } = message.payload;
      setRuns((prev) =>
        prev.map((run) => (run.id === runId ? { ...run, status: newStatus } : run)),
      );
    };

    const onNodeStateChanged = async (message: TypedSignalRMessage<"NodeStateChanged">) => {
      const { runId, nodeId, newStatus } = message.payload;
      setRuns((prev) =>
        prev.map((run) =>
          run.id === runId
            ? {
                ...run,
                nodes: run.nodes.map((n) =>
                  n.nodeId === nodeId ? { ...n, status: newStatus } : n,
                ),
              }
            : run,
        ),
      );
    };

    const onNodeProgress = async (message: TypedSignalRMessage<"NodeProgress">) => {
      const { runId, line } = message.payload;
      setProgressLines((prev) => ({
        ...prev,
        [runId]: [...(prev[runId] ?? []), line],
      }));
      // Auto-expand runs that have progress output
      setExpandedRuns((prev) => {
        const next = new Set(prev);
        next.add(runId);
        return next;
      });
    };

    on("LoopRunStateChanged", onLoopRunStateChanged);
    on("NodeStateChanged", onNodeStateChanged);
    on("NodeProgress", onNodeProgress);

    return () => {
      off("LoopRunStateChanged", onLoopRunStateChanged);
      off("NodeStateChanged", onNodeStateChanged);
      off("NodeProgress", onNodeProgress);
    };
  }, [on, off]);

  // Auto-scroll live output when new lines arrive
  useEffect(() => {
    Object.entries(progressLines).forEach(([runId]) => {
      const el = logRefs.current[runId];
      if (el) {
        el.scrollTop = el.scrollHeight;
      }
    });
  }, [progressLines]);

  const toggleExpand = (id: string) => {
    setExpandedRuns((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const loadRuns = async () => {
    try {
      const data = await loopRunService.getAll();
      setRuns(data);
    } catch (error) {
      console.error("Failed to load loop runs:", error);
    } finally {
      setIsLoading(false);
    }
  };

  const handleCancel = async (id: string) => {
    try {
      await loopRunService.cancel(id);
      setRuns((prev) =>
        prev.map((run) => (run.id === id ? { ...run, status: LoopRunStatus.Cancelled } : run)),
      );
    } catch (error) {
      setErrorText(errorMessage(error, "Failed to cancel run."));
    }
  };

  const handlePause = async (id: string) => {
    try {
      await loopRunService.pause(id);
      setRuns((prev) => prev.map((run) => (run.id === id ? { ...run, isPaused: true } : run)));
    } catch (error) {
      setErrorText(errorMessage(error, "Failed to pause run."));
    }
  };

  const handleResume = async (id: string) => {
    try {
      await loopRunService.resume(id);
      setRuns((prev) => prev.map((run) => (run.id === id ? { ...run, isPaused: false } : run)));
    } catch (error) {
      setErrorText(errorMessage(error, "Failed to resume run."));
    }
  };

  const statusColors: Record<string, string> = {
    [LoopRunStatus.Running]: "#3b82f6",
    [LoopRunStatus.Completed]: "#22c55e",
    [LoopRunStatus.Failed]: "#ef4444",
    [LoopRunStatus.Cancelled]: "#6b7280",
  };

  const nodeStatusColors: Record<string, string> = {
    [LoopRunNodeStatus.Pending]: "#6b7280",
    [LoopRunNodeStatus.Running]: "#3b82f6",
    [LoopRunNodeStatus.Succeeded]: "#22c55e",
    [LoopRunNodeStatus.Failed]: "#ef4444",
    [LoopRunNodeStatus.Skipped]: "#4b5563",
    [LoopRunNodeStatus.WaitingHuman]: "#f59e0b",
  };

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading loop runs...</p>
      </div>
    );
  }

  return (
    <div className="page-container">
      <div className="loop-runs-header">
        <h1 className="page-title">Loop Run Monitor</h1>
      </div>
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />
      <div className="loop-runs-list">
        {runs.map((run) => (
          <div key={run.id} className="loop-run-item">
            <div className="loop-run-main">
              <div
                className="loop-run-status-dot"
                style={{ backgroundColor: statusColors[run.status] }}
              />
              <div className="loop-run-info">
                <Link to={`/loop-runs/${run.id}/events`} className="loop-run-name">
                  Run {run.id.slice(0, 8)}
                </Link>
                <div className="loop-run-meta">
                  {new Date(run.startedAt).toLocaleString()} &middot; {run.nodeExecutionCount}{" "}
                  executions
                  {run.isPaused && " &middot; Paused"}
                </div>
              </div>
              <span className={`loop-run-status ${run.status.toLowerCase()}`}>{run.status}</span>
            </div>
            <div className="loop-run-nodes">
              {run.nodes.map((node) => (
                <div key={node.id} className="loop-run-node">
                  <span className="loop-run-node-label">{node.nodeLabel}</span>
                  <span
                    className={`loop-run-node-status ${node.status.toLowerCase()}`}
                    style={{ color: nodeStatusColors[node.status] }}
                  >
                    {node.status}
                  </span>
                </div>
              ))}
            </div>
            {(progressLines[run.id]?.length ?? 0) > 0 && (
              <>
                <button
                  className="btn btn-secondary btn-small btn-toggle-log"
                  onClick={() => toggleExpand(run.id)}
                >
                  {expandedRuns.has(run.id) ? "▼ Hide" : "▶ Show"} Live Output (
                  {progressLines[run.id]?.length ?? 0} lines)
                </button>
                {expandedRuns.has(run.id) && (
                  <div
                    className="live-output-panel"
                    ref={(el) => {
                      logRefs.current[run.id] = el;
                    }}
                  >
                    {progressLines[run.id]?.map((line, i) => (
                      <div key={i} className="live-output-line">
                        {line}
                      </div>
                    ))}
                  </div>
                )}
              </>
            )}
            {run.status === LoopRunStatus.Running && (
              <div style={{ display: "flex", gap: "0.5rem" }}>
                {run.isPaused ? (
                  <button
                    className="btn btn-primary btn-small"
                    onClick={() => handleResume(run.id)}
                  >
                    Resume
                  </button>
                ) : (
                  <button
                    className="btn btn-secondary btn-small"
                    onClick={() => handlePause(run.id)}
                  >
                    Pause
                  </button>
                )}
                <button className="btn btn-danger btn-small" onClick={() => handleCancel(run.id)}>
                  Cancel
                </button>
              </div>
            )}
          </div>
        ))}
      </div>
      <style>{`
        .loop-runs-header {
          margin-bottom: 1rem;
        }

        .loop-runs-list {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
        }

        .loop-run-item {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          padding: 1rem;
          border: 1px solid #2d2d44;
        }

        .loop-run-main {
          display: flex;
          align-items: center;
          gap: 0.75rem;
        }

        .loop-run-status-dot {
          width: 0.75rem;
          height: 0.75rem;
          border-radius: 50%;
          flex-shrink: 0;
        }

        .loop-run-info {
          flex: 1;
        }

        .loop-run-name {
          font-size: 0.875rem;
          font-weight: 500;
          color: #e0e0e0;
          text-decoration: none;
        }

        .loop-run-name:hover {
          color: #60a5fa;
        }

        .loop-run-meta {
          font-size: 0.75rem;
          color: #707090;
        }

        .loop-run-status {
          font-size: 0.7rem;
          padding: 0.125rem 0.5rem;
          border-radius: 0.25rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .loop-run-status.completed {
          background-color: #065f46;
          color: #6ee7b7;
        }

        .loop-run-status.running {
          background-color: #1e3a5f;
          color: #60a5fa;
        }

        .loop-run-status.failed {
          background-color: #5f1e1e;
          color: #f87171;
        }

        .loop-run-status.cancelled {
          background-color: #3a3a5c;
          color: #a0a0b0;
        }

        .loop-run-nodes {
          margin-top: 0.5rem;
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .loop-run-node {
          display: flex;
          justify-content: space-between;
          font-size: 0.75rem;
          padding: 0.25rem 0.5rem;
          background-color: #2a2a40;
          border-radius: 0.25rem;
        }

        .loop-run-node-label {
          color: #c0c0d0;
        }

        .btn-toggle-log {
          margin-top: 0.5rem;
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

        .btn-danger {
          background-color: #5f1e1e;
          color: #f87171;
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
