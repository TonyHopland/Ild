import { useEffect, useRef, useState, type ReactNode } from "react";
import type { ApiError } from "../types";

interface ProposalDecisionProps {
  /** Only a pending proposal offers a decision; an error from one already made stays shown. */
  pending: boolean;
  /** Without it there is no approve button, only Reject. */
  approve?: () => Promise<unknown>;
  reject: (reason: string | undefined) => Promise<unknown>;
  /**
   * Called once an approve or reject has settled, whatever its outcome, so the
   * owner re-reads; with the server's reason when it was refused because the
   * proposal had been decided or gone stale first, and whether the decision landed.
   */
  onSettled: (refusal: string | null, decided: boolean) => void;
  /** What the approve button says. */
  approveLabel?: string;
  /** More actions for a pending proposal, after Approve and Reject. */
  extraActions?: ReactNode;
}

/** A 409 means it was decided elsewhere first: the owner's re-read shows how, so it is no error here. */
function failureMessage(err: unknown): string | null {
  if (isRefusal(err)) return null;
  return (err as Partial<ApiError> | null)?.message || "The request failed.";
}

function isRefusal(err: unknown): boolean {
  return (err as Partial<ApiError> | null)?.status === 409;
}

function refusalOf(err: unknown): string | null {
  return isRefusal(err) ? (err as Partial<ApiError>).message || "It was already decided." : null;
}

/**
 * Approve (when given), and Reject with an optional reason, for a proposal a human decides.
 * The busy flags, the reason being typed and the errors are this card's own.
 */
export default function ProposalDecision({
  pending,
  approve,
  reject,
  onSettled,
  approveLabel = "Approve",
  extraActions,
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

  const handleApprove = async (approve: () => Promise<unknown>) => {
    setApproveBusy(true);
    setApproveError(null);
    let refusal: string | null = null;
    let decided = false;
    try {
      await approve();
      decided = true;
    } catch (err) {
      refusal = refusalOf(err);
      if (mountedRef.current) setApproveError(failureMessage(err));
    }
    if (!mountedRef.current) return;
    setApproveBusy(false);
    onSettled(refusal, decided);
  };

  const handleReject = async () => {
    setRejectBusy(true);
    setRejectError(null);
    const reason = reasonDraft.trim();
    let refusal: string | null = null;
    let decided = false;
    try {
      await reject(reason || undefined);
      decided = true;
    } catch (err) {
      refusal = refusalOf(err);
      if (mountedRef.current) setRejectError(failureMessage(err));
    }
    if (!mountedRef.current) return;
    setRejectBusy(false);
    onSettled(refusal, decided);
  };

  return (
    <>
      {pending && (
        <div className="edit-proposal-actions">
          {approve && (
            <button
              type="button"
              className="btn btn-primary btn-sm"
              onClick={() => void handleApprove(approve)}
              disabled={decisionBusy}
            >
              {approveLabel}
            </button>
          )}
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
          {extraActions}
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
