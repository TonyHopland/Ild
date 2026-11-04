import { useCallback, useEffect, useRef, useState } from "react";
import ConfirmModal from "../../components/ConfirmModal";
import Icon from "../../components/Icon";
import { managedAgentService } from "../../services/auth";
import type { ApiError, ManagedAgentStatus } from "../../types";

type Busy = "update" | "rollback";

const isInstalled = (agent: ManagedAgentStatus) => agent.installedVersion != null;

const formatDate = (iso: string) => new Date(iso).toLocaleDateString();

/** The lines under an agent's name saying where it stands; one of them is always shown. */
function statusLines(agent: ManagedAgentStatus): string[] {
  if (!isInstalled(agent)) return [];
  const lines: string[] = [];
  if (agent.heldVersion) {
    lines.push(
      `Held at ${agent.installedVersion} after rollback; skipping ${agent.heldVersion} until a newer version`,
    );
  }
  if (agent.autoInstallVersion && agent.autoInstallAfter) {
    lines.push(
      new Date(agent.autoInstallAfter).getTime() > Date.now()
        ? `${agent.autoInstallVersion} will install automatically after ${formatDate(agent.autoInstallAfter)}`
        : `${agent.autoInstallVersion} will install automatically at the next check`,
    );
  }
  // Without a latest version there is nothing to compare with, so nothing is claimed.
  if (lines.length === 0 && agent.latestVersion) {
    lines.push(agent.updateAvailable ? `${agent.latestVersion} is available` : "Up to date");
  }
  return lines;
}

/**
 * The coding agents on the persistent volume: what is installed, what happens to it next, and
 * the two things a person can do about it. Read when the menu opens; nothing is pushed live.
 */
