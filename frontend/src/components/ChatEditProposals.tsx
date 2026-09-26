import EditProposalCard from "./EditProposalCard";
import { useEditProposals } from "../hooks/useEditProposals";

/** The edit proposals one chat has made. Render it keyed by the chat's id. */
export default function ChatEditProposals({ chatSessionId }: { chatSessionId: string }) {
  const { proposals, refresh } = useEditProposals({ chatSessionId });
  if (!proposals?.length) return null;
  return (
    <div className="chat-edit-proposals">
      {proposals.map((proposal) => (
        <EditProposalCard key={proposal.id} proposal={proposal} onSettled={refresh} />
      ))}
    </div>
  );
}
