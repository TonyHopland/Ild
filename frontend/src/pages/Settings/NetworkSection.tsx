import { useCallback, useEffect, useState } from "react";
import { useSignalR } from "../../hooks/useSignalR";
import {
  aiProviderService,
  networkService,
  NetworkSettingKeys,
  settingsService,
} from "../../services/auth";
import type {
  AiProvider,
  NetworkDecision,
  NetworkListKind,
  NetworkLogEntry,
  NetworkMode,
  NetworkPolicyEntry,
  NetworkStatus,
} from "../../types";

const MODES: { value: NetworkMode; label: string; help: string }[] = [
  { value: "off", label: "Off", help: "Every destination is logged; nothing is blocked." },
  { value: "whitelist", label: "Whitelist", help: "Only hosts on the whitelist are reachable." },
  { value: "blacklist", label: "Blacklist", help: "Hosts on the blacklist are unreachable." },
];

const LOG_TAKE = 200;

const DECISIONS: NetworkDecision[] = ["Allowed", "Blocked", "Advisory"];

/**
 * A log line arrives as a name from the REST API and must be one from the hub
 * too, but the hub has sent enums as their ordinal before; accept both so a
 * mismatch degrades to a readable row rather than a crash.
 */
function normalizeDecision(value: unknown): NetworkDecision {
  if (typeof value === "number") return DECISIONS[value] ?? "Advisory";
  const name = DECISIONS.find((d) => d.toLowerCase() === String(value).toLowerCase());
  return name ?? "Advisory";
}

function normalizeLogEntry(entry: NetworkLogEntry): NetworkLogEntry {
  return { ...entry, decision: normalizeDecision(entry.decision) };
}

function isMode(value: string): value is NetworkMode {
  return value === "off" || value === "whitelist" || value === "blacklist";
}

function describeError(err: unknown, fallback: string): string {
  if (err && typeof err === "object" && "message" in err) {
    const message = (err as { message?: unknown }).message;
    if (typeof message === "string" && message) return message;
  }
  return fallback;
}

interface ListEditorProps {
  kind: NetworkListKind;
  title: string;
  entries: NetworkPolicyEntry[];
  providers: AiProvider[];
  onAdd: (host: string, kind: NetworkListKind, aiProviderId: string | null) => Promise<void>;
  onRemove: (id: string) => Promise<void>;
}

/**
 * One list: its entries with their scope, and a row to add another. The draft
 * and its error are its own; the page owns the entries because both lists and
 * the log's promote buttons write the same collection.
 */