export default function InstalledAgentsMenu() {
  const [open, setOpen] = useState(false);
  const [agents, setAgents] = useState<ManagedAgentStatus[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Keyed by agent key: each action owns its own entry, so several can run at once, and an
  // action started before the menu is closed is still shown busy when it is opened again.
  const [busy, setBusy] = useState<Record<string, Busy>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirming, setConfirming] = useState<ManagedAgentStatus | null>(null);
  // Only the newest read counts, and a read that began before an action on an agent finished
  // does not overwrite that agent's row, which already holds the action's result.
  const readSeq = useRef(0);
  const clock = useRef(0);
  const actionDoneAt = useRef<Record<string, number>>({});

  const load = useCallback(async () => {
    const seq = ++readSeq.current;
    const startedAt = ++clock.current;
    setLoadError(null);
    try {
      const result = await managedAgentService.getAll();
      if (seq !== readSeq.current) return;
      setAgents((prev) =>
        (result ?? []).map((fresh) =>
          (actionDoneAt.current[fresh.key] ?? 0) > startedAt
            ? (prev?.find((a) => a.key === fresh.key) ?? fresh)
            : fresh,
        ),
      );
    } catch (error) {
      console.error("Failed to load coding agents:", error);
      if (seq === readSeq.current) setLoadError("Could not load the installed agents.");
    }
  }, []);

  useEffect(() => {
    if (open) void load();
  }, [open, load]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !confirming) setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open, confirming]);

  const run = async (
    agent: ManagedAgentStatus,
    kind: Busy,
    action: (key: string) => Promise<ManagedAgentStatus>,
    failure: string,
  ) => {
    setBusy((prev) => ({ ...prev, [agent.key]: kind }));
    setErrors((prev) => {
      const next = { ...prev };
      delete next[agent.key];
      return next;
    });
    try {
      const updated = await action(agent.key);
      actionDoneAt.current[agent.key] = ++clock.current;
      setAgents((prev) => (prev ?? []).map((a) => (a.key === updated.key ? updated : a)));
    } catch (error) {
      setErrors((prev) => ({ ...prev, [agent.key]: (error as ApiError)?.message ?? failure }));
      // What the action was based on may be out of date; show what is there now.
      void load();
    } finally {
      setBusy((prev) => {
        const next = { ...prev };
        delete next[agent.key];
        return next;
      });
    }
  };

  const renderRow = (agent: ManagedAgentStatus) => {
    const working = busy[agent.key];
    const notInstalled = !isInstalled(agent);
    const canUpdate = agent.updateAvailable;
    const previous = agent.previousVersion ?? null;
    return (
      <div key={agent.key} className="ap-agent-row">
        <div className="ap-agent-id">
          <span className="ap-agent-name">{agent.displayName}</span>
          <span className="ap-agent-meta">
            {notInstalled ? "Not installed" : `v${agent.installedVersion}`}
          </span>
          {statusLines(agent).map((line) => (
            <span key={line} className="ap-agent-status">
              {line}
            </span>
          ))}
          {agent.error && <span className="ap-agent-warn">{agent.error}</span>}
          {errors[agent.key] && <span className="ap-agent-error">{errors[agent.key]}</span>}
        </div>
        <div className="ap-agent-actions">
          {(canUpdate || notInstalled || working === "update") && (
            <button
              className="btn btn-primary btn-small"
              disabled={!canUpdate || working !== undefined}
              aria-busy={working === "update"}
              onClick={() =>
                void run(agent, "update", managedAgentService.update, "Update failed.")
              }
              title={
                notInstalled
                  ? canUpdate
                    ? `Install ${agent.displayName} ${agent.latestVersion} onto /data`
                    : "Latest version unavailable — check registry connectivity"
                  : `Install ${agent.displayName} ${agent.latestVersion} now, without waiting for the cooldown`
              }
            >
              {working === "update"
                ? notInstalled
                  ? "Installing…"
                  : "Updating…"
                : notInstalled
                  ? canUpdate
                    ? `Install ${agent.latestVersion}`
                    : "Unavailable"
                  : `Update to ${agent.latestVersion}`}
            </button>
          )}
          {previous && (
            <button
              className="btn btn-secondary btn-small"
              disabled={working !== undefined}
              aria-busy={working === "rollback"}
              onClick={() => setConfirming(agent)}
              title={`Switch ${agent.displayName} back to ${previous}`}
            >
              {working === "rollback" ? "Rolling back…" : `Roll back to ${previous}`}
            </button>
          )}
        </div>
      </div>
    );
  };

  return (
    <div className="ap-agents-wrap">
      {/* The chevron says this opens a menu rather than doing something; it turns over while open. */}
      <button
        className="btn btn-secondary ap-agents-button"
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        Installed agents
        <Icon
          name="chevronDown"
          size={14}
          className={`ap-agents-chevron ${open ? "is-open" : ""}`}
        />
      </button>
      {open && (
        <>
          <div className="ap-popover-backdrop" onClick={() => setOpen(false)} />
          <div className="ap-popover" role="dialog" aria-label="Installed agents">
            <p className="ap-popover-note">
              Pi, OpenCode, Claude Code, GitHub Copilot and Codex live on the persistent{" "}
              <code>/data</code> volume and update themselves once a release is old enough.
            </p>
            {loadError && <p className="ap-agent-error ap-popover-note">{loadError}</p>}
            {agents === null && !loadError && <p className="ap-popover-note">Loading…</p>}
            <div className="ap-agent-list">{(agents ?? []).map(renderRow)}</div>
          </div>
        </>
      )}
      <ConfirmModal
        isOpen={confirming !== null}
        title={`Roll back ${confirming?.displayName ?? ""}?`}
        message={
          confirming
            ? `Switch ${confirming.displayName} back to ${confirming.previousVersion} now. This takes effect immediately, even while the agent is in use; runs already going keep the version they started with. Automatic updates will then skip ${confirming.installedVersion} and anything older, until a newer version is out or you update by hand.`
            : ""
        }
        confirmText="Roll back"
        onCancel={() => setConfirming(null)}
        onConfirm={() => {
          const agent = confirming;
          setConfirming(null);
          if (agent)
            void run(
              agent,
              "rollback",
              (key) => managedAgentService.rollback(key, agent.previousVersion ?? ""),
              "Rollback failed.",
            );
        }}
      />
      <style>{`
        .ap-agents-wrap {
          position: relative;
        }

        .ap-agents-button {
          display: inline-flex;
          align-items: center;
          gap: 0.4rem;
        }

        .ap-agents-chevron {
          transition: transform 0.15s ease;
        }

        .ap-agents-chevron.is-open {
          transform: rotate(180deg);
        }

        .ap-agent-list {
          display: flex;
          flex-direction: column;
        }

        .ap-agent-row {
          display: flex;
          justify-content: space-between;
          align-items: center;
          gap: 1rem;
          padding: 0.6rem 0.85rem;
        }

        .ap-agent-row + .ap-agent-row {
          border-top: 1px solid #2d2d44;
        }

        .ap-agent-id {
          display: flex;
          flex-direction: column;
          gap: 0.15rem;
          min-width: 0;
        }

        .ap-agent-actions {
          display: flex;
          flex-direction: column;
          align-items: stretch;
          gap: 0.35rem;
        }

        .ap-agent-name {
          font-size: 0.85rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .ap-agent-meta {
          font-size: 0.72rem;
          color: #707090;
        }

        .ap-agent-status {
          font-size: 0.72rem;
          color: #a0a0b8;
        }

        .ap-agent-warn {
          font-size: 0.7rem;
          color: #d9a441;
        }

        .ap-agent-error {
          font-size: 0.7rem;
          color: #ef4444;
        }

        .ap-popover-backdrop {
          position: fixed;
          inset: 0;
          z-index: 30;
        }

        .ap-popover {
          position: absolute;
          right: 0;
          top: calc(100% + 0.5rem);
          width: 380px;
          max-width: calc(100vw - 2rem);
          z-index: 40;
          background-color: #1e1e30;
          border: 1px solid #2d2d44;
          border-radius: 0.5rem;
          box-shadow: 0 10px 30px rgba(0, 0, 0, 0.5);
          overflow: hidden;
        }

        .ap-popover-note {
          font-size: 0.7rem;
          color: #707090;
          padding: 0.55rem 0.85rem;
          border-bottom: 1px solid #2d2d44;
          line-height: 1.4;
          margin: 0;
        }

        .ap-popover-note code {
          background-color: #15151e;
          padding: 0 0.25rem;
          border-radius: 0.25rem;
        }
      `}</style>
    </div>
  );
}
