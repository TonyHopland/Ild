import { useState } from "react";
import type { SessionPlaceholderUsage } from "../types";
import { isTemplatedSessionName, sessionPlaceholderError } from "../utils/sessionPlaceholder";

interface AiSessionControlsProps {
  aiUseSession: boolean;
  aiSessionPlaceholder: string;
  aiForkFromPlaceholder: string;
  sessionPlaceholderUsages: SessionPlaceholderUsage[];
  selectedPlaceholderUsage?: SessionPlaceholderUsage;
  onAiUseSessionChange: (value: boolean) => void;
  onAiSessionPlaceholderChange: (value: string) => void;
  onAiForkFromPlaceholderChange: (value: string) => void;
}

type SessionMode = "none" | "continue" | "fork";

const MODES: { value: SessionMode; label: string; hint: string }[] = [
  { value: "none", label: "None", hint: "Each run starts a fresh, throwaway conversation." },
  {
    value: "continue",
    label: "Continue a session",
    hint: "Share one growing conversation across the nodes that name it.",
  },
  {
    value: "fork",
    label: "Fork a session",
    hint: "Branch off an existing conversation without changing the original.",
  },
];

const BINDING_HELP =
  "This is a design-time name. ILD binds it to the real adapter-generated session id for each run.";

const TEMPLATE_HELP =
  "Interpolate a loop variable — e.g. ticket_{{Var.current_ticket}} — to get one session per value instead of one per loop. The variable must be set before this node runs, or the node fails.";

function deriveMode(useSession: boolean, forkFrom: string): SessionMode {
  if (!useSession) return "none";
  return forkFrom.trim() ? "fork" : "continue";
}

/** Lane label: a templated name is shown verbatim, annotated so it does not read as one shared conversation. */
function laneLabel(entry: SessionPlaceholderUsage): string {
  if (entry.count === 0) return `${entry.name} (no node names this session)`;
  const nodes = `${entry.count} node${entry.count === 1 ? "" : "s"}`;
  return entry.templated ? `${entry.name} (${nodes}, one per value)` : `${entry.name} (${nodes})`;
}

