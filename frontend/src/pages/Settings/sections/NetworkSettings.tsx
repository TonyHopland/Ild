import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSharedWorkItemHub } from "../../../hooks/useWorkItemHub";
import {
  aiProviderService,
  networkService,
  NetworkSettingKeys,
  settingsService,
} from "../../../services/auth";
import type {
  AiProvider,
  NetworkDecision,
  NetworkForward,
  NetworkListKind,
  NetworkLogEntry,
  NetworkMode,
  NetworkPolicyEntry,
  NetworkStatus,
} from "../../../types";
import ConfirmModal from "../../../components/ConfirmModal";
import { NumericSettingField, Segmented, SettingRow, Switch } from "../controls";
import { AddForwardRow, ForwardTable } from "./NetworkForwards";
import { AddRuleRow, RuleTable } from "./NetworkRules";
import { describeError } from "./networkShared";

const MODES: { value: NetworkMode; label: string; help: string }[] = [
  {
    value: "off",
    label: "Off",
    help: "Agents connect directly to any host and port, with no proxy. Nothing is logged and neither list is in effect.",
  },
  {
    value: "monitor",
    label: "Monitor",
    help: "Every destination is logged and nothing is blocked. Neither list is in effect.",
  },
  {
    value: "whitelist",
    label: "Whitelist",
    help: "Only hosts on the allow list are reachable; everything else is refused.",
  },
  {
    value: "blacklist",
    label: "Blacklist",
    help: "Hosts on the block list are refused; everything else is reachable.",
  },
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

/** The stored value is kept as sent, so it is read the way the backend reads it: trimmed, any case. */
function parseMode(value: string): NetworkMode | null {
  const name = value.trim().toLowerCase();
  return MODES.find((m) => m.value === name)?.value ?? null;
}

function coversHost(pattern: string, host: string): boolean {
  if (pattern.startsWith(".")) return host === pattern.slice(1) || host.endsWith(pattern);
  return host === pattern;
}

/**
 * One log row after collapsing a run of identical destinations: the same host,
 * port, decision and provider seen several times in a row is one attempt from
 * where you sit, so it reads as one line with a count and the span it covers.
 */
interface LogGroup {
  key: string;
  entry: NetworkLogEntry;
  count: number;
  firstAt: string;
  lastAt: string;
}

export function groupConsecutive(entries: NetworkLogEntry[]): LogGroup[] {
  const groups: LogGroup[] = [];
  for (const entry of entries) {
    const signature = `${entry.host}:${entry.port}:${entry.decision}:${entry.aiProviderId ?? ""}`;
    const last = groups[groups.length - 1];
    if (last && last.key === signature) {
      last.count += 1;
      last.firstAt = entry.timestamp;
      continue;
    }
    groups.push({
      key: signature,
      entry,
      count: 1,
      firstAt: entry.timestamp,
      lastAt: entry.timestamp,
    });
  }
  return groups;
}

/**
 * A list that is read from the server and edited row by row. A read that
 * started before an add, save or removal settled must not undo it, so only the
 * latest read is applied; a change that dropped a read in flight reads again
 * rather than leave the list without what the dropped read had found.
 */
function useGuardedList<T extends { id: string }>(
  read: () => Promise<T[]>,
  setItems: React.Dispatch<React.SetStateAction<T[]>>,
  setError: (message: string | null) => void,
  failure: string,
) {
  const seq = useRef(0);
  const readPending = useRef(false);
  const refresh = useCallback(async () => {
    const mine = ++seq.current;
    readPending.current = true;
    try {
      const loaded = await read();
      if (mine !== seq.current) return;
      readPending.current = false;
      setError(null);
      setItems(loaded);
    } catch (err) {
      if (mine !== seq.current) return;
      readPending.current = false;
      setError(describeError(err, failure));
    }
  }, [read, setItems, setError, failure]);

  const change = (next: (prev: T[]) => T[]) => {
    const readInFlight = readPending.current;
    ++seq.current;
    setItems(next);
    if (readInFlight) void refresh();
  };

  return {
    refresh,
    add: (created: T) =>
      change((prev) => [...prev.filter((item) => item.id !== created.id), created]),
    replace: (updated: T) =>
      change((prev) => prev.map((item) => (item.id === updated.id ? updated : item))),
    remove: (id: string) => change((prev) => prev.filter((item) => item.id !== id)),
  };
}

/**
 * The Network page: the mode, one table of rules for both lists, the forwards
 * that carry traffic no proxy can read, and the live destination log the rules
 * are usually written from. Everything updates in place from the hub, so a click
 * here is judged by the next connection and shows up as such without a refresh.
 */
export default function NetworkSettings() {
  const { on, off } = useSharedWorkItemHub();
  const [status, setStatus] = useState<NetworkStatus | null>(null);
  const [mode, setMode] = useState<NetworkMode | null>(null);
  const [confirmingOff, setConfirmingOff] = useState(false);
  const [modeError, setModeError] = useState<string | null>(null);
  const [entries, setEntries] = useState<NetworkPolicyEntry[]>([]);
  const [forwards, setForwards] = useState<NetworkForward[]>([]);
  const [log, setLog] = useState<NetworkLogEntry[]>([]);
  const [providers, setProviders] = useState<AiProvider[]>([]);
  const [listError, setListError] = useState<string | null>(null);
  const [forwardError, setForwardError] = useState<string | null>(null);
  const [logError, setLogError] = useState<string | null>(null);
  const [ruleFilter, setRuleFilter] = useState("");
  const [kindFilter, setKindFilter] = useState<"All" | NetworkListKind>("All");
  const [logFilter, setLogFilter] = useState("");
  const [decisionFilter, setDecisionFilter] = useState<"All" | NetworkDecision>("All");
  const [grouped, setGrouped] = useState(true);
  /**
   * The other way onto a list from the log: a host loaded into the add form
   * rather than committed, for when the rule wants widening to `.example.com`
   * or narrowing to one provider first.
   */
  const [draftHost, setDraftHost] = useState<{ host: string; kind: NetworkListKind } | null>(null);

  const entryList = useGuardedList(
    networkService.getEntries,
    setEntries,
    setListError,
    "Failed to load the network lists.",
  );
  const forwardList = useGuardedList(
    networkService.getForwards,
    setForwards,
    setForwardError,
    "Failed to load the forwards.",
  );
  const refreshEntries = entryList.refresh;
  const refreshForwards = forwardList.refresh;

  const refreshLog = useCallback(async () => {
    try {
      const loaded = (await networkService.getLog(LOG_TAKE)).map(normalizeLogEntry);
      setLogError(null);
      setLog(loaded);
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
        setMode(parseMode(s.value));
      })
      .catch(() => {});
    void aiProviderService
      .getAll()
      .then(setProviders)
      .catch(() => {});
    void refreshEntries();
    void refreshForwards();
    void refreshLog();
  }, [refreshEntries, refreshForwards, refreshLog]);

  useEffect(() => {
    // The forwards ride on the same event: their listener set and the verdict on
    // each destination both move with the lists and the mode.
    const onPolicyChanged = () => {
      void refreshEntries();
      void refreshForwards();
      void settingsService
        .get(NetworkSettingKeys.Mode)
        .then((s) => {
          setMode(parseMode(s.value));
        })
        .catch(() => {});
    };
    const onLogAppended = (message: { payload: NetworkLogEntry }) => {
      const entry = normalizeLogEntry(message.payload);
      setLog((prev) => [entry, ...prev.filter((e) => e.id !== entry.id)].slice(0, LOG_TAKE));
    };
    // Lines were removed wholesale — by a manual clear, or by the retention
    // sweep, which leaves the newer ones behind. Re-read rather than empty the
    // view, so a sweep does not read as a clear.
    const onLogCleared = () => void refreshLog();

    on("NetworkPolicyChanged", onPolicyChanged);
    on("NetworkLogAppended", onLogAppended);
    on("NetworkLogCleared", onLogCleared);
    return () => {
      off("NetworkPolicyChanged", onPolicyChanged);
      off("NetworkLogAppended", onLogAppended);
      off("NetworkLogCleared", onLogCleared);
    };
  }, [on, off, refreshEntries, refreshForwards, refreshLog]);

  const changeMode = async (next: NetworkMode) => {
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

  const chooseMode = (next: NetworkMode) => {
    if (next === "off" && mode !== "off") setConfirmingOff(true);
    else void changeMode(next);
  };

  const addEntry = async (host: string, kind: NetworkListKind, aiProviderId: string | null) => {
    const created = await networkService.addEntry({ host, listKind: kind, aiProviderId });
    entryList.add(created);
  };

  const updateEntry = async (
    id: string,
    host: string,
    kind: NetworkListKind,
    aiProviderId: string | null,
  ) => {
    const updated = await networkService.updateEntry(id, { host, listKind: kind, aiProviderId });
    entryList.replace(updated);
  };

  const removeEntry = async (id: string) => {
    try {
      await networkService.deleteEntry(id);
      entryList.remove(id);
    } catch (err) {
      setListError(describeError(err, "Failed to remove the entry."));
    }
  };

  const addForward = async (name: string, host: string, port: number, localPort: number) => {
    const created = await networkService.addForward({
      name: name.trim(),
      host: host.trim(),
      port,
      localPort,
    });
    forwardList.add(created);
  };

  const updateForward = async (
    id: string,
    name: string,
    host: string,
    port: number,
    localPort: number,
  ) => {
    const updated = await networkService.updateForward(id, {
      name: name.trim(),
      host: host.trim(),
      port,
      localPort,
    });
    forwardList.replace(updated);
  };

  const removeForward = async (id: string) => {
    try {
      await networkService.deleteForward(id);
      forwardList.remove(id);
    } catch (err) {
      setForwardError(describeError(err, "Failed to remove the forward."));
    }
  };

  /**
   * The forward is transport; the lists are the decision. Allowing a blocked
   * destination is therefore the same click as anywhere else — one whitelist
   * entry — and the refetch the broadcast triggers re-reads the verdict.
   */
  const whitelistForwardHost = async (host: string) => {
    try {
      await addEntry(host, "Whitelist", null);
      await refreshForwards();
    } catch (err) {
      setForwardError(describeError(err, "Failed to add the host to the whitelist."));
    }
  };

  /** Take a host straight off the log onto a list, without retyping it. */
  const promote = async (entry: NetworkLogEntry, kind: NetworkListKind) => {
    try {
      const created = await networkService.addLogEntryToList(entry.id, kind);
      entryList.add(created);
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

  const visibleRules = useMemo(() => {
    const needle = ruleFilter.trim().toLowerCase();
    return [...entries]
      .sort((a, b) => a.host.localeCompare(b.host))
      .filter(
        (e) =>
          (kindFilter === "All" || e.listKind === kindFilter) &&
          (!needle || e.host.toLowerCase().includes(needle)),
      );
  }, [entries, ruleFilter, kindFilter]);

  const visibleLog = useMemo(() => {
    const needle = logFilter.trim().toLowerCase();
    const filtered = log.filter(
      (e) =>
        (decisionFilter === "All" || e.decision === decisionFilter) &&
        (!needle || e.host.toLowerCase().includes(needle)),
    );
    return grouped
      ? groupConsecutive(filtered)
      : filtered.map((entry) => ({
          key: entry.id,
          entry,
          count: 1,
          firstAt: entry.timestamp,
          lastAt: entry.timestamp,
        }));
  }, [log, logFilter, decisionFilter, grouped]);

  const ruleFor = (host: string) => entries.find((e) => coversHost(e.host, host));

  const activeMode = MODES.find((m) => m.value === mode);
  const offUnavailable = status?.offAvailable === false;
  const filtering = mode === "whitelist" || mode === "blacklist";
  const inEffect = entries.filter(
    (e) => (e.listKind === "Whitelist" ? "whitelist" : "blacklist") === mode,
  ).length;

  return (
    <>
      <div className="settings-pane-header">
        <h2>Network</h2>
        <p>Where agents may connect, and everywhere they have tried.</p>
      </div>

      {status && status.enforcement !== "enforced" && (
        <div className="net-banner" role="status">
          <strong>Advisory mode — agent network limits are not enforced.</strong> {status.reason}
        </div>
      )}
      {mode === "off" && (
        <div className="net-banner">
          <strong>Network mode is Off.</strong>{" "}
          {offUnavailable ? (
            <>{status.reason} Switch to Monitor to give agents the proxy back.</>
          ) : (
            <>
              Agents launched in it can reach any host, including your LAN and cloud metadata, and
              nothing is logged.
              {status?.enforcement === "enforced" && <> {status.reason}</>}
            </>
          )}
        </div>
      )}
      {listError && <div className="settings-error">{listError}</div>}

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Filter mode</h3>
        </div>
        <SettingRow
          label="Mode"
          help={
            <>
              {activeMode?.help} Switching to or from Off applies to the next agent launch; running
              agents keep the network they started with. A change between Monitor, Whitelist and
              Blacklist, or to a list, applies to the next connection; nothing needs a restart.
              {status && status.enforcement !== "enforced" && (
                <> Without the firewall, Off and Monitor differ only in logging.</>
              )}
              {offUnavailable && mode !== "off" && <> {status.reason}</>}
              {filtering && (
                <>
                  {" "}
                  <strong>{inEffect}</strong> {inEffect === 1 ? "rule is" : "rules are"} in effect.
                </>
              )}
              {modeError && <span className="settings-error"> {modeError}</span>}
            </>
          }
        >
          <Segmented
            label="Filter mode"
            value={mode}
            onChange={chooseMode}
            options={MODES.map((m) => ({
              value: m.value,
              label: m.label,
              disabled: m.value === "off" && offUnavailable && mode !== "off",
            }))}
          />
        </SettingRow>
      </section>

      <ConfirmModal
        isOpen={confirmingOff}
        title="Turn the network proxy off?"
        message="Agents launched from now on connect directly: agents can reach any host, including your LAN and cloud metadata, and nothing is logged. Agents already running keep the network they started with."
        confirmText="Turn off"
        onCancel={() => setConfirmingOff(false)}
        onConfirm={() => {
          setConfirmingOff(false);
          void changeMode("off");
        }}
      />

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Rules</h3>
        </div>
        {mode === "monitor" && (
          <p className="settings-card-note">
            Monitor blocks nothing, so no rule is in effect. Every destination is logged.
          </p>
        )}
        {mode === "off" && (
          <p className="settings-card-note">
            Agents bypass the proxy, so no rule is in effect and nothing is logged.
          </p>
        )}
        <div className="net-toolbar">
          <input
            type="search"
            className="settings-input"
            value={ruleFilter}
            onChange={(e) => setRuleFilter(e.target.value)}
            placeholder="Filter rules by host"
            aria-label="Filter rules"
          />
          <Segmented
            label="Rule kind filter"
            value={kindFilter}
            onChange={setKindFilter}
            options={[
              { value: "All" as const, label: "All" },
              { value: "Whitelist" as const, label: "Allow" },
              { value: "Blacklist" as const, label: "Block" },
            ]}
          />
        </div>
        <RuleTable
          entries={visibleRules}
          providers={providers}
          mode={mode}
          filtered={kindFilter !== "All" || ruleFilter.trim() !== ""}
          onUpdate={updateEntry}
          onRemove={removeEntry}
        />
        <AddRuleRow providers={providers} draft={draftHost} onAdd={addEntry} />
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Forwards</h3>
        </div>
        <p className="settings-card-note">
          A named destination the orchestrator relays on loopback, for clients that cannot be
          pointed at a proxy — databases, caches, mail. Point the client at{" "}
          <code>127.0.0.1:&lt;local port&gt;</code>; the rules above still decide whether the
          connection is made, and the log below still records it under the destination&apos;s host
          name.
        </p>
        {forwardError && <div className="settings-error">{forwardError}</div>}
        <ForwardTable
          forwards={forwards}
          onUpdate={updateForward}
          onRemove={removeForward}
          onWhitelist={whitelistForwardHost}
        />
        <AddForwardRow onAdd={addForward} />
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Traffic log</h3>
          <button
            type="button"
            className="btn"
            onClick={() => void clearLog()}
            disabled={log.length === 0}
          >
            Clear log
          </button>
        </div>
        <div className="net-toolbar">
          <input
            type="search"
            className="settings-input"
            value={logFilter}
            onChange={(e) => setLogFilter(e.target.value)}
            placeholder="Filter by host"
            aria-label="Filter the traffic log by host"
          />
          <Segmented
            label="Decision"
            value={decisionFilter}
            onChange={setDecisionFilter}
            options={[
              { value: "All" as const, label: "All" },
              { value: "Allowed" as const, label: "Allowed" },
              { value: "Blocked" as const, label: "Blocked" },
              { value: "Advisory" as const, label: "Advisory" },
            ]}
          />
          <label className="net-group-toggle">
            <Switch checked={grouped} onChange={setGrouped} label="Group repeats" />
            Group repeats
          </label>
        </div>
        {logError && <div className="settings-error">{logError}</div>}
        {visibleLog.length === 0 ? (
          <p className="settings-card-note">No destinations recorded yet.</p>
        ) : (
          <table className="net-table" aria-label="Traffic log">
            <thead>
              <tr>
                <th>When</th>
                <th>Destination</th>
                <th>Decision</th>
                <th>Provider</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {visibleLog.map((group) => {
                const rule = ruleFor(group.entry.host);
                return (
                  <tr key={group.key + group.lastAt}>
                    <td className="net-sub">
                      {new Date(group.lastAt).toLocaleTimeString()}
                      {group.count > 1 && (
                        <span className="net-sub">
                          first at {new Date(group.firstAt).toLocaleTimeString()}
                        </span>
                      )}
                    </td>
                    <td>
                      <span className="net-host">
                        {group.entry.host}
                        <span className="net-port">:{group.entry.port}</span>
                      </span>
                      {group.count > 1 && <span className="net-count">×{group.count}</span>}
                    </td>
                    <td>
                      <span
                        className={`net-decision net-decision-${group.entry.decision.toLowerCase()}`}
                      >
                        {group.entry.decision}
                      </span>
                    </td>
                    <td className="net-sub">
                      {group.entry.aiProviderId
                        ? (providers.find((p) => p.id === group.entry.aiProviderId)?.name ?? "—")
                        : "—"}
                    </td>
                    <td className="net-actions">
                      {rule ? (
                        <span className="net-sub">
                          covered by <span className="net-host">{rule.host}</span>
                        </span>
                      ) : (
                        <>
                          <button
                            type="button"
                            className="btn"
                            onClick={() => void promote(group.entry, "Whitelist")}
                            aria-label={`Add ${group.entry.host} to whitelist`}
                          >
                            Allow
                          </button>
                          <button
                            type="button"
                            className="btn"
                            onClick={() => void promote(group.entry, "Blacklist")}
                            aria-label={`Add ${group.entry.host} to blacklist`}
                          >
                            Block
                          </button>
                          <button
                            type="button"
                            className="btn"
                            onClick={() =>
                              setDraftHost({ host: group.entry.host, kind: "Whitelist" })
                            }
                            aria-label={`Write a rule for ${group.entry.host}`}
                            title="Load this host into the rule form"
                          >
                            Edit…
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        <NumericSettingField
          settingKey={NetworkSettingKeys.LogRetentionDays}
          label="Delete log entries after"
          min={0}
          max={3650}
          fallback={30}
          minLabel="0 (disabled)"
          unit="days"
        >
          How long a destination stays in the log before it is deleted. Every agent connection adds
          a line, so without a limit the log only grows. Set to <strong>0</strong> to keep every
          line forever.
        </NumericSettingField>
      </section>

      <style>{`
        .net-banner {
          background-color: #3b2f12;
          border: 1px solid #a16207;
          border-radius: 0.375rem;
          color: #fde68a;
          font-size: 0.8rem;
          padding: 0.5rem 0.75rem;
        }
        .net-table { width: 100%; border-collapse: collapse; font-size: 0.8rem; }
        .net-table th {
          text-align: left;
          font-weight: 500;
          color: #707090;
          font-size: 0.7rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          padding: 0 0.4rem 0.35rem;
        }
        .net-table td { padding: 0.45rem 0.4rem; border-top: 1px solid #2d2d44; color: #c0c0d0; vertical-align: top; }
        .net-row-idle { opacity: 0.55; }
        .net-idle-note { font-size: 0.7rem; color: #707090; margin-left: 0.4rem; }
        .net-host { font-family: monospace; color: #e0e0e0; }
        .net-port { color: #707090; }
        .net-sub { display: block; font-size: 0.72rem; color: #707090; }
        td.net-sub, .net-sub .net-host { display: inline; font-size: 0.72rem; }
        .net-count {
          margin-left: 0.4rem;
          padding: 0.05rem 0.35rem;
          border-radius: 999px;
          background-color: #2d2d44;
          color: #a0a0b0;
          font-size: 0.7rem;
        }
        .net-pill {
          display: inline-block;
          padding: 0.1rem 0.45rem;
          border-radius: 0.25rem;
          font-size: 0.7rem;
          text-transform: uppercase;
          letter-spacing: 0.04em;
        }
        .net-pill-whitelist { background-color: #14342a; color: #4ade80; }
        .net-pill-blacklist { background-color: #3a1c1c; color: #f87171; }
        .net-actions { text-align: right; white-space: nowrap; }
        .net-actions .btn { padding: 0.25rem 0.6rem; font-size: 0.75rem; background-color: #2d2d44; color: #c0c0d0; }
        .net-actions .btn:hover { background-color: #3a3a5c; }
        .net-add { margin-top: 0.9rem; padding-top: 0.9rem; border-top: 1px solid #2d2d44; }
        .net-add-row { display: flex; gap: 0.5rem; align-items: center; }
        .net-add-row > input[type="text"] { flex: 1; min-width: 0; }
        .net-toolbar { display: flex; gap: 0.75rem; align-items: center; margin-bottom: 0.75rem; flex-wrap: wrap; }
        .net-toolbar input[type="search"] { flex: 1; min-width: 10rem; }
        .net-group-toggle { display: flex; align-items: center; gap: 0.5rem; font-size: 0.78rem; color: #a0a0b0; }
        .net-decision { font-size: 0.75rem; }
        .net-decision-blocked { color: #f87171; }
        .net-decision-allowed { color: #4ade80; }
        .net-decision-advisory { color: #a0a0b0; }
        .net-forward-ok { color: #4ade80; }
        .net-forward-warn { color: #fbbf24; }
        .net-forward-blocked { color: #f87171; }
        .net-port-input { width: 6rem; flex: 0 0 auto; }
        .net-forward-add { align-items: flex-end; flex-wrap: wrap; }
        .net-field { display: flex; flex: 1 1 11rem; flex-direction: column; gap: 0.25rem; min-width: 0; }
        .net-field-destination { flex: 2 1 16rem; }
        .net-field-local { flex: 0 0 auto; }
        .net-field-label {
          color: #707090;
          font-size: 0.7rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }
        .net-field > .settings-input { min-width: 0; }
        .net-field-pair { display: flex; gap: 0.35rem; align-items: center; }
        .net-host-input { flex: 1 1 auto; min-width: 0; }
        .net-field-sep, .net-fixed-host { font-family: monospace; color: #707090; }
      `}</style>
    </>
  );
}
