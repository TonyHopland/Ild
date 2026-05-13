import type { SessionPlaceholderUsage } from "../types";

interface AiSessionControlsProps {
  aiSessionPlaceholder: string;
  sessionPlaceholderUsages: SessionPlaceholderUsage[];
  selectedPlaceholderUsage?: SessionPlaceholderUsage;
  onAiSessionPlaceholderChange: (value: string) => void;
}

export function AiSessionControls({
  aiSessionPlaceholder,
  sessionPlaceholderUsages,
  selectedPlaceholderUsage,
  onAiSessionPlaceholderChange,
}: AiSessionControlsProps) {
  if (sessionPlaceholderUsages.length === 0) {
    return (
      <div className="config-field">
        <label htmlFor="ai-session-placeholder">Local Session Placeholder</label>
        <input
          id="ai-session-placeholder"
          type="text"
          value={aiSessionPlaceholder}
          onChange={(event) => onAiSessionPlaceholderChange(event.target.value)}
          placeholder="e.g. research"
        />
        <small className="config-help-text">
          This is a design-time name. ILD binds it to the real adapter-generated session id for each
          run.
        </small>
      </div>
    );
  }

  return (
    <>
      <div className="config-field">
        <label htmlFor="ai-session-placeholder-picker">Reuse Existing Placeholder</label>
        <select
          id="ai-session-placeholder-picker"
          value={aiSessionPlaceholder.trim()}
          onChange={(event) => onAiSessionPlaceholderChange(event.target.value)}
        >
          <option value="">Create or type a new placeholder</option>
          {sessionPlaceholderUsages.map((entry) => (
            <option key={entry.name} value={entry.name}>
              {entry.name} ({entry.count} node{entry.count === 1 ? "" : "s"})
            </option>
          ))}
        </select>
        <small className="config-help-text">
          Pick an existing placeholder to keep multiple AI nodes attached to the same conversation
          lane.
        </small>
      </div>

      <div className="config-field">
        <label htmlFor="ai-session-placeholder">Local Session Placeholder</label>
        <input
          id="ai-session-placeholder"
          type="text"
          value={aiSessionPlaceholder}
          onChange={(event) => onAiSessionPlaceholderChange(event.target.value)}
          placeholder="e.g. research"
          list="ai-session-placeholder-options"
        />
        <datalist id="ai-session-placeholder-options">
          {sessionPlaceholderUsages.map((entry) => (
            <option key={entry.name} value={entry.name} />
          ))}
        </datalist>
        <small className="config-help-text">
          This is a design-time name. ILD binds it to the real adapter-generated session id for each
          run.
        </small>
        {selectedPlaceholderUsage && (
          <small className="config-help-text config-help-text-strong">
            Reused by {selectedPlaceholderUsage.count} AI node
            {selectedPlaceholderUsage.count === 1 ? "" : "s"} in this template.
          </small>
        )}
      </div>

      <div className="config-field">
        <label>Placeholder Library</label>
        <div className="config-chip-list">
          {sessionPlaceholderUsages.map((entry) => {
            const isSelected = entry.name === aiSessionPlaceholder.trim();
            return (
              <button
                key={entry.name}
                type="button"
                className={`config-chip ${isSelected ? "active" : ""}`}
                onClick={() => onAiSessionPlaceholderChange(entry.name)}
              >
                {entry.name} ({entry.count})
              </button>
            );
          })}
        </div>
      </div>
    </>
  );
}
