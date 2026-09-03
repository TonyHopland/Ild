import { useState, useEffect, useCallback } from "react";
import { useAuth } from "../../hooks/useAuth";
import { useChatEnabled, setChatEnabled } from "../../hooks/useChatEnabled";
import {
  authService,
  loggingService,
  settingsService,
  SchedulerSettingKeys,
  SessionSettingKeys,
} from "../../services/auth";
import { UserSession } from "../../types";
import NetworkSection from "./NetworkSection";

const LOG_LEVELS = ["Debug", "Information", "Warning", "Error"] as const;

/**
 * A user agent is unreadable; the browser name is what tells one of your own
 * devices from another. The full string stays available as a tooltip for when
 * the guess is not enough.
 */
function describeDevice(userAgent: string | null | undefined): string {
  if (!userAgent) return "Unknown device";
  const browser = [
    ["Edg/", "Edge"],
    ["OPR/", "Opera"],
    ["Firefox/", "Firefox"],
    ["Chrome/", "Chrome"],
    ["Safari/", "Safari"],
  ].find(([token]) => userAgent.includes(token))?.[1];
  const platform = [
    ["Android", "Android"],
    ["iPhone", "iPhone"],
    ["iPad", "iPad"],
    ["Mac OS X", "macOS"],
    ["Windows", "Windows"],
    ["Linux", "Linux"],
  ].find(([token]) => userAgent.includes(token))?.[1];

  if (browser && platform) return `${browser} on ${platform}`;
  return browser ?? platform ?? userAgent.slice(0, 40);
}

interface NumericSettingFieldProps {
  /** The app-setting key this field reads and writes. Also the input's id. */
  settingKey: string;
  label: string;
  min: number;
  max: number;
  /** Shown while the current value is still being fetched, or if it cannot be. */
  fallback: number;
  /** Replaces `min` in the range message, e.g. `"0 (disabled)"`. */
  minLabel?: string;
  /** Help text under the field. */
  children?: React.ReactNode;
}

/**
 * One integer app setting: shows its current value, refuses one outside the
 * allowed range before going near the server, and reports whatever the save
 * said. The draft, the error and the in-flight flag are its own because nothing
 * outside the field reads them — the page renders several of these and holds no
 * state for any of them.
 */
function NumericSettingField({
  settingKey,
  label,
  min,
  max,
  fallback,
  minLabel,
  children,
}: NumericSettingFieldProps) {
  const [saved, setSaved] = useState<number>(fallback);
  const [draft, setDraft] = useState<string>(String(fallback));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void settingsService
      .get(settingKey)
      .then((s) => {
        const n = parseInt(s.value, 10);
        if (Number.isNaN(n)) return;
        setSaved(n);
        setDraft(String(n));
      })
      // Unreachable API: leave the default showing rather than an empty box.
      .catch(() => {});
  }, [settingKey]);

  const save = async () => {
    const n = parseInt(draft, 10);
    if (Number.isNaN(n) || n < min || n > max) {
      setError(`Must be an integer between ${minLabel ?? min} and ${max}.`);
      return;
    }
    setError(null);
    setSaving(true);
    try {
      await settingsService.put(settingKey, String(n));
      setSaved(n);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="form-group">
      <label htmlFor={settingKey}>{label}</label>
      <div style={{ display: "flex", gap: "0.5rem", alignItems: "center" }}>
        <input
          id={settingKey}
          type="number"
          min={min}
          max={max}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          style={{ width: "6rem" }}
        />
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => void save()}
          disabled={saving || draft === String(saved)}
        >
          Save
        </button>
      </div>
      {error && (
        <div className="form-error" style={{ color: "#f87171", marginTop: "0.25rem" }}>
          {error}
        </div>
      )}
      {children && (
        <p className="settings-about-desc" style={{ marginTop: "0.5rem" }}>
          {children}
        </p>
      )}
    </div>
  );
}

interface ToggleSettingFieldProps {
  /** The app-setting key this checkbox reads and writes. Also the input's id. */
  settingKey: string;
  label: string;
  /** Help text under the checkbox. */
  children?: React.ReactNode;
}

