import { useEffect } from "react";
import { useSchedulerState } from "../hooks/useSchedulerState";
import Icon from "./Icon";

const ERROR_SHOWN_MS = 6000;
import "./SchedulerChip.css";

/**
 * The scheduler's state in the header, on every page: "Scheduler: Running" or
 * "Scheduler: Paused", and a click flips it. It means the same as the Settings
 * switch (on is running) and reads the same live state.
 */
export default function SchedulerChip() {
  const { running, busy, error, set, clearError } = useSchedulerState();
  // The message is about the change just tried: it goes by itself after a while,
  // or at once with its Dismiss button.
  useEffect(() => {
    if (!error) return;
    const t = setTimeout(clearError, ERROR_SHOWN_MS);
    return () => clearTimeout(t);
  }, [error, clearError]);
  if (running === null) return null;

  return (
    <span className="scheduler-chip-wrap">
      <button
        type="button"
        role="switch"
        aria-checked={running}
        aria-label="Scheduler running"
        className={`scheduler-chip${running ? "" : " is-paused"}`}
        disabled={busy}
        title={
          running
            ? "The scheduler is starting Ready items. Click to pause it: runs already going carry on to their next node."
            : "The scheduler is paused: Ready items stay queued. Click to resume it."
        }
        onClick={() => void set(!running)}
      >
        <span className="scheduler-chip-dot" aria-hidden="true" />
        <span className="scheduler-chip-label">Scheduler:</span>
        <strong>{running ? "Running" : "Paused"}</strong>
      </button>
      {error && (
        <span className="scheduler-chip-error" role="alert">
          {error}
          <button
            type="button"
            className="scheduler-chip-error-dismiss"
            aria-label="Dismiss"
            onClick={clearError}
          >
            <Icon name="close" size={12} />
          </button>
        </span>
      )}
    </span>
  );
}
