import { useEffect, useId, useRef, useState } from "react";
import { ApiError, Repository } from "../../types";
import { repositoryService } from "../../services/auth";

type Step =
  | { kind: "idle" }
  | { kind: "confirming" }
  | { kind: "resetting" }
  | { kind: "done" }
  | { kind: "error"; message: string };

/**
 * Reset preview data for the repository being edited. Rendered keyed by the
 * repository's id, so its state belongs to that repository alone and a reply for
 * one never lands in another's form (docs/adr/0021-client-state-scoped-to-its-entity.md).
 */
export default function PreviewDataReset({ repository }: { repository: Repository }) {
  const [step, setStep] = useState<Step>({ kind: "idle" });
  const mounted = useRef(true);
  const resetButton = useRef<HTMLButtonElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  // Set when the control that had focus goes away, so it lands back on the reset button.
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
    } else if (returnFocus.current && resetButton.current) {
      returnFocus.current = false;
      resetButton.current.focus();
    }
  }, [step.kind]);

  const reset = async () => {
    setStep({ kind: "resetting" });
    try {
      await repositoryService.resetPreviewData(repository.id);
      returnFocus.current = true;
      if (mounted.current) setStep({ kind: "done" });
    } catch (error) {
      returnFocus.current = true;
      if (mounted.current)
        setStep({
          kind: "error",
          message: (error as ApiError | Error).message || "Could not reset the preview data.",
        });
    }
  };

  if (step.kind === "confirming" || step.kind === "resetting") {
    return (
      <div className="repo-reset-confirm" role="group" aria-label="Reset preview data">
        <span className="repo-hint" id={warning}>
          This wipes the preview data for every work item in {repository.name}.
        </span>
        <div className="delete-confirm">
          <button
            type="button"
            ref={confirmButton}
            aria-describedby={warning}
            className="btn btn-danger btn-small"
            onClick={() => void reset()}
            disabled={step.kind === "resetting"}
          >
            Confirm reset
          </button>
          <button
            type="button"
            className="btn btn-small"
            onClick={() => {
              returnFocus.current = true;
              setStep({ kind: "idle" });
            }}
            disabled={step.kind === "resetting"}
          >
            Cancel
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="repo-reset">
      <button
        ref={resetButton}
        type="button"
        className="btn btn-secondary btn-small"
        onClick={() => setStep({ kind: "confirming" })}
      >
        Reset preview data
      </button>
      {step.kind === "done" && (
        <span className="repo-hint" role="status">
          The preview data was reset.
        </span>
      )}
      {step.kind === "error" && (
        <span className="repo-hint repo-hint-error" role="alert">
          {step.message}
        </span>
      )}
    </div>
  );
}
