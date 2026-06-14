interface FeedbackActionsProps {
  actions: string | null | undefined;
  onApprove: () => void;
  onReject: () => void;
  onEdge: (name: string) => void;
}

// Tokens in the comma-separated actions string that map to the fixed
// success/failure roles; everything else is a named custom edge.
const ROLE_TOKENS = new Set(["OnSuccess", "OnFailure"]);

/**
 * Renders the Approve / custom-edge / Reject buttons based on the
 * comma-separated <c>humanFeedbackActions</c> string from the work item.
 * Each connected custom edge surfaces as its own button (its name is the edge
 * key sent back to the engine). Defaults to Approve + Reject when empty.
 */
export default function FeedbackActions({
  actions,
  onApprove,
  onReject,
  onEdge,
}: FeedbackActionsProps) {
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
    </div>
  );
}
