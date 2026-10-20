import { useSchedulerState } from "../hooks/useSchedulerState";

/**
 * The scheduler's state in the header, on every page: "Scheduler: Running" or
 * "Scheduler: Paused", and a click flips it. It means the same as the Settings
 * switch (on is running) and reads the same live state.
 */
export default function SchedulerChip() {
  const { running, busy, error, set, clearError } = useSchedulerState();
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
        <span className="scheduler-chip-error" role="alert" onClick={clearError}>
          {error}
        </span>
      )}
    </span>
  );
}
