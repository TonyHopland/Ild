import EditProposalCard from "../EditProposalCard";
import { useEditProposals } from "../../hooks/useEditProposals";

/** Agent-proposed edits to one work item. Render it keyed by the item's id. */
export default function EditProposalsPanel({ workItemId }: { workItemId: string }) {
  const { proposals, refresh } = useEditProposals({ workItemId });
  if (!proposals?.length) return null;
  return (
    <div className="wiv2-edit-proposals">
      <span className="detail-label">Proposed edits</span>
      {proposals.map((proposal) => (
        <EditProposalCard key={proposal.id} proposal={proposal} onSettled={refresh} />
      ))}
    </div>
  );
}
