import { useState } from "react";

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
}

// Tokens in the comma-separated actions string that map to the fixed
// success/failure roles; everything else is a named custom edge.
const ROLE_TOKENS = new Set(["OnSuccess", "OnFailure"]);

/**
 * Renders the Approve / Merge / custom-edge / Reject buttons based on the
 * comma-separated <c>humanFeedbackActions</c> string from the work item.
 * Each connected custom edge surfaces as its own button (its name is the edge
 * key sent back to the engine). Defaults to Approve + Reject when empty.
 */
export default function FeedbackActions({
  actions,
  onApprove,
  onReject,
  onEdge,
  onMerge,
}: FeedbackActionsProps) {
  const [confirmingMerge, setConfirmingMerge] = useState(false);
  const [deleteBranch, setDeleteBranch] = useState(true);

  const actionList = actions
    ? actions
        .split(",")
        .map((a) => a.trim())
        .filter(Boolean)
    : ["OnSuccess", "OnFailure"];

  const customNames = actionList.filter((a) => !ROLE_TOKENS.has(a));

  return (
    <div className="feedback-actions">
      {actionList.includes("OnSuccess") && (
        <button type="button" className="btn btn-sm btn-primary" onClick={onApprove}>
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
          className="btn btn-sm btn-warning"
          onClick={() => onEdge(name)}
        >
          {name}
        </button>
      ))}
      {actionList.includes("OnFailure") && (
        <button type="button" className="btn btn-sm btn-danger" onClick={onReject}>
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
    </div>
  );
}
