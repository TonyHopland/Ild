import { useState, useEffect } from "react";
import { useAuth } from "../../hooks/useAuth";
import { loggingService, settingsService, SchedulerSettingKeys } from "../../services/auth";

const LOG_LEVELS = ["Debug", "Information", "Warning", "Error"] as const;

export default function Settings() {
  const { user } = useAuth();
  const [notificationsEnabled, setNotificationsEnabled] = useState(true);
  const [logLevel, setLogLevel] = useState("Information");
  const [maxConcurrent, setMaxConcurrent] = useState<number>(5);
  const [maxConcurrentInput, setMaxConcurrentInput] = useState<string>("5");
  const [maxConcurrentError, setMaxConcurrentError] = useState<string | null>(null);
  const [savingMaxConcurrent, setSavingMaxConcurrent] = useState(false);

  useEffect(() => {
    const stored = localStorage.getItem("ild_notifications_enabled");
    if (stored !== null) {
      setNotificationsEnabled(stored !== "false");
    }
    void settingsService
      .get(SchedulerSettingKeys.MaxConcurrent)
      .then((s) => {
        const n = parseInt(s.value, 10);
        if (!Number.isNaN(n)) {
          setMaxConcurrent(n);
          setMaxConcurrentInput(String(n));
        }
      })
      .catch(() => {});
  }, []);

  const saveMaxConcurrent = async () => {
    const n = parseInt(maxConcurrentInput, 10);
    if (Number.isNaN(n) || n < 1 || n > 1000) {
      setMaxConcurrentError("Must be an integer between 1 and 1000.");
      return;
    }
    setMaxConcurrentError(null);
    setSavingMaxConcurrent(true);
    try {
      await settingsService.put(SchedulerSettingKeys.MaxConcurrent, String(n));
      setMaxConcurrent(n);
    } catch (err) {
      setMaxConcurrentError(err instanceof Error ? err.message : "Failed to save.");
    } finally {
      setSavingMaxConcurrent(false);
    }
  };

  const handleLogLevelChange = async (e: React.ChangeEvent<HTMLSelectElement>) => {
    const newLevel = e.target.value;
    let previousLevel = "";
    setLogLevel((prev) => {
      previousLevel = prev;
      return newLevel;
    });
    try {
      await loggingService.setLevel(newLevel);
    } catch {
      setLogLevel(previousLevel);
    }
  };

  return (
    <div className="page-container">
      <h1 className="page-title">Settings</h1>
      <div className="settings-layout">
        <div className="settings-section">
          <h2 className="settings-section-title">User Profile</h2>
          <div className="settings-profile">
            <div className="settings-profile-field">
              <span className="settings-label">Username</span>
              <span className="settings-value">{user?.username}</span>
            </div>
          </div>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">Connection</h2>
          <div className="form-group">
            <label>
              <input
                type="checkbox"
                checked={notificationsEnabled}
                onChange={(e) => {
                  const val = e.target.checked;
                  setNotificationsEnabled(val);
                  localStorage.setItem("ild_notifications_enabled", val ? "true" : "false");
                  if (
                    val &&
                    typeof Notification !== "undefined" &&
                    Notification.permission === "default"
                  ) {
                    void Notification.requestPermission();
                  }
                }}
              />{" "}
              Enable browser notifications
            </label>
          </div>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">Scheduler</h2>
          <div className="form-group">
            <label htmlFor="maxConcurrent">Max concurrent running work items</label>
            <div style={{ display: "flex", gap: "0.5rem", alignItems: "center" }}>
              <input
                id="maxConcurrent"
                type="number"
                min={1}
                max={1000}
                value={maxConcurrentInput}
                onChange={(e) => setMaxConcurrentInput(e.target.value)}
                style={{ width: "6rem" }}
              />
              <button
                type="button"
                className="btn btn-primary"
                onClick={saveMaxConcurrent}
                disabled={savingMaxConcurrent || maxConcurrentInput === String(maxConcurrent)}
              >
                Save
              </button>
            </div>
            {maxConcurrentError && (
              <div className="form-error" style={{ color: "#f87171", marginTop: "0.25rem" }}>
                {maxConcurrentError}
              </div>
            )}
            <p className="settings-about-desc" style={{ marginTop: "0.5rem" }}>
              Caps how many work items the scheduler will run at once. Per-provider parallelism is
              configured on each AI provider.
            </p>
          </div>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">Logging</h2>
          <div className="form-group">
            <label htmlFor="logLevel">Backend Log Level</label>
            <select id="logLevel" value={logLevel} onChange={handleLogLevelChange}>
              {LOG_LEVELS.map((level) => (
                <option key={level} value={level}>
                  {level}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">About</h2>
          <div className="settings-about">
            <p>ILD v0.1.0</p>
            <p className="settings-about-desc">
              Integrated Loop Dashboard — a tool for managing work items and automation loops.
            </p>
          </div>
        </div>
      </div>
      <style>{`
        .settings-layout {
          display: flex;
          flex-direction: column;
          gap: 1.5rem;
          max-width: min(680px, 100%);
        }

        .settings-section {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          padding: 1rem;
          border: 1px solid #2d2d44;
        }

        .settings-section-title {
          font-size: 0.875rem;
          font-weight: 600;
          color: #c0c0d0;
          margin-bottom: 0.75rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .settings-profile {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .settings-profile-field {
          display: flex;
          justify-content: space-between;
          padding: 0.5rem 0;
          border-bottom: 1px solid #2d2d44;
        }

        .settings-label {
          font-size: 0.8rem;
          color: #707090;
        }

        .settings-value {
          font-size: 0.8rem;
          color: #e0e0e0;
        }

        .settings-section .form-group {
          margin-bottom: 0.75rem;
        }

        .settings-section .form-group label {
          display: block;
          font-size: 0.75rem;
          color: #a0a0b0;
          margin-bottom: 0.25rem;
        }

        .settings-section .form-group input[type="text"] {
          width: 100%;
          padding: 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.875rem;
        }

        .settings-section .form-group select {
          width: 100%;
          padding: 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.875rem;
        }

        .settings-about p {
          font-size: 0.8rem;
          color: #a0a0b0;
        }

        .settings-about-desc {
          color: #707090;
          margin-top: 0.25rem;
        }
      `}</style>
    </div>
  );
}
