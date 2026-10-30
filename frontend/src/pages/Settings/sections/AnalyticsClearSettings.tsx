import { useEffect, useId, useRef, useState } from "react";
import { analyticsService } from "../../../services/auth";
import type { ApiError } from "../../../types";

const MAX_DAYS = 36500;

type Action = "all" | { days: number };

type Step =
  | { kind: "idle" }
  | { kind: "confirming"; action: Action }
  | { kind: "clearing"; action: Action }
  | { kind: "done" }
  | { kind: "error"; message: string };

const warningFor = (action: Action) =>
  action === "all"
    ? "Removes all stored analytics history. This cannot be undone."
    : `Removes stored analytics history older than ${action.days} ${action.days === 1 ? "day" : "days"}. This cannot be undone.`;

/** Clears the stored analytics after a confirm step. Focus goes to the confirm button, and back to the trigger once the step is cancelled or answered. */
export default function AnalyticsClearSettings() {
  const [step, setStep] = useState<Step>({ kind: "idle" });
  const [daysText, setDaysText] = useState("30");
  const mounted = useRef(true);
  const allButton = useRef<HTMLButtonElement>(null);
  const olderButton = useRef<HTMLButtonElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  const returnFocusTo = useRef<Action | null>(null);
  const id = useId();

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  useEffect(() => {
    if (step.kind === "confirming") {
      confirmButton.current?.focus();
    } else if (step.kind !== "clearing" && returnFocusTo.current) {
      const trigger = returnFocusTo.current === "all" ? allButton : olderButton;
      returnFocusTo.current = null;
      trigger.current?.focus();
    }
  }, [step.kind]);

  const days = /^\d+$/.test(daysText.trim()) ? Number(daysText) : NaN;
  const daysValid = days >= 1 && days <= MAX_DAYS;
  const pending = step.kind === "confirming" || step.kind === "clearing";

  const clear = async (action: Action) => {
    setStep({ kind: "clearing", action });
    returnFocusTo.current = action;
    try {
      await analyticsService.clear(action === "all" ? undefined : action.days);
      if (mounted.current) setStep({ kind: "done" });
    } catch (error) {
      if (mounted.current)
        setStep({
          kind: "error",
          message: (error as ApiError | Error | undefined)?.message || "Could not clear analytics.",
        });
    }
  };

  return (
    <section className="settings-card">
      <div className="settings-card-header">
        <h3 className="settings-card-title">Analytics</h3>
      </div>
      <p className="settings-card-note">
        Clearing removes the stored cost and usage history. Runs ILD still keeps (finished runs are
        kept for a while, pinned ones until unpinned) stay in analytics until they are removed.
      </p>
      <div className="settings-row">
        <div className="settings-row-copy">
          <div className="settings-row-label">Everything</div>
        </div>
        <div className="settings-row-control">
          <button
            ref={allButton}
            type="button"
            className="btn btn-secondary"
            disabled={pending}
            onClick={() => setStep({ kind: "confirming", action: "all" })}
          >
            Clear all analytics
          </button>
        </div>
      </div>
      <div className="settings-row">
        <div className="settings-row-copy">
          <label className="settings-row-label" htmlFor={`${id}-days`}>
            Days
          </label>
        </div>
        <div className="settings-row-control">
          <input
            id={`${id}-days`}
            type="number"
            min={1}
            max={MAX_DAYS}
            step={1}
            value={daysText}
            onChange={(event) => setDaysText(event.target.value)}
          />
          <button
            ref={olderButton}
            type="button"
            className="btn btn-secondary"
            disabled={pending || !daysValid}
            onClick={() => setStep({ kind: "confirming", action: { days } })}
          >
            Clear older than…
          </button>
        </div>
      </div>
      {pending && (
        <div className="settings-row" role="group" aria-label="Confirm clearing analytics">
          <div className="settings-row-copy">
            <p className="settings-row-help" id={`${id}-warning`}>
              {warningFor(step.action)}
            </p>
          </div>
          <div className="settings-row-control">
            <button
              type="button"
              ref={confirmButton}
              aria-describedby={`${id}-warning`}
              className="btn btn-danger btn-sm"
              disabled={step.kind === "clearing"}
              onClick={() => void clear(step.action)}
            >
              Confirm clear
            </button>
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              disabled={step.kind === "clearing"}
              onClick={() => {
                returnFocusTo.current = step.action;
                setStep({ kind: "idle" });
              }}
            >
              Cancel
            </button>
          </div>
        </div>
      )}
      {step.kind === "done" && (
        <p className="settings-card-note" role="status">
          Analytics cleared.
        </p>
      )}
      {step.kind === "error" && (
        <div className="settings-error" role="alert">
          {step.message}
        </div>
      )}
    </section>
  );
}
