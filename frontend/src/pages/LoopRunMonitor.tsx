import { useState, useEffect } from "react";
import { LoopRun, LoopRunStatus } from "../types";
import { loopRunService } from "../services/auth";
import { useSignalR } from "../hooks/useSignalR";

export default function LoopRunMonitor() {
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const { on } = useSignalR();

  useEffect(() => {
    loadRuns();
  }, []);

  useEffect(() => {
    on("loop_run_started" as any, async (message: any) => {
      const newRun = message.payload as LoopRun;
      setRuns((prev) => [newRun, ...prev]);
    });

    on("loop_run_updated" as any, async (message: any) => {
      const updated = message.payload as LoopRun;
      setRuns((prev) => prev.map((run) => (run.id === updated.id ? updated : run)));
    });
  }, [on]);

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
      console.error("Failed to cancel run:", error);
    }
  };

  const statusColors: Record<LoopRunStatus, string> = {
    [LoopRunStatus.Pending]: "#f59e0b",
    [LoopRunStatus.Running]: "#3b82f6",
    [LoopRunStatus.Completed]: "#22c55e",
    [LoopRunStatus.Failed]: "#ef4444",
    [LoopRunStatus.Cancelled]: "#6b7280",
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
      <div className="loop-runs-list">
        {runs.map((run) => (
          <div key={run.id} className="loop-run-item">
            <div className="loop-run-main">
              <div
                className="loop-run-status-dot"
                style={{ backgroundColor: statusColors[run.status] }}
              />
              <div className="loop-run-info">
                <div className="loop-run-name">{run.templateName}</div>
                <div className="loop-run-meta">
                  {new Date(run.startedAt).toLocaleString()}
                  {run.durationMs != null && ` &middot; ${run.durationMs}ms`}
                </div>
              </div>
              <span className={`loop-run-status ${run.status.toLowerCase()}`}>{run.status}</span>
            </div>
            {run.error && <div className="loop-run-error">{run.error}</div>}
            <div className="loop-run-steps">
              {run.steps.map((step) => (
                <div key={step.id} className="loop-run-step">
                  <span className="loop-run-step-name">{step.stepName}</span>
                  <span className={`loop-run-step-status ${step.status.toLowerCase()}`}>
                    {step.status}
                  </span>
                </div>
              ))}
            </div>
            {run.status === LoopRunStatus.Running && (
              <button className="btn btn-danger btn-small" onClick={() => handleCancel(run.id)}>
                Cancel
              </button>
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

        .loop-run-status.pending {
          background-color: #5f4a1e;
          color: #fbbf24;
        }

        .loop-run-status.cancelled {
          background-color: #3a3a5c;
          color: #a0a0b0;
        }

        .loop-run-error {
          margin-top: 0.5rem;
          font-size: 0.8rem;
          color: #f87171;
          padding: 0.5rem;
          background-color: #2a1e1e;
          border-radius: 0.25rem;
        }

        .loop-run-steps {
          margin-top: 0.5rem;
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .loop-run-step {
          display: flex;
          justify-content: space-between;
          font-size: 0.75rem;
          padding: 0.25rem 0.5rem;
          background-color: #2a2a40;
          border-radius: 0.25rem;
        }

        .loop-run-step-name {
          color: #c0c0d0;
        }

        .loop-run-step-status {
          color: #707090;
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
