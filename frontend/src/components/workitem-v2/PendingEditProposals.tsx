import { Link } from "react-router";
import { useEditProposals } from "../../hooks/useEditProposals";
import type { WorkItemEditProposal } from "../../types";

function proposedFields({ proposed }: WorkItemEditProposal): string[] {
  const fields: string[] = [];
  if (proposed.title != null) fields.push("Title");
  if (proposed.description != null) fields.push("Description");
  if (proposed.tags != null) fields.push("Tags");
  if (proposed.branchNameOverride != null) fields.push("Branch");
  if (proposed.baseBranchOverride != null) fields.push("Base branch");
  return fields;
}

function Source({ proposal }: { proposal: WorkItemEditProposal }) {
  if (proposal.createdByChatSessionId) return <>Chat</>;
  if (proposal.requestedByWorkItemId)
    return (
      <Link to={`/taskboard/${proposal.requestedByWorkItemId}`}>
        #{proposal.requestedByWorkItemId}
      </Link>
    );
  return null;
}

/**
 * The item's pending edit proposals, one line each with who asked for it. They
 * are decided where they were asked for: in the chat, or in the requesting
 * item's Action tab.
 */
export default function PendingEditProposals({ workItemId }: { workItemId: string }) {
  const { proposals } = useEditProposals({ workItemId });
  const pending = (proposals ?? []).filter((p) => p.status === "Pending");
  if (pending.length === 0) return null;
  return (
    <section className="wiv2-pending-proposals" aria-labelledby="wiv2-pending-proposals-heading">
      <h3 id="wiv2-pending-proposals-heading" className="detail-label">
        Proposed edits
      </h3>
      <ul>
        {pending.map((proposal) => (
          <li key={proposal.id}>
            <span>{proposedFields(proposal).join(", ")}</span>
            <span className="wiv2-pending-proposal-source">
              <Source proposal={proposal} />
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
