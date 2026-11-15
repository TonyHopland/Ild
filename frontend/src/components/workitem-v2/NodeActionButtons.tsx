import { useState, type ReactNode } from "react";

/** One of the two things a person can do with a node that stopped: a button, and what confirming it says and does. */
export interface NodeAction {
  /** The button, and the tooltip that says what it does. */
  label: string;
  title: string;
  /** The confirmation's button, while it runs, and the group's accessible name. */
  confirmLabel: string;
  pendingLabel: string;
  groupLabel: string;
  /** What the person is told before confirming. */
  prompt: ReactNode;
  tone: "danger" | "primary";
  /** Why the action cannot be taken, when it cannot; it shows as the button's tooltip. */
  unavailableReason?: string | null;
  act: (note: string) => void | Promise<unknown>;
}

interface NodeActionButtonsProps {
  first: NodeAction;
  second: NodeAction;
  /** What the person typed for the agent; cleared by `onActed` once an action went through. */
  note: string;
  onActed: () => void;
  onError: (message: string) => void;
  /** Another action on this run is in flight. */
  blocked: boolean;
}

/**
 * Two actions on a node that stopped (restart or resume, retry or resume), each confirmed
 * after saying what it does, sharing the note the person typed above them.
 */
export default function NodeActionButtons({
  first,
  second,
  note,
  onActed,
  onError,
  blocked,
}: NodeActionButtonsProps) {
  const [confirming, setConfirming] = useState<NodeAction | null>(null);
  const [acting, setActing] = useState(false);

  const confirm = async (action: NodeAction) => {
    setActing(true);
    try {
      await action.act(note);
      onActed();
      setConfirming(null);
    } catch (error) {
      onError(error instanceof Error ? error.message : `Failed to ${action.label.toLowerCase()}.`);
    } finally {
      setActing(false);
    }
  };

  if (confirming) {
    return (
      <span className="wiv2-abandon-confirm" role="group" aria-label={confirming.groupLabel}>
        <span className="wiv2-abandon-prompt">{confirming.prompt}</span>
        <button
          type="button"
          className={`btn btn-sm ${confirming.tone === "danger" ? "btn-danger" : "btn-primary"}`}
          onClick={() => void confirm(confirming)}
          disabled={acting || blocked}
        >
          {acting ? confirming.pendingLabel : confirming.confirmLabel}
        </button>
        <button
          type="button"
          className="btn btn-sm btn-secondary"
          onClick={() => setConfirming(null)}
          disabled={acting}
        >
          Cancel
        </button>
      </span>
    );
  }

  return (
    <>
      {[first, second].map((action) => (
        <button
          key={action.label}
          type="button"
          className={`btn btn-sm ${action.tone === "danger" ? "btn-danger" : "btn-primary"}`}
          onClick={() => setConfirming(action)}
          disabled={blocked || !!action.unavailableReason}
          title={action.unavailableReason ?? action.title}
        >
          {action.label}
        </button>
      ))}
    </>
  );
}
