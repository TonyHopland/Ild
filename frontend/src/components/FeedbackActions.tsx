interface FeedbackActionsProps {
  actions: string | null | undefined;
  onApprove: () => void;
  onReject: () => void;
  onRespond: () => void;
}

/**
 * Renders the Approve / Respond / Reject buttons based on the
 * comma-separated <c>humanFeedbackActions</c> string from the work item.
 * Defaults to Approve + Reject when the string is empty.
 */
export default function FeedbackActions({
  actions,
  onApprove,
  onReject,
  onRespond,
}: FeedbackActionsProps) {
  const actionList = actions ? actions.split(",").map((a) => a.trim()) : ["OnSuccess", "OnFailure"];

  return (
    <div className="feedback-actions">
      {actionList.includes("OnSuccess") && (
        <button type="button" className="btn btn-sm btn-primary" onClick={onApprove}>
          Approve
        </button>
      )}
      {actionList.includes("OnRespond") && (
        <button type="button" className="btn btn-sm btn-warning" onClick={onRespond}>
          Respond
        </button>
      )}
      {actionList.includes("OnFailure") && (
        <button type="button" className="btn btn-sm btn-danger" onClick={onReject}>
          Reject
        </button>
      )}
    </div>
  );
}
