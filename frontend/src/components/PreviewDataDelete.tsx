import { useEffect, useId, useRef, useState } from "react";
import { ApiError } from "../types";
import { storageService } from "../services/auth";
import "./PreviewDataDelete.css";

type Step =
  | { kind: "idle" }
  | { kind: "confirming" }
  | { kind: "deleting" }
  | { kind: "done" }
  | { kind: "error"; message: string };

/** What the control says where it is used: the repository's Reset, the storage page's Delete. */
export interface PreviewDataDeleteWording {
  trigger: string;
  /** The trigger's accessible name, when its text alone does not say which data it deletes. */
  triggerLabel?: string;
  /** The accessible name of the confirm step. */
  confirmGroup: string;
  warning: string;
  confirm: string;
  done: string;
  /** Shown when the request fails without a reason. */
  failed: string;
}

/**
 * Deletes a repository's kept preview data after a confirm step. Render it keyed
 * by the repository's id, so its state belongs to that repository alone and a
 * reply for one never lands on another's control
 * (docs/adr/0021-client-state-scoped-to-its-entity.md). Focus goes to the confirm
 * button, and back to the trigger once the step is cancelled or answered.
 */
export default function PreviewDataDelete({
  repositoryId,
  wording,
  onDeleted,
}: {
  repositoryId: string;
  wording: PreviewDataDeleteWording;
  /** Called after a delete succeeds, even if this control has gone away since. */
  onDeleted?: () => void;
}) {
  const [step, setStep] = useState<Step>({ kind: "idle" });
  const mounted = useRef(true);
  const triggerButton = useRef<HTMLButtonElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  // Set when the control that had focus goes away, so it lands back on the trigger.
  const returnFocus = useRef(false);
  const warning = useId();

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  useEffect(() => {
    if (step.kind === "confirming") {
      confirmButton.current?.focus();
    } else if (returnFocus.current && triggerButton.current) {
      returnFocus.current = false;
      triggerButton.current.focus();
    }
  }, [step.kind]);

  const remove = async () => {
    setStep({ kind: "deleting" });
    try {
      await storageService.deletePreviewData(repositoryId);
      returnFocus.current = true;
      if (mounted.current) setStep({ kind: "done" });
      onDeleted?.();
    } catch (error) {
      returnFocus.current = true;
      if (mounted.current)
        setStep({
          kind: "error",
          message: (error as ApiError | Error | undefined)?.message || wording.failed,
        });
    }
  };

  if (step.kind === "confirming" || step.kind === "deleting") {
    return (
      <div className="preview-data-delete-confirm" role="group" aria-label={wording.confirmGroup}>
        <span className="preview-data-delete-hint" id={warning}>
          {wording.warning}
        </span>
        <div className="preview-data-delete-actions">
          <button
            type="button"
            ref={confirmButton}
            aria-describedby={warning}
            className="btn btn-danger btn-sm"
            onClick={() => void remove()}
            disabled={step.kind === "deleting"}
          >
            {wording.confirm}
          </button>
          <button
            type="button"
            className="btn btn-secondary btn-sm"
            onClick={() => {
              returnFocus.current = true;
              setStep({ kind: "idle" });
            }}
            disabled={step.kind === "deleting"}
          >
            Cancel
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="preview-data-delete">
      <button
        ref={triggerButton}
        type="button"
        className="btn btn-secondary btn-sm"
        aria-label={wording.triggerLabel}
        onClick={() => setStep({ kind: "confirming" })}
      >
        {wording.trigger}
      </button>
      {step.kind === "done" && (
        <span className="preview-data-delete-hint" role="status">
          {wording.done}
        </span>
      )}
      {step.kind === "error" && (
        <span className="preview-data-delete-error" role="alert">
          {step.message}
        </span>
      )}
    </div>
  );
}
