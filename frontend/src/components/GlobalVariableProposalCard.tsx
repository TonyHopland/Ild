import { useEffect, useRef, type ReactNode } from "react";
import { globalVariableService } from "../services/auth";
import { DescriptionDiff } from "./EditProposalCard";
import ProposalDecision from "./ProposalDecision";
import type { GlobalVariableProposal } from "../types";
import "./EditProposalCard.css";
import "./GlobalVariableProposalCard.css";

interface GlobalVariableProposalCardProps {
  proposal: GlobalVariableProposal;
  /** Called once an approve or reject has settled, whatever its outcome, so the owner re-reads. */
  onSettled: () => void;
  /** Where the suggestion came from, as the owner shows it. */
  source?: ReactNode;
  /** Given while this card is the one a link points at: called with the card once it is shown. */
  reveal?: (card: HTMLElement) => void;
}

/**
 * An agent's suggested new value for a global variable: the text against the
 * variable's current value (or as a new variable), why, and — while it is
 * pending — Approve, which applies the text exactly as suggested, or Reject
 * with an optional reason. The text cannot be edited here.
 */
export default function GlobalVariableProposalCard({
  proposal,
  onSettled,
  source,
  reveal,
}: GlobalVariableProposalCardProps) {
  const status = proposal.status.toLowerCase();

  // Once each time a link starts pointing at this card, not on every render.
  const cardRef = useRef<HTMLDivElement>(null);
  const revealedRef = useRef(false);
  useEffect(() => {
    if (!reveal) {
      revealedRef.current = false;
      return;
    }
    if (revealedRef.current || !cardRef.current) return;
    revealedRef.current = true;
    reveal(cardRef.current);
  }, [reveal]);

  return (
    <div ref={cardRef} className={`edit-proposal-card edit-proposal-card--${status}`}>
      <div className="edit-proposal-header">
        <span className="edit-proposal-heading">
          Suggested change to <code>{proposal.name}</code>
        </span>
        <span className={`edit-proposal-status edit-proposal-status--${status}`}>
          {proposal.status}
        </span>
      </div>

      <p className="edit-proposal-rationale">{proposal.rationale}</p>

      {!proposal.variableExists && <p className="gv-proposal-new">New variable</p>}
      <DescriptionDiff before={proposal.currentValue ?? ""} after={proposal.value} />

      {proposal.status === "Rejected" && proposal.rejectionReason && (
        <p className="edit-proposal-reason">Reason: {proposal.rejectionReason}</p>
      )}

      {source && <div className="gv-proposal-source">{source}</div>}

      <ProposalDecision
        pending={proposal.status === "Pending"}
        approve={() => globalVariableService.approveProposal(proposal.id)}
        reject={(reason) => globalVariableService.rejectProposal(proposal.id, reason)}
        onSettled={onSettled}
      />
    </div>
  );
}
