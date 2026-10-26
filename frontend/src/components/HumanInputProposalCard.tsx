import { humanInputProposalService } from "../services/auth";
import { outputLabel } from "./FeedbackActions";
import MarkdownRenderer from "./MarkdownRenderer";
import ProposalDecision from "./ProposalDecision";
import type { HumanInputProposal } from "../types";
import "./ProposalCard.css";

interface HumanInputProposalCardProps {
  proposal: HumanInputProposal;
  /**
   * Called once an approve or reject has settled, whatever its outcome, so the
   * owner re-reads; with the server's reason when it was refused (the question
   * moved on, or it was decided first).
   */
  onSettled: (refusal: string | null) => void;
}

/**
 * An agent's suggested answer to the question a work item is parked on: the
 * output to take, by the label its button has, the text to send with it, and
 * why — and, while it is pending, Approve, which sends exactly that, or Reject
 * with an optional reason. The text cannot be edited here.
 */
export default function HumanInputProposalCard({
  proposal,
  onSettled,
}: HumanInputProposalCardProps) {
  const status = proposal.status.toLowerCase();

  return (
    <div className={`proposal-card proposal-card--${status}`} data-testid="proposal-card">
      <div className="proposal-header">
        <span className="proposal-heading">
          Suggested answer: <strong>{outputLabel(proposal.output)}</strong>{" "}
        </span>
        <span className={`proposal-status proposal-status--${status}`}>{proposal.status}</span>
      </div>

      <p className="proposal-rationale">{proposal.rationale}</p>

      <div>{proposal.text ? <MarkdownRenderer content={proposal.text} /> : "No comment"}</div>

      {proposal.status === "Rejected" && proposal.rejectionReason && (
        <p className="proposal-reason">Reason: {proposal.rejectionReason}</p>
      )}

      <ProposalDecision
        pending={proposal.status === "Pending"}
        approve={() => humanInputProposalService.approve(proposal.id)}
        reject={(reason) => humanInputProposalService.reject(proposal.id, reason)}
        onSettled={onSettled}
      />
    </div>
  );
}
