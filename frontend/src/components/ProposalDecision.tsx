import { useEffect, useRef, useState } from "react";
import type { ApiError } from "../types";

interface ProposalDecisionProps {
  /** Only a pending proposal offers a decision; an error from one already made stays shown. */
  pending: boolean;
  approve: () => Promise<unknown>;
  reject: (reason: string | undefined) => Promise<unknown>;
  /** Called once an approve or reject has settled, whatever its outcome, so the owner re-reads. */
  onSettled: () => void;
}

/** A 409 means it was decided elsewhere first: the owner's re-read shows how, so it is no error. */
function failureMessage(err: unknown): string | null {
  const status = (err as Partial<ApiError> | null)?.status;
  if (status === 409) return null;
  return (err as Partial<ApiError> | null)?.message || "The request failed.";
}

/**
 * Approve, and Reject with an optional reason, for a proposal a human decides.
 * The busy flags, the reason being typed and the errors are this card's own.
 */
export default function ProposalDecision({
  pending,
  approve,
  reject,
  onSettled,
}: ProposalDecisionProps) {
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

  const handleApprove = async () => {
    setApproveBusy(true);
    setApproveError(null);
    try {
      await approve();
    } catch (err) {
      if (mountedRef.current) setApproveError(failureMessage(err));
    }
    if (!mountedRef.current) return;
    setApproveBusy(false);
    onSettled();
  };

  const handleReject = async () => {
    setRejectBusy(true);
    setRejectError(null);
    const reason = reasonDraft.trim();
    try {
      await reject(reason || undefined);
    } catch (err) {
      if (mountedRef.current) setRejectError(failureMessage(err));
    }
    if (!mountedRef.current) return;
    setRejectBusy(false);
    onSettled();
  };

  return (
    <>
      {pending && (
        <div className="edit-proposal-actions">
          <button
            type="button"
            className="btn btn-primary btn-sm"
            onClick={() => void handleApprove()}
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
      {pending && rejectOpen && (
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
              onClick={() => void handleReject()}
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
    </>
  );
}