function ListEditor({ kind, title, entries, providers, onAdd, onRemove }: ListEditorProps) {
  const [host, setHost] = useState("");
  const [scope, setScope] = useState<string>("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const label = kind === "Whitelist" ? "whitelist" : "blacklist";

  const add = async () => {
    setBusy(true);
    setError(null);
    try {
      await onAdd(host, kind, scope || null);
      setHost("");
    } catch (err) {
      setError(describeError(err, `Failed to add to the ${label}.`));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="network-list">
      <h3 className="network-list-title">{title}</h3>
      {entries.length === 0 ? (
        <p className="settings-about-desc">No hosts on the {label}.</p>
      ) : (
        <ul className="network-entries" aria-label={`${title} entries`}>
          {entries.map((entry) => (
            <li key={entry.id} className="network-entry">
              <span className="network-entry-host">{entry.host}</span>
              <span className="settings-label">
                {entry.aiProviderId
                  ? (providers.find((p) => p.id === entry.aiProviderId)?.name ?? "one provider")
                  : "all providers"}
              </span>
              <button
                type="button"
                className="btn"
                onClick={() => void onRemove(entry.id)}
                aria-label={`Remove ${entry.host} from the ${label}`}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="network-add-row">
        <input
          type="text"
          value={host}
          onChange={(e) => setHost(e.target.value)}
          placeholder="api.example.com or .example.com"
          aria-label={`Host to add to the ${label}`}
          onKeyDown={(e) => {
            if (e.key === "Enter") void add();
          }}
        />
        <select
          value={scope}
          onChange={(e) => setScope(e.target.value)}
          aria-label={`Scope for the new ${label} entry`}
        >
          <option value="">All providers</option>
          {providers.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => void add()}
          disabled={busy || host.trim() === ""}
        >
          Add to {label}
        </button>
      </div>
      {error && (
        <div className="form-error" style={{ color: "#f87171", marginTop: "0.25rem" }}>
          {error}
        </div>
      )}
    </div>
  );
}

/**
 * The Network section of Settings: the mode toggle, the two lists, the live
 * destination log with its promote buttons, and the enforcement banner. The
 * lists and the log update in place from the hub, so a click here is judged by
 * the agent's next connection and shows up as such without a refresh.
 */
export default function NetworkSection() {
  const { on, off } = useSignalR();
  const [status, setStatus] = useState<NetworkStatus | null>(null);
  const [mode, setMode] = useState<NetworkMode>("off");
  const [modeError, setModeError] = useState<string | null>(null);
  const [entries, setEntries] = useState<NetworkPolicyEntry[]>([]);
  const [log, setLog] = useState<NetworkLogEntry[]>([]);
  const [providers, setProviders] = useState<AiProvider[]>([]);
  const [listError, setListError] = useState<string | null>(null);
  const [logError, setLogError] = useState<string | null>(null);

  const refreshEntries = useCallback(async () => {
    try {
      setEntries(await networkService.getEntries());
      setListError(null);
    } catch (err) {
      setListError(describeError(err, "Failed to load the network lists."));
    }
  }, []);

  const refreshLog = useCallback(async () => {
    try {
      setLog((await networkService.getLog(LOG_TAKE)).map(normalizeLogEntry));
      setLogError(null);
    } catch (err) {
      setLogError(describeError(err, "Failed to load the network log."));
    }
  }, []);

  useEffect(() => {
    void networkService
      .getStatus()
      .then(setStatus)
      .catch(() => {});
    void settingsService
      .get(NetworkSettingKeys.Mode)
      .then((s) => {
        if (isMode(s.value)) setMode(s.value);
      })
      .catch(() => {});
    void aiProviderService
      .getAll()
      .then(setProviders)
      .catch(() => {});
    void refreshEntries();
    void refreshLog();
  }, [refreshEntries, refreshLog]);

  useEffect(() => {
    const onPolicyChanged = () => {
      void refreshEntries();
      void settingsService
        .get(NetworkSettingKeys.Mode)
        .then((s) => {
          if (isMode(s.value)) setMode(s.value);
        })
        .catch(() => {});
    };
    const onLogAppended = (message: { payload: NetworkLogEntry }) => {
      const entry = normalizeLogEntry(message.payload);
      setLog((prev) => [entry, ...prev.filter((e) => e.id !== entry.id)].slice(0, LOG_TAKE));
    };
    const onLogCleared = () => setLog([]);

    on("NetworkPolicyChanged", onPolicyChanged);
    on("NetworkLogAppended", onLogAppended);
    on("NetworkLogCleared", onLogCleared);
    return () => {
      off("NetworkPolicyChanged", onPolicyChanged);
      off("NetworkLogAppended", onLogAppended);
      off("NetworkLogCleared", onLogCleared);
    };
  }, [on, off, refreshEntries]);

  const changeMode = async (next: string) => {
    if (!isMode(next)) return;
    const previous = mode;
    setMode(next);
    setModeError(null);
    try {
      await settingsService.put(NetworkSettingKeys.Mode, next);
    } catch (err) {
      setMode(previous);
      setModeError(describeError(err, "Failed to change the mode."));
    }
  };

  const addEntry = async (host: string, kind: NetworkListKind, aiProviderId: string | null) => {
    const created = await networkService.addEntry({ host, listKind: kind, aiProviderId });
    setEntries((prev) => [...prev.filter((e) => e.id !== created.id), created]);
  };

  const removeEntry = async (id: string) => {
    try {
      await networkService.deleteEntry(id);
      setEntries((prev) => prev.filter((e) => e.id !== id));
    } catch (err) {
      setListError(describeError(err, "Failed to remove the entry."));
    }
  };

  const promote = async (entry: NetworkLogEntry, kind: NetworkListKind) => {
    try {
      const created = await networkService.addLogEntryToList(entry.id, kind);
      setEntries((prev) => [...prev.filter((e) => e.id !== created.id), created]);
    } catch (err) {
      setListError(describeError(err, "Failed to add the host to the list."));
    }
  };

  const clearLog = async () => {
    try {
      await networkService.clearLog();
      setLog([]);
    } catch (err) {
      setLogError(describeError(err, "Failed to clear the log."));
    }
  };

  const whitelist = entries.filter((e) => e.listKind === "Whitelist");
  const blacklist = entries.filter((e) => e.listKind === "Blacklist");

  return (
    <div className="settings-section">
      <h2 className="settings-section-title">Network</h2>
      {status && status.enforcement !== "enforced" && (
        <div className="network-banner" role="status">
          <strong>Advisory mode — agent network limits are not enforced.</strong> {status.reason}
        </div>
      )}
      {listError && (
        <div className="form-error" style={{ color: "#f87171", marginBottom: "0.5rem" }}>
          {listError}
        </div>
      )}

      <div className="form-group">
        <label htmlFor="network-mode">Filter mode</label>
        <select id="network-mode" value={mode} onChange={(e) => void changeMode(e.target.value)}>
          {MODES.map((m) => (
            <option key={m.value} value={m.value}>
              {m.label}
            </option>
          ))}
        </select>
        <p className="settings-about-desc" style={{ marginTop: "0.5rem" }}>
          {MODES.find((m) => m.value === mode)?.help} A change applies to the agent&apos;s next
          connection; nothing needs a restart. Hosts match exactly, or every subdomain when written
          with a leading dot (<code>.example.com</code>).
        </p>
        {modeError && (
          <div className="form-error" style={{ color: "#f87171", marginTop: "0.25rem" }}>
            {modeError}
          </div>
        )}
      </div>

      <ListEditor
        kind="Whitelist"
        title="Whitelist"
        entries={whitelist}
        providers={providers}
        onAdd={addEntry}
        onRemove={removeEntry}
      />
      <ListEditor
        kind="Blacklist"
        title="Blacklist"
        entries={blacklist}
        providers={providers}
        onAdd={addEntry}
        onRemove={removeEntry}
      />

      <div className="network-log">
        <div className="network-log-header">
          <h3 className="network-list-title">Network log</h3>
          <button
            type="button"
            className="btn"
            onClick={() => void clearLog()}
            disabled={log.length === 0}
          >
            Clear log
          </button>
        </div>
        {logError && (
          <div className="form-error" style={{ color: "#f87171", marginBottom: "0.5rem" }}>
            {logError}
          </div>
        )}
        {log.length === 0 ? (
          <p className="settings-about-desc">No destinations recorded yet.</p>
        ) : (
          <table className="network-log-table">
            <thead>
              <tr>
                <th>When</th>
                <th>Host</th>
                <th>Port</th>
                <th>Decision</th>
                <th>Provider</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {log.map((entry) => (
                <tr key={entry.id}>
                  <td>{new Date(entry.timestamp).toLocaleString()}</td>
                  <td className="network-entry-host">{entry.host}</td>
                  <td>{entry.port}</td>
                  <td
                    className={`network-decision network-decision-${entry.decision.toLowerCase()}`}
                  >
                    {entry.decision}
                  </td>
                  <td>
                    {entry.aiProviderId
                      ? (providers.find((p) => p.id === entry.aiProviderId)?.name ?? "—")
                      : "—"}
                  </td>
                  <td className="network-log-actions">
                    <button
                      type="button"
                      className="btn"
                      onClick={() => void promote(entry, "Whitelist")}
                      aria-label={`Add ${entry.host} to whitelist`}
                    >
                      Add to whitelist
                    </button>
                    <button
                      type="button"
                      className="btn"
                      onClick={() => void promote(entry, "Blacklist")}
                      aria-label={`Add ${entry.host} to blacklist`}
                    >
                      Add to blacklist
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <style>{`
        .network-banner {
          background-color: #3b2f12;
          border: 1px solid #a16207;
          border-radius: 0.375rem;
          color: #fde68a;
          font-size: 0.8rem;
          padding: 0.5rem 0.75rem;
          margin-bottom: 0.75rem;
        }
        .network-list { margin-top: 1rem; }
        .network-list-title {
          font-size: 0.8rem;
          font-weight: 600;
          color: #a0a0b0;
          margin-bottom: 0.5rem;
        }
        .network-entries { list-style: none; margin: 0 0 0.5rem 0; padding: 0; }
        .network-entry {
          display: flex;
          align-items: center;
          gap: 0.75rem;
          padding: 0.35rem 0;
          border-bottom: 1px solid #2d2d44;
        }
        .network-entry-host { font-family: monospace; font-size: 0.8rem; color: #e0e0e0; flex: 1; }
        .network-add-row { display: flex; gap: 0.5rem; align-items: center; }
        .network-add-row input[type="text"], .network-add-row select {
          padding: 0.4rem 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.8rem;
        }
        .network-add-row input[type="text"] { flex: 1; }
        .network-log { margin-top: 1.25rem; }
        .network-log-header { display: flex; justify-content: space-between; align-items: center; }
        .network-log-table { width: 100%; border-collapse: collapse; font-size: 0.75rem; }
        .network-log-table th { text-align: left; color: #707090; padding: 0.25rem 0.4rem; }
        .network-log-table td { padding: 0.3rem 0.4rem; border-top: 1px solid #2d2d44; color: #c0c0d0; }
        .network-log-actions { display: flex; gap: 0.35rem; justify-content: flex-end; }
        .network-decision-blocked { color: #f87171; }
        .network-decision-allowed { color: #4ade80; }
        .network-decision-advisory { color: #a0a0b0; }
      `}</style>
    </div>
  );
}
