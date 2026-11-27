import { useEffect, useState } from "react";
import { networkService } from "../../../services/auth";
import type { AiProvider, NetworkLogDestination, NetworkPolicyEntry } from "../../../types";
import { INSTALLERS_SCOPE } from "./NetworkRules";
import { describeError } from "./networkShared";

export const DESTINATION_DAYS = 7;

type ScopeKind = "global" | "installers" | "provider";

/** A scope value is `""` for every provider, the installers' scope, or a provider id. */
export function scopeRequest(scope: string): { kind: ScopeKind; providerId?: string } {
  if (scope === "") return { kind: "global" };
  if (scope === INSTALLERS_SCOPE) return { kind: "installers" };
  return { kind: "provider", providerId: scope };
}

interface Choice {
  checked: boolean;
  subdomains: boolean;
}

interface AllowFromLogDialogProps {
  providers: AiProvider[];
  initialScope: string;
  onClose: () => void;
  onAdded: (entries: NetworkPolicyEntry[]) => void;
}

/**
 * The hosts the log has seen for one scope, to tick and allow. Nothing is written until Add: the
 * policy starts empty, so this is how a whitelist gets written from what actually connected.
 */
export function AllowFromLogDialog({
  providers,
  initialScope,
  onClose,
  onAdded,
}: AllowFromLogDialogProps) {
  const [scope, setScope] = useState(initialScope);
  const [destinations, setDestinations] = useState<NetworkLogDestination[] | null>(null);
  const [choices, setChoices] = useState<Record<string, Choice>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let current = true;
    const { kind, providerId } = scopeRequest(scope);
    setDestinations(null);
    setChoices({});
    setError(null);
    networkService
      .getLogDestinations(kind, providerId, DESTINATION_DAYS)
      .then((loaded) => {
        if (current) setDestinations(loaded);
      })
      .catch((err) => {
        if (!current) return;
        setDestinations([]);
        setError(describeError(err, "Failed to load the hosts from the log."));
      });
    return () => {
      current = false;
    };
  }, [scope]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const choiceFor = (host: string): Choice =>
    choices[host] ?? { checked: false, subdomains: false };
  const change = (host: string, patch: Partial<Choice>) =>
    setChoices((prev) => ({ ...prev, [host]: { ...choiceFor(host), ...patch } }));

  const open = (destinations ?? []).filter((d) => !d.allowed);
  const selected = open.filter((d) => choiceFor(d.host).checked);

  const confirm = async () => {
    const { kind, providerId } = scopeRequest(scope);
    setSaving(true);
    setError(null);
    try {
      const created = await networkService.addAllowed(
        kind,
        providerId ?? null,
        selected.map((d) => (choiceFor(d.host).subdomains ? `.${d.host}` : d.host)),
      );
      onAdded(created);
      onClose();
    } catch (err) {
      setError(describeError(err, "Failed to allow the hosts."));
      setSaving(false);
    }
  };

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div
        className="modal-content"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label="Add allowed hosts from the log"
      >
        <div className="modal-header">
          <h2>Add allowed hosts from the log</h2>
        </div>
        <div className="modal-body">
          <p className="settings-card-note">
            Hosts used in the last {DESTINATION_DAYS} days. Tick the ones to allow; nothing is
            written until you add them.
          </p>
          <select
            className="settings-input"
            value={scope}
            onChange={(e) => setScope(e.target.value)}
            aria-label="Scope to allow the hosts in"
          >
            <option value="">All providers</option>
            <option value={INSTALLERS_SCOPE}>Installers</option>
            {scope !== "" &&
              scope !== INSTALLERS_SCOPE &&
              !providers.some((p) => p.id === scope) && <option value={scope}>one provider</option>}
            {providers.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
          {error && <div className="settings-error">{error}</div>}
          {destinations === null ? (
            <p className="settings-card-note">Loading…</p>
          ) : open.length === 0 ? (
            <p className="settings-card-note">
              {destinations.length === 0
                ? "No hosts were logged for this scope in that time."
                : "Every host logged for this scope is already allowed."}
            </p>
          ) : (
            <table className="net-table" aria-label="Hosts from the log">
              <thead>
                <tr>
                  <th />
                  <th>Host</th>
                  <th>Seen</th>
                  <th>Last seen</th>
                  <th>Allow</th>
                </tr>
              </thead>
              <tbody>
                {open.map((d) => {
                  const choice = choiceFor(d.host);
                  return (
                    <tr key={d.host}>
                      <td>
                        <input
                          type="checkbox"
                          checked={choice.checked}
                          onChange={(e) => change(d.host, { checked: e.target.checked })}
                          aria-label={`Allow ${d.host}`}
                        />
                      </td>
                      <td className="net-host">{d.host}</td>
                      <td>×{d.count}</td>
                      <td className="net-sub">{new Date(d.lastSeen).toLocaleString()}</td>
                      <td>
                        <select
                          className="settings-input"
                          value={choice.subdomains ? "suffix" : "exact"}
                          onChange={(e) =>
                            change(d.host, { subdomains: e.target.value === "suffix" })
                          }
                          aria-label={`How to match ${d.host}`}
                        >
                          <option value="exact">This host only</option>
                          <option value="suffix">Host and subdomains</option>
                        </select>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => void confirm()}
            disabled={selected.length === 0 || saving}
          >
            Add {selected.length > 0 ? selected.length : ""} allowed{" "}
            {selected.length === 1 ? "host" : "hosts"}
          </button>
        </div>
      </div>
    </div>
  );
}
