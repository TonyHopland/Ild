import { useState } from "react";
import { useAuth } from "../hooks/useAuth";

export default function Settings() {
  const { user } = useAuth();
  const [backendUrl, setBackendUrl] = useState("http://localhost:5000");
  const [signalrEnabled, setSignalrEnabled] = useState(true);

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
            <label htmlFor="backendUrl">Backend URL</label>
            <input
              id="backendUrl"
              type="text"
              value={backendUrl}
              onChange={(e) => setBackendUrl(e.target.value)}
            />
          </div>
          <div className="form-group">
            <label>
              <input
                type="checkbox"
                checked={signalrEnabled}
                onChange={(e) => setSignalrEnabled(e.target.checked)}
              />{" "}
              Enable SignalR real-time updates
            </label>
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
          max-width: 600px;
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
