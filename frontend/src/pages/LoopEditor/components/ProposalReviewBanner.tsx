import LoopTemplateChangeList from "../../../components/LoopTemplateChangeList";
import { LoopTemplateProposalSource } from "../../../components/LoopTemplateProposalCard";
import ProposalDecision from "../../../components/ProposalDecision";
import { loopTemplateProposalService } from "../../../services/auth";
import type { LoopTemplateProposal } from "../../../types";
import "../../../components/ProposalCard.css";
import "../../../components/LoopTemplateProposalCard.css";

interface ProposalReviewBannerProps {
  proposal: LoopTemplateProposal;
  /** Back to the saved version; the proposal stays as it is. */
  onDiscard: () => void;
  /** The proposal was rejected here. */
  onRejected: () => void;
  /** A reject was refused (decided or gone stale first): re-read the proposal. */
  onRefused: () => void;
}

/**
 * Over the Loop Editor while a Loop Template Proposal is the unsaved working
 * copy: why it was proposed, where it came from and what it changes, with
 * Discard and, while it is still pending, Reject. Saving goes through the
 * editor's own Save. Once the proposal is no longer pending it says so, and a
 * save is an ordinary save.
 */
export default function ProposalReviewBanner({
  proposal,
  onDiscard,
  onRejected,
  onRefused,
}: ProposalReviewBannerProps) {
  const pending = proposal.status === "Pending";
  return (
    <section className="proposal-card proposal-review-banner" aria-label="Proposal under review">
      <div className="proposal-review-header">
        <span>
          Reviewing a proposed change, made against version {proposal.baseVersion}. It is not saved
          until you save it.
        </span>
        <span className="proposal-review-source">
          <LoopTemplateProposalSource proposal={proposal} />
        </span>
      </div>
      <p className="edit-proposal-rationale">{proposal.rationale}</p>
      {!pending && (
        <p className="proposal-review-closed" role="status">
          {proposal.status === "Stale"
            ? `This proposal is Stale: the template was saved after it was made${proposal.latestVersion ? ` (it is now version ${proposal.latestVersion})` : ""}, so it can no longer be applied. Saving now saves your copy as a new version in place of that one, and anything it changed is lost.`
            : `This proposal is no longer pending (${proposal.status}). Saving now saves your copy as an ordinary new version.`}
        </p>
      )}
      <LoopTemplateChangeList changes={proposal.changes} />
      <div className="proposal-review-actions">
        <ProposalDecision
          pending={pending}
          leadingActions={
            <button type="button" className="btn btn-secondary btn-sm" onClick={onDiscard}>
              Discard
            </button>
          }
          reject={(reason) => loopTemplateProposalService.reject(proposal.id, reason)}
          onSettled={(refusal, decided) => {
            if (decided) onRejected();
            else if (refusal !== null) onRefused();
          }}
        />
      </div>
    </section>
  );
}
