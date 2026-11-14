import { useState } from "react";
import { Link } from "react-router";
import {
  HaltReason,
  LoopRun,
  LoopRunNode,
  LoopRunStatus,
  LoopRunNodeStatus,
  WorkItemStatus,
} from "../../types";

interface HaltSteerControlsProps {
  /** The work item's current run (detail with nodes + nodeType), or null. */
  run: LoopRun | null;
  workItemStatus: WorkItemStatus;
  /** Halt the in-flight AI node. */
  onHalt?: () => void | Promise<unknown>;
  /**
   * Restart a node that is Waiting for ILD: its running processes are stopped and the node starts over
   * from scratch, with the optional note after its prompt. Offered with `onResumeWaiting`.
   */
  onRestartWaiting?: (note: string) => void | Promise<unknown>;
  /**
   * Resume a node that is Waiting for ILD in the same session, leaving its processes running. The agent
   * gets only the optional note.
   */
  onResumeWaiting?: (note: string) => void | Promise<unknown>;
  /** Resume a halted run with optional steering guidance. */
  onResumeSteer?: (note: string) => void | Promise<unknown>;
  /** Cleanup handlers reused for a halted run. */
  onCleanupDone?: () => void | Promise<unknown>;
  onCleanupBacklog?: () => void | Promise<unknown>;
  /**
   * Whether to offer the standalone Abandon-run affordance. Defaults to true;
   * pass false where abandoning does not belong (e.g. the action tab, which is
   * only for steering the live run — abandoning lives on the run tab).
   */
  showAbandon?: boolean;
  /** Another action on this run is in flight: every control here waits for it. */
  blocked?: boolean;
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

function isNodeInterrupted(value: unknown): boolean {
  return value === LoopRunNodeStatus.Interrupted || value === 6;
}

/**
 * Whether an AI provider parked this run rather than a person. Read off the
 * run's own `haltReason`, which both run endpoints project as the enum's name —
 * the engine records who parked it, so there is nothing to infer. A human's Halt
 * also ends its node `Interrupted` with an error ("Run cancelled"), so the node
 * rows cannot tell the two apart.
 */
function isThrottleParked(run: LoopRun): boolean {
  return run.haltReason === HaltReason.Throttled;
}

/**
 * Whether the engine parked this run because it had run its budget of AI steps
 * with nobody in the loop. Read off `haltReason` for the same reason the
 * throttle park is: the node rows of a capped park and a human's Halt are
 * indistinguishable. Resuming — with or without a note — refills the budget.
 */
function isStepCapParked(run: LoopRun): boolean {
  return run.haltReason === HaltReason.MaxAiTraversals;
}

/**
 * Whether the engine parked this run before a node whose prompt uses a global
 * variable that is missing or empty. `missingGlobalVariables` is read from the
 * values as they are now, so it shrinks as a person sets them.
 */
function isGlobalMissingParked(run: LoopRun): boolean {
  return run.haltReason === HaltReason.GlobalVariableMissing;
}

/**
 * The node the provider interrupted. It carries the explanation of which Resume
 * the human is about to get (same agent session, or a cold restart of the node)
 * and the provider's own words, which is where the reset time is stated: ILD
 * parses no timestamps and schedules nothing.
 */
function interruptedNode(run: LoopRun): LoopRunNode | null {
  const interrupted = run.nodes?.filter((n) => isNodeInterrupted(n.status)) ?? [];
  return interrupted[interrupted.length - 1] ?? null;
}

/**
 * Halt-and-steer control surfaced wherever the live view of a running work item
 * is shown. While an AI node is in flight it renders a Halt button; once the run
 * is halted it renders the steer window (note + Resume + Cleanup) so the human
 * can continue the same agent session or wind the run down — right where they
 * were watching, not buried in a node detail. Whenever the run is still live —
 * actively running or parked for human feedback — it also renders an Abandon
 * button that stops the run and resets the work item to Backlog, so a run
 * heading the wrong way can be dropped and retried fresh after editing the
 * description, even when the AI is not currently running. The Abandon button is
 * opt-out via `showAbandon` so it only appears on the run tab, not the action tab.
 */
export default function HaltSteerControls({
  run,
  workItemStatus,
  onHalt,
  onResumeSteer,
  onRestartWaiting,
  onResumeWaiting,
  onCleanupDone,
  onCleanupBacklog,
  showAbandon = true,
  blocked = false,
}: HaltSteerControlsProps) {
  const [halting, setHalting] = useState(false);
  const [resuming, setResuming] = useState(false);
  const [abandoning, setAbandoning] = useState(false);
  const [confirmingAbandon, setConfirmingAbandon] = useState(false);
  const [steerNote, setSteerNote] = useState("");
  const [confirmingWaiting, setConfirmingWaiting] = useState<"restart" | "resume" | null>(null);
  const [actingWaiting, setActingWaiting] = useState(false);
  const [errorText, setErrorText] = useState("");

  if (!run) return null;

  const isRunningStatus = isRunStatus(run.status, LoopRunStatus.Running);
  const isWaitingHuman = isRunStatus(run.status, LoopRunStatus.WaitingHuman);
  const runningAiNode = run.nodes?.find((n) => n.nodeType === "AI" && isNodeRunning(n.status));
  const canHalt =
    !!onHalt && workItemStatus === WorkItemStatus.Running && isRunningStatus && !!runningAiNode;
  const isHalted = !!onResumeSteer && isWaitingHuman && !!run.isHalted;
  const throttled = isHalted && isThrottleParked(run);
  const stepCapped = isHalted && isStepCapParked(run);
  const globalMissing = isHalted && isGlobalMissingParked(run);
  // Processes the agent left running when its turn ended: ILD waits for them (or has stopped waiting after
  // too many cycles), and the person can restart or resume the node meanwhile.
  const childWait =
    isHalted && !!run.childWait && !!onRestartWaiting && !!onResumeWaiting ? run.childWait : null;
  const interruption = throttled ? interruptedNode(run) : null;
  // Abandon applies to any non-terminal run — actively running OR parked for
  // human feedback — so a run heading the wrong way can be dropped without
  // first halting it, even when the AI is not currently running. A halted run
  // is excluded because its steer window already offers Cleanup -> Backlog.
  const canAbandon =
    showAbandon && !!onCleanupBacklog && (isRunningStatus || isWaitingHuman) && !isHalted;

  if (!canHalt && !canAbandon && !isHalted) return null;

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

  const handleWaitingAction = async (action: "restart" | "resume") => {
    const act = action === "restart" ? onRestartWaiting : onResumeWaiting;
    if (!act) return;
    setErrorText("");
    setActingWaiting(true);
    try {
      await act(steerNote);
      setSteerNote("");
      setConfirmingWaiting(null);
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : `Failed to ${action} the node.`);
    } finally {
      setActingWaiting(false);
    }
  };

