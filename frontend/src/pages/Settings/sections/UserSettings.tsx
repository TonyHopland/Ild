import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../hooks/useAuth";
import { useChatEnabled, setChatEnabled } from "../../../hooks/useChatEnabled";
import { authService, SessionSettingKeys } from "../../../services/auth";
import { UserSession } from "../../../types";
import { NumericSettingField, SettingRow, Switch } from "../controls";

/**
 * A user agent is unreadable; the browser name is what tells one of your own
 * devices from another. The full string stays available as a tooltip for when
 * the guess is not enough.
 */
export function describeDevice(userAgent: string | null | undefined): string {
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

/** Who you are signed in as, on what, and the preferences that follow you around. */
export default function UserSettings() {
  const { user } = useAuth();
  const chatEnabled = useChatEnabled();
  const [notificationsEnabled, setNotificationsEnabled] = useState(true);
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

  const toggleNotifications = (val: boolean) => {
    setNotificationsEnabled(val);
    localStorage.setItem("ild_notifications_enabled", val ? "true" : "false");
    if (val && typeof Notification !== "undefined" && Notification.permission === "default") {
      void Notification.requestPermission();
    }
  };

  const otherDevices = sessions.filter((s) => !s.isCurrent).length;

  return (
    <>
      <div className="settings-pane-header">
        <h2>User</h2>
        <p>Your account, your devices, and the preferences kept in this browser.</p>
      </div>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Profile</h3>
        </div>
        <SettingRow label="Username">
          <span className="settings-row-label">{user?.username}</span>
        </SettingRow>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Preferences</h3>
        </div>
        <SettingRow
          label="Browser notifications"
          help="Notify this browser when a run needs you. Asks for permission the first time you turn it on."
        >
          <Switch
            checked={notificationsEnabled}
            onChange={toggleNotifications}
            label="Enable browser notifications"
          />
        </SettingRow>
        <SettingRow
          label="AI chat bubble"
          help="Shows the floating chat bubble in the lower corner. Turn it off to hide the bubble entirely so it cannot obstruct the view."
        >
          <Switch checked={chatEnabled} onChange={setChatEnabled} label="Enable AI chat bubble" />
        </SettingRow>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Signed-in devices</h3>
          <button
            type="button"
            className="btn"
            onClick={() => void revokeOtherSessions()}
            disabled={otherDevices === 0}
          >
            Sign out everywhere else
          </button>
        </div>
        {sessionsError && <div className="settings-error">{sessionsError}</div>}
        <ul className="settings-sessions">
          {sessions.map((session) => (
            <li key={session.id} className="settings-session">
              <div>
                <span className="settings-row-label" title={session.userAgent ?? undefined}>
                  {describeDevice(session.userAgent)}
                </span>
                {session.isCurrent && <span className="settings-badge">This device</span>}
                <div className="settings-row-help">
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
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Sign-in expiry</h3>
        </div>
        <p className="settings-card-note">
          Set either to <strong>0</strong> to disable that limit. The inactivity window applies to
          devices already signed in; the second only to sign-ins made after you change it.
        </p>
        <NumericSettingField
          settingKey={SessionSettingKeys.IdleDays}
          label="Sign out after inactivity"
          min={0}
          max={3650}
          fallback={30}
          minLabel="0 (never)"
          unit="days"
        />
        <NumericSettingField
          settingKey={SessionSettingKeys.MaxDays}
          label="Sign out after, however active"
          min={0}
          max={3650}
          fallback={90}
          minLabel="0 (never)"
          unit="days"
        />
      </section>

      <style>{`
        .settings-sessions { list-style: none; margin: 0; padding: 0; }
        .settings-session {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 0.5rem;
          padding: 0.6rem 0;
          border-top: 1px solid #2d2d44;
        }
        .settings-session:first-child { border-top: none; padding-top: 0; }
        .settings-session .settings-badge { margin-left: 0.5rem; }
      `}</style>
    </>
  );
}