export function AiSessionControls({
  aiUseSession,
  aiSessionPlaceholder,
  aiForkFromPlaceholder,
  sessionPlaceholderUsages,
  selectedPlaceholderUsage,
  onAiUseSessionChange,
  onAiSessionPlaceholderChange,
  onAiForkFromPlaceholderChange,
}: AiSessionControlsProps) {
  // The 3-way mode is a UI affordance over two stored fields (useSession +
  // forkFrom). We hold it locally so picking "Fork" sticks even before a source
  // is chosen; the parent keys this component per node so it re-seeds on switch.
  const [mode, setMode] = useState<SessionMode>(() =>
    deriveMode(aiUseSession, aiForkFromPlaceholder),
  );

  const handleModeChange = (next: SessionMode) => {
    setMode(next);
    if (next === "none") {
      onAiUseSessionChange(false);
      return;
    }
    onAiUseSessionChange(true);
    if (next === "continue") {
      // Continuing in place means there is no source to branch from.
      onAiForkFromPlaceholderChange("");
    }
  };

  const sessionName = aiSessionPlaceholder.trim();
  const forkSource = aiForkFromPlaceholder.trim();
  const hasLanes = sessionPlaceholderUsages.length > 0;
  const sessionNameError = sessionPlaceholderError("Session name", aiSessionPlaceholder);
  const forkSourceError = sessionPlaceholderError("Fork from", aiForkFromPlaceholder);

  // A fork source that names no known lane — a template, or a lane since
  // renamed — must still show as the selected value. A <select> with no
  // matching option renders blank, which reads as "nothing set" and invites
  // the author to overwrite a value they never meant to change.
  const forkOptions =
    forkSource && !sessionPlaceholderUsages.some((entry) => entry.name === forkSource)
      ? [
          ...sessionPlaceholderUsages,
          { name: forkSource, count: 0, templated: isTemplatedSessionName(forkSource) },
        ]
      : sessionPlaceholderUsages;

  return (
    <div className="config-field session-controls">
      <div className="session-mode-toggle" role="radiogroup" aria-label="Session memory mode">
        {MODES.map((option) => (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={mode === option.value}
            className={`session-mode-option ${mode === option.value ? "active" : ""}`}
            onClick={() => handleModeChange(option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
      <small className="config-help-text">
        {MODES.find((option) => option.value === mode)?.hint}
      </small>

      {mode === "continue" && (
        <div className="session-panel">
          <label htmlFor="ai-session-placeholder">Session name</label>
          <input
            id="ai-session-placeholder"
            type="text"
            value={aiSessionPlaceholder}
            onChange={(event) => onAiSessionPlaceholderChange(event.target.value)}
            placeholder="e.g. research"
            list="ai-session-placeholder-options"
            autoComplete="off"
          />
          {hasLanes && (
            <datalist id="ai-session-placeholder-options">
              {sessionPlaceholderUsages.map((entry) => (
                <option key={entry.name} value={entry.name} />
              ))}
            </datalist>
          )}
          <small className="config-help-text">
            Type a new name to start a session, or pick an existing one to join it. {BINDING_HELP}
          </small>
          <small className="config-help-text">{TEMPLATE_HELP}</small>
          {sessionNameError && (
            <small className="config-help-text config-help-text-strong">{sessionNameError}</small>
          )}
          {!sessionNameError && isTemplatedSessionName(aiSessionPlaceholder) && (
            <small className="config-help-text config-help-text-strong">
              This name expands per run — one session per distinct value.
            </small>
          )}
          {selectedPlaceholderUsage && (
            <small className="config-help-text config-help-text-strong">
              Shared with {selectedPlaceholderUsage.count - 1} other AI node
              {selectedPlaceholderUsage.count - 1 === 1 ? "" : "s"} in this template.
            </small>
          )}
        </div>
      )}

      {mode === "fork" && (
        <div className="session-panel">
          <div className="session-fork-flow">
            <div className="session-fork-step">
              <label htmlFor="ai-fork-from-placeholder">Fork from</label>
              <select
                id="ai-fork-from-placeholder"
                value={forkSource}
                onChange={(event) => onAiForkFromPlaceholderChange(event.target.value)}
                disabled={forkOptions.length === 0}
              >
                <option value="">Select a session…</option>
                {forkOptions.map((entry) => (
                  <option key={entry.name} value={entry.name}>
                    {laneLabel(entry)}
                  </option>
                ))}
              </select>
            </div>
            <span className="session-fork-arrow" aria-hidden="true">
              →
            </span>
            <div className="session-fork-step">
              <label htmlFor="ai-session-placeholder">Into new session</label>
              <input
                id="ai-session-placeholder"
                type="text"
                value={aiSessionPlaceholder}
                onChange={(event) => onAiSessionPlaceholderChange(event.target.value)}
                placeholder="e.g. variant-a"
                autoComplete="off"
              />
            </div>
          </div>

          {forkOptions.length === 0 && (
            <small className="config-help-text">
              No sessions to fork from yet. Add an AI node that continues a session first.
            </small>
          )}

          <small className="config-help-text">{TEMPLATE_HELP}</small>
          {(sessionNameError || forkSourceError) && (
            <small className="config-help-text config-help-text-strong">
              {sessionNameError ?? forkSourceError}
            </small>
          )}

          {hasLanes && forkSource && sessionName && (
            <div className="session-fork-summary">
              Starts from <strong>{forkSource}</strong>, writes into <strong>{sessionName}</strong>.{" "}
              <strong>{forkSource}</strong> stays unchanged and reusable.
            </div>
          )}

          <small className="config-help-text">{BINDING_HELP}</small>
        </div>
      )}
    </div>
  );
}
