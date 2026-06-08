import { useState, useEffect } from "react";
import { workItemServerService } from "../../services/auth";

export default function WorkItemServer() {
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedMessage, setSavedMessage] = useState<string | null>(null);

  const [url, setUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [hasApiKey, setHasApiKey] = useState(false);
  const [pollIntervalSeconds, setPollIntervalSeconds] = useState(60);
  const [graceIntervalSeconds, setGraceIntervalSeconds] = useState(5);

  useEffect(() => {
    void loadConfig();
  }, []);

  const loadConfig = async () => {
    try {
      const config = await workItemServerService.get();
      setUrl(config.url ?? "");
      setHasApiKey(Boolean(config.hasApiKey));
      setPollIntervalSeconds(config.pollIntervalSeconds ?? 60);
      setGraceIntervalSeconds(config.graceIntervalSeconds ?? 5);
    } catch (err) {
      console.error("Failed to load WorkItem server config:", err);
    } finally {
      setIsLoading(false);
    }
  };

  const handleSave = async () => {
    setError(null);
    setSavedMessage(null);
    setIsSaving(true);
    try {
      const updated = await workItemServerService.update({
        url: url || null,
        apiKey: apiKey || null,
        pollIntervalSeconds,
        graceIntervalSeconds,
      });
      setUrl(updated.url ?? "");
      setHasApiKey(Boolean(updated.hasApiKey));
      setPollIntervalSeconds(updated.pollIntervalSeconds ?? 60);
      setGraceIntervalSeconds(updated.graceIntervalSeconds ?? 5);
      setApiKey("");
      setSavedMessage("Settings saved.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save settings.");
    } finally {
      setIsSaving(false);
    }
  };

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading...</p>
      </div>
    );
  }

  return (
    <div className="page-container">
      <div className="rp-header">
        <h1 className="page-title">WorkItem Server</h1>
      </div>

      <p className="settings-description">
        Configure the standalone WorkItem server that ILD polls for work items. This is a single
        app-wide connection shared across all remote providers.
      </p>

      <div className="form-group">
        <label htmlFor="wiUrl">WorkItem Server URL</label>
        <input
          id="wiUrl"
          type="text"
          placeholder="http://localhost:5180"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
        />
      </div>
      <div className="form-group">
        <label htmlFor="wiKey">WorkItem API Key</label>
        <input
          id="wiKey"
          type="password"
          placeholder={hasApiKey ? "(unchanged)" : ""}
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
        />
      </div>
      <div className="form-group">
        <label htmlFor="wiPoll">Poll Interval (seconds)</label>
        <input
          id="wiPoll"
          type="number"
          min={1}
          max={86400}
          value={pollIntervalSeconds}
          onChange={(e) => setPollIntervalSeconds(Number(e.target.value))}
        />
      </div>
      <div className="form-group">
        <label htmlFor="wiGrace">Grace Interval (seconds)</label>
        <input
          id="wiGrace"
          type="number"
          min={1}
          max={3600}
          value={graceIntervalSeconds}
          onChange={(e) => setGraceIntervalSeconds(Number(e.target.value))}
        />
      </div>

      {error && <p className="form-error">{error}</p>}
      {savedMessage && <p className="form-success">{savedMessage}</p>}

      <div className="modal-footer">
        <button className="btn btn-primary" onClick={handleSave} disabled={isSaving}>
          {isSaving ? "Saving..." : "Save"}
        </button>
      </div>
    </div>
  );
}
