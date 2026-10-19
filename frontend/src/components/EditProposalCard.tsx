import { useMemo } from "react";
import { workItemService } from "../services/auth";
import { computeLineDiff } from "../utils/jsonDiff";
import ProposalDecision from "./ProposalDecision";
import type { EditProposalDependency, WorkItemEditProposal } from "../types";
import "./ProposalCard.css";
import "./EditProposalCard.css";

interface EditProposalCardProps {
  proposal: WorkItemEditProposal;
  /** Called once an approve or reject has settled, whatever its outcome, so the owner re-reads. */
  onSettled: () => void;
}

export function DescriptionDiff({ before, after }: { before: string; after: string }) {
  const diff = useMemo(() => computeLineDiff(before, after), [before, after]);
  return (
    <div className="edit-proposal-diff">
      {diff.map((line, idx) => (
        <div key={idx} className={`edit-proposal-diff-line edit-proposal-diff-${line.type}`}>
          <span className="edit-proposal-diff-gutter" aria-hidden="true">
            {line.type === "add" ? "+" : line.type === "del" ? "-" : " "}
          </span>
          <span className="edit-proposal-diff-text">{line.text}</span>
        </div>
      ))}
    </div>
  );
}

export function TagsChange({ before, after }: { before: string[]; after: string[] }) {
  const removed = before.filter((t) => !after.includes(t));
  const added = after.filter((t) => !before.includes(t));
  const kept = after.filter((t) => before.includes(t));
  return (
    <div className="edit-proposal-tags">
      {added.map((t) => (
        <span key={`+${t}`} className="edit-proposal-tag edit-proposal-tag--added" title="Added">
          +{t}
        </span>
      ))}
      {removed.map((t) => (
        <span
          key={`-${t}`}
          className="edit-proposal-tag edit-proposal-tag--removed"
          title="Removed"
        >
          −{t}
        </span>
      ))}
      {kept.map((t) => (
        <span key={`=${t}`} className="edit-proposal-tag">
          {t}
        </span>
      ))}
    </div>
  );
}

function OverrideChange({ before, after }: { before: string | null; after: string }) {
  return (
    <div className="edit-proposal-values">
      <span className="edit-proposal-old">{before || "(none)"}</span>
      <span aria-hidden="true">→</span>
      {after === "" ? (
        <span className="edit-proposal-new edit-proposal-cleared">cleared</span>
      ) : (
        <span className="edit-proposal-new">{after}</span>
      )}
    </div>
  );
}

function DependencyChanges({
  added,
  removed,
}: {
  added: EditProposalDependency[];
  removed: EditProposalDependency[];
}) {
  const line = (verb: string, dependency: EditProposalDependency) => (
    <li key={`${verb}${dependency.id}`}>
      {verb} dependency: #{dependency.id}
      {dependency.title != null && <> {dependency.title}</>}
    </li>
  );
  return (
    <ul className="edit-proposal-dependencies">
      {added.map((d) => line("Add", d))}
      {removed.map((d) => line("Remove", d))}
    </ul>
  );
}

export default function EditProposalCard({ proposal, onSettled }: EditProposalCardProps) {
  const { proposed, snapshot } = proposal;
  const addedDependencies = proposed.addDependencies ?? [];
  const removedDependencies = proposed.removeDependencies ?? [];
  return (
    <div
      className={`proposal-card proposal-card--${proposal.status.toLowerCase()}`}
      data-testid="proposal-card"
    >
      <div className="proposal-header">
        <span className="proposal-heading">Proposed edit to #{proposal.workItemId}</span>
        <span className={`proposal-status proposal-status--${proposal.status.toLowerCase()}`}>
          {proposal.status}
        </span>
      </div>

      {proposal.rationale && <p className="proposal-rationale">{proposal.rationale}</p>}

      <dl className="edit-proposal-fields">
        {proposed.title != null && (
          <>
            <dt>Title</dt>
            <dd>
              <div className="edit-proposal-values">
                <span className="edit-proposal-old">{snapshot.title || "(none)"}</span>
                <span aria-hidden="true">→</span>
                <span className="edit-proposal-new">{proposed.title}</span>
              </div>
            </dd>
          </>
        )}
        {proposed.description != null && (
          <>
            <dt>Description</dt>
            <dd>
              <DescriptionDiff before={snapshot.description ?? ""} after={proposed.description} />
            </dd>
          </>
        )}
        {proposed.tags != null && (
          <>
            <dt>Tags</dt>
            <dd>
              <TagsChange before={snapshot.tags ?? []} after={proposed.tags} />
            </dd>
          </>
        )}
        {proposed.branchNameOverride != null && (
          <>
            <dt>Custom branch</dt>
            <dd>
              <OverrideChange
                before={snapshot.branchNameOverride}
                after={proposed.branchNameOverride}
              />
            </dd>
          </>
        )}
        {proposed.baseBranchOverride != null && (
          <>
            <dt>Base branch</dt>
            <dd>
              <OverrideChange
                before={snapshot.baseBranchOverride}
                after={proposed.baseBranchOverride}
              />
            </dd>
          </>
        )}
        {(addedDependencies.length > 0 || removedDependencies.length > 0) && (
          <>
            <dt>Dependencies</dt>
            <dd>
              <DependencyChanges added={addedDependencies} removed={removedDependencies} />
            </dd>
          </>
        )}
      </dl>

      {proposal.status === "Rejected" && proposal.rejectionReason && (
        <p className="proposal-reason">Reason: {proposal.rejectionReason}</p>
      )}

      <ProposalDecision
        pending={proposal.status === "Pending"}
        approve={() => workItemService.approveEditProposal(proposal.workItemId, proposal.id)}
        reject={(reason) =>
          workItemService.rejectEditProposal(proposal.workItemId, proposal.id, reason)
        }
        onSettled={onSettled}
      />
    </div>
  );
}
