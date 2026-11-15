import { useEditProposals } from "../../hooks/useEditProposals";
import EditProposalCard from "../EditProposalCard";

/**
 * The item's edit proposals: the pending ones to approve or reject, and the
 * decided and stale ones as a collapsed history. The same proposals can also be
 * decided in the chat or the requesting item's Action tab; whichever comes
 * first wins and the other re-reads.
 */
export default function PendingEditProposals({ workItemId }: { workItemId: string }) {
  const { proposals, refresh } = useEditProposals({ workItemId });
  const all = proposals ?? [];
  const pending = all.filter((p) => p.status === "Pending");
  const history = all.filter((p) => p.status !== "Pending");
  if (all.length === 0) return null;
  return (
    <section className="wiv2-pending-proposals" aria-labelledby="wiv2-pending-proposals-heading">
      {pending.length > 0 && (
        <div className="wiv2-proposals-banner" role="status">
          {pending.length === 1
            ? "1 proposed edit awaiting your decision"
            : `${pending.length} proposed edits awaiting your decision`}
        </div>
      )}
      <h3 id="wiv2-pending-proposals-heading" className="detail-label">
        Proposed edits
        {pending.length > 0 && (
          <span className="wiv2-proposals-badge" aria-label={`${pending.length} pending`}>
            {pending.length}
          </span>
        )}
      </h3>
      {pending.map((proposal) => (
        <EditProposalCard key={proposal.id} proposal={proposal} onSettled={refresh} />
      ))}
      {history.length > 0 && (
        <details className="wiv2-proposals-history">
          <summary>History ({history.length})</summary>
          {history.map((proposal) => (
            <div key={proposal.id}>
              {proposal.status === "Stale" && (
                <p className="wiv2-proposal-stale">
                  The item changed after this was proposed, so it can't be applied.
                </p>
              )}
              <EditProposalCard proposal={proposal} onSettled={refresh} />
            </div>
          ))}
        </details>
      )}
    </section>
  );
}
