import { useState } from "react";
import { LoopRun, LoopRunStatus, LoopRunNodeStatus, WorkItemStatus } from "../../types";

interface HaltSteerControlsProps {
  /** The work item's current run (detail with nodes + nodeType), or null. */
  run: LoopRun | null;
  workItemStatus: WorkItemStatus;
  /** Halt the in-flight AI node. */
  onHalt?: () => void | Promise<unknown>;
  /** Resume a halted run with optional steering guidance. */
  onResumeSteer?: (note: string) => void | Promise<unknown>;
  /** Cleanup handlers reused for a halted run. */
  onCleanupDone?: () => void | Promise<unknown>;
  onCleanupBacklog?: () => void | Promise<unknown>;
}

// The run/node status fields arrive as strings from the API but can be numeric
// in some payloads; normalize both so the control shows reliably.
function isRunStatus(value: unknown, target: LoopRunStatus): boolean {
  if (value === target) return true;
  if (typeof value === "number") {
    const map: Record<number, LoopRunStatus> = {
      0: LoopRunStatus.Running,
      1: LoopRunStatus.Completed,
      2: LoopRunStatus.Failed,
      3: LoopRunStatus.Cancelled,
      4: LoopRunStatus.WaitingHuman,
    };
    return map[value] === target;
  }
  return false;
}

function isNodeRunning(value: unknown): boolean {
  return value === LoopRunNodeStatus.Running || value === 1;
}

/**
 * Halt-and-steer control surfaced wherever the live view of a running work item
 * is shown. While an AI node is in flight it renders a Halt button; once the run
 * is halted it renders the steer window (note + Resume + Cleanup) so the human
 * can continue the same agent session or wind the run down — right where they
 * were watching, not buried in a node detail.
 */
export default function HaltSteerControls({
  run,
  workItemStatus,
  onHalt,
  onResumeSteer,
  onCleanupDone,
  onCleanupBacklog,
}: HaltSteerControlsProps) {
  const [halting, setHalting] = useState(false);
  const [resuming, setResuming] = useState(false);
  const [steerNote, setSteerNote] = useState("");
  const [errorText, setErrorText] = useState("");

  if (!run) return null;

  const runningAiNode = run.nodes?.find((n) => n.nodeType === "AI" && isNodeRunning(n.status));
  const canHalt =
    !!onHalt &&
    workItemStatus === WorkItemStatus.Running &&
    isRunStatus(run.status, LoopRunStatus.Running) &&
    !!runningAiNode;
  const isHalted =
    !!onResumeSteer && isRunStatus(run.status, LoopRunStatus.WaitingHuman) && !!run.isHalted;

  if (!canHalt && !isHalted) return null;

  const handleHalt = async () => {
    if (!onHalt) return;
    setErrorText("");
    setHalting(true);
    try {
      await onHalt();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to halt run.");
    } finally {
      setHalting(false);
    }
  };

  const handleResume = async () => {
    if (!onResumeSteer) return;
    setErrorText("");
    setResuming(true);
    try {
      await onResumeSteer(steerNote);
      setSteerNote("");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to resume run.");
    } finally {
      setResuming(false);
    }
  };

  return (
    <div className="wiv2-halt-steer">
      {errorText && (
        <div className="wiv2-error" role="alert">
          {errorText}
          <button type="button" className="wiv2-error-close" onClick={() => setErrorText("")}>
            ✕
          </button>
        </div>
      )}
      {canHalt && (
        <div className="wiv2-halt-bar">
          <button
            type="button"
            className="btn btn-sm btn-warning"
            onClick={() => void handleHalt()}
            disabled={halting}
            title="Interrupt this AI node now, then resume it with optional guidance"
          >
            {halting ? "Halting…" : "Halt AI node"}
          </button>
        </div>
      )}
      {isHalted && (
        <div className="wiv2-feedback">
          <div className="wiv2-feedback-title">Halted — steer &amp; resume</div>
          <textarea
            className="feedback-textarea"
            value={steerNote}
            onChange={(e) => setSteerNote(e.target.value)}
            placeholder="Optional guidance for the resumed agent — leave blank to just continue…"
            rows={3}
          />
          <div className="feedback-actions">
            <button
              type="button"
              className="btn btn-sm btn-primary"
              onClick={() => void handleResume()}
              disabled={resuming}
            >
              {resuming ? "Resuming…" : "Resume"}
            </button>
            {onCleanupDone && (
              <button
                type="button"
                className="btn btn-sm btn-warning"
                onClick={() => void onCleanupDone()}
              >
                Cleanup -&gt; Done
              </button>
            )}
            {onCleanupBacklog && (
              <button
                type="button"
                className="btn btn-sm btn-secondary"
                onClick={() => void onCleanupBacklog()}
              >
                Cleanup -&gt; Backlog
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
