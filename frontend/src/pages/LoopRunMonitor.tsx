import { useState, useEffect, useRef } from "react";
import { Link } from "react-router-dom";
import { LoopRun, LoopRunNodeStatus, LoopRunStatus } from "../types";
import type { TypedSignalRMessage } from "../types/signalr";
import { loopRunService } from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";
import ErrorBanner from "../components/ErrorBanner";

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

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
}

export default function LoopRunMonitor() {
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [errorText, setErrorText] = useState("");
  const { on, off, invoke, connectionState } = useSignalR("/hubs/loop-run");
  const reloadTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Coalesce bursty SignalR events into one REST refetch. The
  // NodeStateChanged payload doesn't carry per-run-node fields like
  // Output / CompletedAt / RetryCount, and the same template node id
  // can map to multiple LoopRunNode rows when a loop iterates, so
  // patching state in place corrupted the timeline (the user reported
  // having to refresh the page to see the correct state).
  const scheduleReload = () => {
    if (reloadTimerRef.current) return;
    reloadTimerRef.current = setTimeout(() => {
      reloadTimerRef.current = null;
      void loadRuns();
    }, 150);
  };

  useEffect(
    () => () => {
      if (reloadTimerRef.current) {
        clearTimeout(reloadTimerRef.current);
        reloadTimerRef.current = null;
      }
    },
    [],
  );

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
      const status = normalizeLoopRunStatus(newStatus);
      setRuns((prev) => prev.map((run) => (run.id === runId ? { ...run, status } : run)));
      if (status !== LoopRunStatus.Running) {
        scheduleReload();
      }
    };

    const onNodeStateChanged = async (_message: TypedSignalRMessage<"NodeStateChanged">) => {
      scheduleReload();
    };

    on("LoopRunStateChanged", onLoopRunStateChanged);
    on("NodeStateChanged", onNodeStateChanged);

    return () => {
      off("LoopRunStateChanged", onLoopRunStateChanged);
      off("NodeStateChanged", onNodeStateChanged);
    };
  }, [on, off]);

  const loadRuns = async () => {
    try {
      const data = await loopRunService.getAll();
      setRuns(
        data.map((run) => ({
          ...run,
          status: normalizeLoopRunStatus(run.status),
          nodes: run.nodes.map((n) => ({ ...n, status: normalizeNodeStatus(n.status) })),
        })),
      );
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

  const handleDelete = async (id: string) => {
    if (!window.confirm("Delete this loop run and all its event history?")) return;
    try {
      await loopRunService.delete(id);
      setRuns((prev) => prev.filter((run) => run.id !== id));
    } catch (error) {
      setErrorText(errorMessage(error, "Failed to delete run."));
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
    [LoopRunNodeStatus.Responded]: "#f59e0b",
  };

  const nodeTypeIcons: Record<string, string> = {
    Start: "\u25B6",
    Cmd: "\u2699",
    AI: "\uD83E\uDD16",
    Human: "\uD83D\uDC64",
    PR: "\uD83D\uDD01",
    Cleanup: "\uD83E\uDDD9",
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
                style={{ backgroundColor: statusColors[run.status] ?? "#6b7280" }}
              />
              <div className="loop-run-info">
                <Link to={`/loop-runs/${run.id}`} className="loop-run-name">
                  Run {run.id.slice(0, 8)}
                </Link>
                <div className="loop-run-meta">
                  {new Date(run.startedAt).toLocaleString()} &middot; {run.nodeExecutionCount}{" "}
                  executions
                  {run.isPaused && " &middot; Paused"}
                </div>
              </div>
              <span
                className={`loop-run-status ${typeof run.status === "string" ? run.status.toLowerCase() : "unknown"}`}
              >
                {run.status}
              </span>
            </div>
            <div className="loop-run-nodes">
              {run.nodes.map((node) => (
                <div key={node.id} className="loop-run-node">
                  <span className="loop-run-node-label">
                    <span className="loop-run-node-icon">
                      {nodeTypeIcons[node.nodeLabel.split(" ")[0]] || "\u25CF"}
                    </span>
                    {node.nodeLabel}
                  </span>
                  <span
                    className={`loop-run-node-status ${typeof node.status === "string" ? node.status.toLowerCase() : "unknown"}`}
                    style={{ color: nodeStatusColors[node.status] ?? "#6b7280" }}
                  >
                    {node.status ?? "Unknown"}
                  </span>
                </div>
              ))}
            </div>
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
            {run.status !== LoopRunStatus.Running && (
              <div style={{ display: "flex", gap: "0.5rem" }}>
                <button
                  className="btn btn-danger btn-small"
                  onClick={() => handleDelete(run.id)}
                  title="Delete this run and its event log"
                >
                  Delete
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
          display: flex;
          align-items: center;
          gap: 0.4rem;
        }

        .loop-run-node-icon {
          font-size: 0.8rem;
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