/**
 * One boolean app setting, saved the moment it is ticked — there is nothing to
 * validate and nothing to draft, so a Save button would only be a second step
 * between the user and the one bit they came to change. A save that fails puts
 * the box back where it was and says why, rather than leaving the UI claiming a
 * setting the server never took.
 */
function ToggleSettingField({ settingKey, label, children }: ToggleSettingFieldProps) {
  const [checked, setChecked] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void settingsService
      .get(settingKey)
      // Case-insensitive because the API validates with bool.TryParse: a value
      // written as "True" is on to every backend reader, and a box showing it
      // off would be the only thing in the system that disagrees.
      .then((s) => setChecked(s.value.toLowerCase() === "true"))
      // Unreachable API: leave it showing off rather than a value we invented.
      .catch(() => {});
  }, [settingKey]);

  const save = async (next: boolean) => {
    setChecked(next);
    setError(null);
    try {
      await settingsService.put(settingKey, next ? "true" : "false");
    } catch (err) {
      setChecked(!next);
      setError(err instanceof Error ? err.message : "Failed to save.");
    }
  };

  return (
    <div className="form-group">
      <label>
        <input
          id={settingKey}
          type="checkbox"
          checked={checked}
          onChange={(e) => void save(e.target.checked)}
        />{" "}
        {label}
      </label>
      {error && (
        <div className="form-error" style={{ color: "#f87171", marginTop: "0.25rem" }}>
          {error}
        </div>
      )}
      {children && (
        <p className="settings-about-desc" style={{ marginTop: "0.5rem" }}>
          {children}
        </p>
      )}
    </div>
  );
}

