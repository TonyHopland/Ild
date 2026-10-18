import { Link } from "react-router";
import { loopTemplateProposalService } from "../services/auth";
import { ROUTES } from "../utils/constants";
import LoopTemplateChangeList from "./LoopTemplateChangeList";
import ProposalDecision from "./ProposalDecision";
import type { LoopTemplateProposal } from "../types";
import "./ProposalCard.css";
import "./LoopTemplateProposalCard.css";

/** Where the Loop Editor opens a proposal as its unsaved working copy. */
export function reviewPath(proposal: Pick<LoopTemplateProposal, "id" | "templateId">) {
  return `${ROUTES.LOOP_EDITOR}/${encodeURIComponent(proposal.templateId)}?proposal=${encodeURIComponent(proposal.id)}`;
}

/** Where a proposal came from, linked while it still exists and is the viewer's to open. */
export function LoopTemplateProposalSource({ proposal }: { proposal: LoopTemplateProposal }) {
  const { source } = proposal;
  if (source.kind === "run" && source.workItemId)
    return (
      <Link to={`${ROUTES.TASKBOARD}/${encodeURIComponent(source.workItemId)}`}>
        Proposed by a run of work item #{source.workItemId}
      </Link>
    );
  if (source.kind === "chat" && source.chatSessionId && source.linkable)
    return (
      <Link to={`${ROUTES.CHAT}/${encodeURIComponent(source.chatSessionId)}`}>
        Proposed in a chat
      </Link>
    );
  if (source.kind === "chat") return <>Proposed in another user&apos;s chat</>;
  return <>Source deleted</>;
}

function Outcome({ proposal }: { proposal: LoopTemplateProposal }) {
  switch (proposal.status) {
    case "Applied":
      return (
        <p className="proposal-reason">
          Saved as version {proposal.appliedVersion}
          {proposal.editedBeforeSave ? ", with edits made in the Loop Editor" : ", as is"}.
        </p>
      );
    case "Rejected":
      return proposal.rejectionReason ? (
        <p className="proposal-reason">Reason: {proposal.rejectionReason}</p>
      ) : null;
    case "Stale":
      return (
        <p className="proposal-reason">
          The template was saved after this was proposed, so it can no longer be saved.
        </p>
      );
    default:
      return null;
  }
}

interface LoopTemplateProposalCardProps {
  proposal: LoopTemplateProposal;
  /**
   * Called once a save or reject has settled, whatever its outcome, so the owner
   * re-reads; with the server's reason when it was refused (gone stale, or decided first),
   * and whether the decision landed.
   */
  onSettled: (refusal: string | null, decided: boolean) => void;
  /** Show where it came from: for cards shown away from the run or chat that made it. */
  showSource?: boolean;
}

/**
 * An agent's proposed change to a saved loop template: the template, the
 * version it was made against, why, and what it changes. While it is pending:
 * Save, which saves the document exactly as proposed; Reject with an optional
 * reason; and Review, which opens it in the Loop Editor as an unsaved working copy.
 */
export default function LoopTemplateProposalCard({
  proposal,
  onSettled,
  showSource = false,
}: LoopTemplateProposalCardProps) {
  const status = proposal.status.toLowerCase();
  const pending = proposal.status === "Pending";

  return (
    <div className={`proposal-card proposal-card--${status}`} data-testid="proposal-card">
      <div className="proposal-header">
        <span className="proposal-heading">
          Proposed change to loop template{" "}
          {proposal.templateName ? <strong>{proposal.templateName}</strong> : "since deleted"}, made
          against version {proposal.baseVersion}
        </span>
        <span className={`proposal-status proposal-status--${status}`}>{proposal.status}</span>
      </div>

      <p className="proposal-rationale">{proposal.rationale}</p>

      <LoopTemplateChangeList changes={proposal.changes} />

      <Outcome proposal={proposal} />

      {showSource && (
        <div className="proposal-source">
          <LoopTemplateProposalSource proposal={proposal} />
        </div>
      )}

      <ProposalDecision
        pending={pending}
        approve={() => loopTemplateProposalService.save(proposal.id)}
        reject={(reason) => loopTemplateProposalService.reject(proposal.id, reason)}
        onSettled={onSettled}
        approveLabel="Save"
        extraActions={
          <Link className="btn btn-secondary btn-sm" to={reviewPath(proposal)}>
            Review
          </Link>
        }
      />
    </div>
  );
}
