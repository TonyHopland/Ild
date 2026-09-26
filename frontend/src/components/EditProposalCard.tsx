import { useEffect, useMemo, useRef, useState } from "react";
import { workItemService } from "../services/auth";
import { computeLineDiff } from "../utils/jsonDiff";
import type { ApiError, WorkItemEditProposal } from "../types";
import "./EditProposalCard.css";

interface EditProposalCardProps {
  proposal: WorkItemEditProposal;
  /** Called once an approve or reject has settled, whatever its outcome, so the owner re-reads. */
  onSettled: () => void;
}

function failureMessage(err: unknown): string | null {
  const status = (err as Partial<ApiError> | null)?.status;
  if (status === 409) return null;
  return (err as Partial<ApiError> | null)?.message || "The request failed.";
}

function DescriptionDiff({ before, after }: { before: string; after: string }) {
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

function TagsChange({ before, after }: { before: string[]; after: string[] }) {
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

export default function EditProposalCard({ proposal, onSettled }: EditProposalCardProps) {
  const { proposed, snapshot } = proposal;
  const isPending = proposal.status === "Pending";

  const [approveBusy, setApproveBusy] = useState(false);
  const [approveError, setApproveError] = useState<string | null>(null);
  const [rejectOpen, setRejectOpen] = useState(false);
  const [reasonDraft, setReasonDraft] = useState("");
  const [rejectBusy, setRejectBusy] = useState(false);
  const [rejectError, setRejectError] = useState<string | null>(null);
  const decisionBusy = approveBusy || rejectBusy;

  const mountedRef = useRef(true);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const approve = async () => {
    setApproveBusy(true);
    setApproveError(null);
    try {
      await workItemService.approveEditProposal(proposal.workItemId, proposal.id);
    } catch (err) {
      if (mountedRef.current) setApproveError(failureMessage(err));
    }
    if (!mountedRef.current) return;
    setApproveBusy(false);
    onSettled();
  };

  const reject = async () => {
    setRejectBusy(true);
    setRejectError(null);
    const reason = reasonDraft.trim();
    try {
      await workItemService.rejectEditProposal(
        proposal.workItemId,
        proposal.id,
        reason || undefined,
      );
    } catch (err) {
      if (mountedRef.current) setRejectError(failureMessage(err));
    }
    if (!mountedRef.current) return;
    setRejectBusy(false);
    onSettled();
  };

  return (
    <div className={`edit-proposal-card edit-proposal-card--${proposal.status.toLowerCase()}`}>
      <div className="edit-proposal-header">
        <span className="edit-proposal-heading">Proposed edit to #{proposal.workItemId}</span>
        <span
          className={`edit-proposal-status edit-proposal-status--${proposal.status.toLowerCase()}`}
        >
          {proposal.status}
        </span>
      </div>

      {proposal.rationale && <p className="edit-proposal-rationale">{proposal.rationale}</p>}

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
      </dl>

      {proposal.status === "Rejected" && proposal.rejectionReason && (
        <p className="edit-proposal-reason">Reason: {proposal.rejectionReason}</p>
      )}

      {isPending && (
        <div className="edit-proposal-actions">
          <button
            type="button"
            className="btn btn-primary btn-sm"
            onClick={() => void approve()}
            disabled={decisionBusy}
          >
            Approve
          </button>
          {!rejectOpen && (
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              onClick={() => setRejectOpen(true)}
              disabled={decisionBusy}
            >
              Reject
            </button>
          )}
        </div>
      )}
      {isPending && rejectOpen && (
        <div className="edit-proposal-reject">
          <textarea
            aria-label="Rejection reason (optional)"
            placeholder="Why not? (optional)"
            value={reasonDraft}
            onChange={(e) => setReasonDraft(e.target.value)}
            maxLength={2000}
            rows={2}
            disabled={rejectBusy}
          />
          <div className="edit-proposal-actions">
            <button
              type="button"
              className="btn btn-danger btn-sm"
              onClick={() => void reject()}
              disabled={decisionBusy}
            >
              Confirm reject
            </button>
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              onClick={() => {
                setRejectOpen(false);
                setRejectError(null);
              }}
              disabled={rejectBusy}
            >
              Cancel
            </button>
          </div>
        </div>
      )}
      {approveError && (
        <p className="edit-proposal-error" role="alert">
          {approveError}
        </p>
      )}
      {rejectError && (
        <p className="edit-proposal-error" role="alert">
          {rejectError}
        </p>
      )}
    </div>
  );
}