  const handleAbandon = async () => {
    if (!onCleanupBacklog) return;
    setErrorText("");
    setAbandoning(true);
    try {
      await onCleanupBacklog();
      setConfirmingAbandon(false);
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : "Failed to abandon run.");
    } finally {
      setAbandoning(false);
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
      {(canHalt || canAbandon) && (
        <div className="wiv2-halt-bar">
          {canHalt && (
            <button
              type="button"
              className="btn btn-sm btn-warning"
              onClick={() => void handleHalt()}
              disabled={halting || blocked}
              title="Interrupt this AI node now, then resume it with optional guidance"
            >
              {halting ? "Halting…" : "Halt AI node"}
            </button>
          )}
          {canAbandon &&
            (confirmingAbandon ? (
              <span className="wiv2-abandon-confirm" role="group" aria-label="Confirm abandon run">
                <span className="wiv2-abandon-prompt">
                  Abandon this run and reset the work item to Backlog? Its worktree, local branch
                  and AI session data are removed unless it is pinned; the run and its history are
                  kept.
                </span>
                <button
                  type="button"
                  className="btn btn-sm btn-danger"
                  onClick={() => void handleAbandon()}
                  disabled={abandoning || blocked}
                >
                  {abandoning ? "Abandoning…" : "Confirm abandon"}
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-secondary"
                  onClick={() => setConfirmingAbandon(false)}
                  disabled={abandoning}
                >
                  Cancel
                </button>
              </span>
            ) : (
              <button
                type="button"
                className="btn btn-sm btn-danger"
                onClick={() => setConfirmingAbandon(true)}
                disabled={blocked}
                title="Stop this run and send the work item back to Backlog so you can edit the description and try again on a new run. The run's worktree, local branch and AI session data are removed unless it is pinned; the run and its history are kept."
              >
                Abandon run
              </button>
            ))}
        </div>
      )}
      {isHalted && (
        <div className="wiv2-feedback">
          <div className="wiv2-feedback-title">
            {throttled && "Provider interrupted — resume when ready"}
            {stepCapped && "Enough AI steps — carry on?"}
            {childWait?.waiting && "Waiting for ILD — its processes are still running"}
            {childWait && !childWait.waiting && "Processes still running — restart or resume"}
            {globalMissing && "Global variables missing — set them, then resume"}
            {!throttled && !stepCapped && !globalMissing && !childWait && "Halted — steer & resume"}
          </div>
          {stepCapped && (
            <div className="feedback-reason">
              The AI ran {run.aiTraversalCount ?? 0} steps without human input. Resume to let it
              carry on — with guidance if it is heading the wrong way — or abandon the run.
            </div>
          )}
          {globalMissing && (
            <div className="feedback-reason">
              {run.missingGlobalVariables?.length ? (
                <>
                  The next step uses global variables that are missing or empty:{" "}
                  {run.missingGlobalVariables.map((name, i) => (
                    <span key={name}>
                      {i > 0 && ", "}
                      <code>{name}</code>
                    </span>
                  ))}
                  . Set them on the <Link to="/global-variables">Global Variables page</Link>, then
                  resume.
                </>
              ) : (
                <>
                  The next step uses global variables that were missing or empty. Set them on the{" "}
                  <Link to="/global-variables">Global Variables page</Link>, then resume.
                </>
              )}
            </div>
          )}
          {childWait && (
            <div className="feedback-reason">
              <div>
                {childWait.waiting
                  ? "The agent ended its turn while these processes it started were still running. ILD is waiting for them and will wake the agent with how they ended."
                  : "The agent ended its turn with these processes still running several times in a row, so ILD stopped waiting for them."}
              </div>
              <ul className="wiv2-child-processes">
                {childWait.processes.map((p) => (
                  <li key={p.pid}>
                    <code>
                      {p.pid}: {p.commandLine}
                    </code>
                  </li>
                ))}
              </ul>
            </div>
          )}
          {throttled && (
            <div className="feedback-reason">
              <div>{interruption?.error ?? "An AI provider interrupted this run."}</div>
              {interruption?.output && (
                <pre className="wiv2-interruption-output">{interruption.output}</pre>
              )}
            </div>
          )}
          <textarea
            className="feedback-textarea"
            value={steerNote}
            onChange={(e) => setSteerNote(e.target.value)}
            placeholder={
              childWait
                ? "Optional input for the agent — leave blank to add nothing…"
                : globalMissing
                  ? "Optional guidance — it is added to the run's conversation. Leave blank to just continue…"
                  : "Optional guidance for the resumed agent — leave blank to just continue…"
            }
            rows={3}
          />
          <div className="feedback-actions">
            {childWait ? (
              confirmingWaiting ? (
                <span
                  className="wiv2-abandon-confirm"
                  role="group"
                  aria-label={
                    confirmingWaiting === "restart" ? "Confirm restart node" : "Confirm resume node"
                  }
                >
                  <span className="wiv2-abandon-prompt">
                    {confirmingWaiting === "restart"
                      ? "Restart stops the processes that are still running and starts this node again from scratch. The agent is not told about the stopped processes; anything you typed above is added after its prompt."
                      : "Resume runs this node again in the same session and leaves the processes running. The agent gets only what you typed above (or a plain continue) and what it already knows — nothing about the processes."}
                  </span>
                  <button
                    type="button"
                    className={`btn btn-sm ${confirmingWaiting === "restart" ? "btn-danger" : "btn-primary"}`}
                    onClick={() => void handleWaitingAction(confirmingWaiting)}
                    disabled={actingWaiting || blocked}
                  >
                    {confirmingWaiting === "restart"
                      ? actingWaiting
                        ? "Restarting…"
                        : "Confirm restart"
                      : actingWaiting
                        ? "Resuming…"
                        : "Confirm resume"}
                  </button>
                  <button
                    type="button"
                    className="btn btn-sm btn-secondary"
                    onClick={() => setConfirmingWaiting(null)}
                    disabled={actingWaiting}
                  >
                    Cancel
                  </button>
                </span>
              ) : (
                <>
                  <button
                    type="button"
                    className="btn btn-sm btn-danger"
                    onClick={() => setConfirmingWaiting("restart")}
                    disabled={blocked}
                    title="Stop the running processes and start this node again from scratch"
                  >
                    Restart node
                  </button>
                  <button
                    type="button"
                    className="btn btn-sm btn-primary"
                    onClick={() => setConfirmingWaiting("resume")}
                    disabled={blocked}
                    title="Run this node again in the same session, leaving the processes running"
                  >
                    Resume node
                  </button>
                </>
              )
            ) : (
              <button
                type="button"
                className="btn btn-sm btn-primary"
                onClick={() => void handleResume()}
                disabled={resuming || blocked}
              >
                {resuming ? "Resuming…" : "Resume"}
              </button>
            )}
            {onCleanupDone && (
              <button
                type="button"
                className="btn btn-sm btn-warning"
                onClick={() => void onCleanupDone()}
                disabled={blocked}
              >
                Cleanup -&gt; Done
              </button>
            )}
            {onCleanupBacklog && (
              <button
                type="button"
                className="btn btn-sm btn-secondary"
                onClick={() => void onCleanupBacklog()}
                disabled={blocked}
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
