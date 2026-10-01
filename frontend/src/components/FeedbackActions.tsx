import { useState } from "react";
import ConfirmModal from "./ConfirmModal";

/**
 * An answer waiting on the person to confirm it: the button they pressed and
 * its colour, what it sends, and the `needsConfirm` it was asked under.
 */
interface PendingAnswer {
  label: string;
  tone: string;
  send: () => void;
  askedUnder: (name: string) => boolean;
}

interface FeedbackActionsProps {
  actions: string | null | undefined;
  onApprove: () => void;
  onReject: () => void;
  onEdge: (name: string) => void;
  /**
   * When provided, a Merge button is shown that performs the real remote merge
   * (and optional branch delete) before the loop continues along OnSuccess.
   * Only wired in for PR-awaiting-merge feedback.
   */
  onMerge?: (deleteBranch: boolean) => void;
  /** An answer is in flight; pressing again would submit it a second time. */
  busy?: boolean;
  /** Whether the parked node offers the output of that name to the person answering. */
  isVisible?: (name: string) => boolean;
  /**
   * Whether the parked node asks the person answering to confirm before taking
   * the output of that name. A new function means a new state of the parked
   * node: a confirmation still open from before it is dropped unsent.
   */
  needsConfirm?: (name: string) => boolean;
}

// Tokens in the comma-separated actions string that map to the fixed
// success/failure roles; everything else names one of the node's outputs.
const ROLE_TOKENS = new Set(["OnSuccess", "OnFailure"]);

const APPROVE_TONE = "btn-primary";
const OUTPUT_TONE = "btn-warning";
const REJECT_TONE = "btn-danger";

/**
 * Renders the Approve / Merge / named-output / Reject buttons based on the
 * comma-separated <c>humanFeedbackActions</c> string from the work item.
 * Each wired named output surfaces as its own button (its name is the output
 * sent back to the engine). Defaults to Approve + Reject when empty. An output
 * the node hides has no button, and one it marks for confirmation asks first.
 * Merge is not an output and is always offered, behind its own confirmation.
 */
export default function FeedbackActions({
  actions,
  onApprove,
  onReject,
  onEdge,
  onMerge,
  busy = false,
  isVisible = () => true,
  needsConfirm,
}: FeedbackActionsProps) {
  const [asked, setAsked] = useState<PendingAnswer | null>(null);
  // The answer was for the node as it was then; confirming it now could send it to another one.
  const pending = asked?.askedUnder === needsConfirm ? asked : null;
  const [confirmingMerge, setConfirmingMerge] = useState(false);
  const [deleteBranch, setDeleteBranch] = useState(true);

  const actionList = (
    actions
      ? actions
          .split(",")
          .map((a) => a.trim())
          .filter(Boolean)
      : ["OnSuccess", "OnFailure"]
  ).filter((name) => isVisible(name));

  const customNames = actionList.filter((a) => !ROLE_TOKENS.has(a));

  const answer = (name: string, label: string, tone: string, send: () => void) => () => {
    if (needsConfirm?.(name)) setAsked({ label, tone, send, askedUnder: needsConfirm });
    else send();
  };

  return (
    <div className="feedback-actions">
      {actionList.includes("OnSuccess") && (
        <button
          type="button"
          className={`btn btn-sm ${APPROVE_TONE}`}
          onClick={answer("OnSuccess", "Approve", APPROVE_TONE, onApprove)}
          disabled={busy}
        >
          Approve
        </button>
      )}
      {onMerge && (
        <button
          type="button"
          className="btn btn-sm btn-success"
          onClick={() => setConfirmingMerge(true)}
        >
          Merge
        </button>
      )}
      {customNames.map((name) => (
        <button
          key={name}
          type="button"
          className={`btn btn-sm ${OUTPUT_TONE}`}
          onClick={answer(name, name, OUTPUT_TONE, () => onEdge(name))}
          disabled={busy}
        >
          {name}
        </button>
      ))}
      {actionList.includes("OnFailure") && (
        <button
          type="button"
          className={`btn btn-sm ${REJECT_TONE}`}
          onClick={answer("OnFailure", "Reject", REJECT_TONE, onReject)}
          disabled={busy}
        >
          Reject
        </button>
      )}
      {onMerge && confirmingMerge && (
        <div className="merge-confirm" role="dialog" aria-label="Confirm merge">
          <label className="merge-confirm-option">
            <input
              type="checkbox"
              checked={deleteBranch}
              onChange={(e) => setDeleteBranch(e.target.checked)}
            />
            Delete branch after merge
          </label>
          <div className="merge-confirm-actions">
            <button
              type="button"
              className="btn btn-sm btn-success"
              onClick={() => {
                setConfirmingMerge(false);
                onMerge(deleteBranch);
              }}
            >
              Confirm Merge
            </button>
            <button
              type="button"
              className="btn btn-sm btn-secondary"
              onClick={() => setConfirmingMerge(false)}
            >
              Cancel
            </button>
          </div>
        </div>
      )}
      {pending && (
        <ConfirmModal
          isOpen
          title={`Confirm ${pending.label}`}
          message={`Are you sure you want to take "${pending.label}"? The run moves on as soon as you confirm.`}
          confirmText={pending.label}
          confirmClassName={pending.tone}
          onConfirm={() => {
            setAsked(null);
            pending.send();
          }}
          onCancel={() => setAsked(null)}
        />
      )}
    </div>
  );
}