export default function Settings() {
  const { user } = useAuth();
  const chatEnabled = useChatEnabled();
  const [notificationsEnabled, setNotificationsEnabled] = useState(true);
  const [logLevel, setLogLevel] = useState("Information");
  const [sessions, setSessions] = useState<UserSession[]>([]);
  const [sessionsError, setSessionsError] = useState<string | null>(null);

  const refreshSessions = useCallback(async () => {
    try {
      setSessions(await authService.getSessions());
      setSessionsError(null);
    } catch (err) {
      setSessionsError(err instanceof Error ? err.message : "Failed to load sessions.");
    }
  }, []);

  useEffect(() => {
    void refreshSessions();
  }, [refreshSessions]);

  useEffect(() => {
    const stored = localStorage.getItem("ild_notifications_enabled");
    if (stored !== null) {
      setNotificationsEnabled(stored !== "false");
    }
  }, []);

  const revokeSession = async (id: string) => {
    try {
      await authService.revokeSession(id);
      await refreshSessions();
    } catch (err) {
      setSessionsError(err instanceof Error ? err.message : "Failed to sign that device out.");
    }
  };

  const revokeOtherSessions = async () => {
    try {
      await authService.revokeOtherSessions();
      await refreshSessions();
    } catch (err) {
      setSessionsError(
        err instanceof Error ? err.message : "Failed to sign the other devices out.",
      );
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
          <h2 className="settings-section-title">Signed-in devices</h2>
          {sessionsError && (
            <div className="form-error" style={{ color: "#f87171", marginBottom: "0.5rem" }}>
              {sessionsError}
            </div>
          )}
          <ul className="settings-sessions">
            {sessions.map((session) => (
              <li key={session.id} className="settings-session">
                <div>
                  <span className="settings-value" title={session.userAgent ?? undefined}>
                    {describeDevice(session.userAgent)}
                  </span>
                  {session.isCurrent && (
                    <span className="settings-session-current">This device</span>
                  )}
                  <div className="settings-label">
                    Last active {new Date(session.lastSeenAt).toLocaleString()}
                    {session.createdFromIp ? ` · ${session.createdFromIp}` : ""}
                  </div>
                </div>
                {!session.isCurrent && (
                  <button
                    type="button"
                    className="btn"
                    onClick={() => void revokeSession(session.id)}
                    aria-label={`Sign out ${describeDevice(session.userAgent)}`}
                  >
                    Sign out
                  </button>
                )}
              </li>
            ))}
          </ul>
          <button
            type="button"
            className="btn"
            onClick={() => void revokeOtherSessions()}
            disabled={sessions.filter((s) => !s.isCurrent).length === 0}
          >
            Sign out everywhere else
          </button>
          <div style={{ marginTop: "1rem" }}>
            <NumericSettingField
              settingKey={SessionSettingKeys.IdleDays}
              label="Sign out after inactivity (days)"
              min={0}
              max={3650}
              fallback={30}
              minLabel="0 (never)"
            />
            <NumericSettingField
              settingKey={SessionSettingKeys.MaxDays}
              label="Sign out after (days), however active"
              min={0}
              max={3650}
              fallback={90}
              minLabel="0 (never)"
            >
              Set either to <strong>0</strong> to disable that limit. The inactivity window applies
              to devices already signed in; the second only to sign-ins made after you change it.
            </NumericSettingField>
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
          <h2 className="settings-section-title">Chat</h2>
          <div className="form-group">
            <label>
              <input
                type="checkbox"
                checked={chatEnabled}
                onChange={(e) => setChatEnabled(e.target.checked)}
              />{" "}
              Enable AI chat bubble
            </label>
            <p className="settings-about-desc" style={{ marginTop: "0.5rem" }}>
              Shows the floating chat bubble in the lower corner. Disable it to hide the bubble
              entirely so it cannot obstruct the view.
            </p>
          </div>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">Scheduler</h2>
          <NumericSettingField
            settingKey={SchedulerSettingKeys.MaxConcurrent}
            label="Max concurrent running work items"
            min={1}
            max={1000}
            fallback={5}
          >
            Caps how many work items the scheduler will run at once. Per-provider parallelism is
            configured on each AI provider.
          </NumericSettingField>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">Run retention</h2>
          <NumericSettingField
            settingKey={SchedulerSettingKeys.RunRetentionDays}
            label="Delete finished runs after (days)"
            min={0}
            max={3650}
            fallback={30}
            minLabel="0 (disabled)"
          >
            How long a finished run's worktree, branch, and history are kept before being reclaimed.
            Set to <strong>0</strong> to keep runs forever. Runs pinned with “Retain” are never
            deleted.
          </NumericSettingField>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">PR heartbeat</h2>
          <NumericSettingField
            settingKey={SchedulerSettingKeys.PrHeartbeatSeconds}
            label="Poll PR state every (seconds)"
            min={5}
            max={3600}
            fallback={60}
          >
            How often the background poller fetches PR state (CI, reviews, merge status) for runs
            parked at a PR node awaiting merge. Lower values react faster but use more provider API
            calls.
          </NumericSettingField>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">AI steps</h2>
          <NumericSettingField
            settingKey={SchedulerSettingKeys.MaxAiTraversals}
            label="Max AI steps between human interactions"
            min={1}
            max={1000}
            fallback={25}
          >
            How many AI nodes a run may execute before it stops and asks you whether to carry on.
            The count resets every time you interact with the run, so a back-and-forth between you
            and the AI never reaches it — only a loop running on its own does. At the cap the run
            waits in Human Feedback, where you can continue it, continue with guidance, or abandon
            it.
          </NumericSettingField>
        </div>

        <div className="settings-section">
          <h2 className="settings-section-title">Provider interruptions</h2>
          <ToggleSettingField
            settingKey={SchedulerSettingKeys.ThrottleAutoResume}
            label="Resume throttled runs automatically"
          >
            When the AI provider stops a step — a usage or session limit, a busy provider — the run
            waits in Human Feedback for you to click <strong>Resume</strong>. Turn this on to have
            ILD try the resume for you instead, waiting longer before each further attempt. After a
            few tries it stops and leaves the run for you — resuming it yourself gives it a fresh
            set of tries, and you can do that at any point.
          </ToggleSettingField>
        </div>

        <NetworkSection />

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

        .settings-sessions {
          list-style: none;
          margin: 0 0 0.75rem 0;
          padding: 0;
        }

        .settings-session {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 0.5rem;
          padding: 0.5rem 0;
          border-bottom: 1px solid #2d2d44;
        }

        .settings-session-current {
          margin-left: 0.5rem;
          padding: 0.05rem 0.35rem;
          border-radius: 0.25rem;
          background-color: #2d2d44;
          font-size: 0.7rem;
          color: #a0a0b0;
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
