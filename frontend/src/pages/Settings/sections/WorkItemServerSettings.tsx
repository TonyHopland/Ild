import { useEffect, useState } from "react";
import { workItemServerService } from "../../../services/auth";
import { SettingRow } from "../controls";

/** The one app-wide WorkItem Server connection the poller claims work from. */
export default function WorkItemServerSettings() {
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

  return (
    <>
      <div className="settings-pane-header">
        <h2>WorkItem Server</h2>
        <p>
          The standalone WorkItem server that ILD polls for work items. This is a single app-wide
          connection shared across all remote providers.
        </p>
      </div>

      {isLoading ? (
        <p className="settings-card-note">Loading...</p>
      ) : (
        <section className="settings-card">
          <div className="settings-card-header">
            <h3 className="settings-card-title">Connection</h3>
            <button
              type="button"
              className="btn btn-primary"
              onClick={() => void handleSave()}
              disabled={isSaving}
            >
              {isSaving ? "Saving..." : "Save"}
            </button>
          </div>
          {error && <div className="settings-error">{error}</div>}
          {savedMessage && <div className="settings-success">{savedMessage}</div>}
          <SettingRow label="WorkItem Server URL" htmlFor="wiUrl">
            <input
              id="wiUrl"
              type="text"
              className="settings-input"
              placeholder="http://localhost:5180"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
            />
          </SettingRow>
          <SettingRow label="WorkItem API Key" htmlFor="wiKey">
            <input
              id="wiKey"
              type="password"
              className="settings-input"
              placeholder={hasApiKey ? "(unchanged)" : ""}
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
            />
          </SettingRow>
          <SettingRow label="Poll Interval (seconds)" htmlFor="wiPoll">
            <input
              id="wiPoll"
              type="number"
              className="settings-input"
              min={1}
              max={86400}
              value={pollIntervalSeconds}
              onChange={(e) => setPollIntervalSeconds(Number(e.target.value))}
              style={{ width: "5rem" }}
            />
          </SettingRow>
          <SettingRow label="Grace Interval (seconds)" htmlFor="wiGrace">
            <input
              id="wiGrace"
              type="number"
              className="settings-input"
              min={1}
              max={3600}
              value={graceIntervalSeconds}
              onChange={(e) => setGraceIntervalSeconds(Number(e.target.value))}
              style={{ width: "5rem" }}
            />
          </SettingRow>
        </section>
      )}
    </>
  );
}
