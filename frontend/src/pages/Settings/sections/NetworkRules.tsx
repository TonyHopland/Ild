import { useEffect, useRef, useState } from "react";
import type { AiProvider, NetworkListKind, NetworkMode, NetworkPolicyEntry } from "../../../types";
import { Segmented } from "../controls";
import { EditActions, listInEffect, useFormRequest, useRowEdit } from "./networkShared";

/** What a pattern covers, spelled out so a leading dot is not a thing to know. */
function describePattern(host: string): string {
  const trimmed = host.trim();
  if (!trimmed) return "";
  if (trimmed.startsWith(".")) return `matches ${trimmed.slice(1)} and every subdomain of it`;
  return `matches ${trimmed} exactly`;
}

interface RuleTableProps {
  entries: NetworkPolicyEntry[];
  providers: AiProvider[];
  mode: NetworkMode | null;
  /** True when a filter is hiding rules, so "none" reads as "none matching". */
  filtered: boolean;
  onUpdate: (
    id: string,
    host: string,
    kind: NetworkListKind,
    aiProviderId: string | null,
  ) => Promise<void>;
  onRemove: (id: string) => Promise<void>;
}

export function RuleTable({
  entries,
  providers,
  mode,
  filtered,
  onUpdate,
  onRemove,
}: RuleTableProps) {
  if (entries.length === 0) {
    return (
      <p className="settings-card-note">
        {filtered ? "No rule matches that filter." : "No rules yet."}
      </p>
    );
  }
  return (
    <table className="net-table" aria-label="Network rules">
      <thead>
        <tr>
          <th>Rule</th>
          <th>Pattern</th>
          <th>Scope</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {entries.map((entry) => (
          <RuleRow
            key={entry.id}
            entry={entry}
            providers={providers}
            mode={mode}
            onUpdate={onUpdate}
            onRemove={onRemove}
          />
        ))}
      </tbody>
    </table>
  );
}

interface RuleRowProps {
  entry: NetworkPolicyEntry;
  providers: AiProvider[];
  mode: NetworkMode | null;
  onUpdate: RuleTableProps["onUpdate"];
  onRemove: RuleTableProps["onRemove"];
}

function RuleRow({ entry, providers, mode, onUpdate, onRemove }: RuleRowProps) {
  const row = useRowEdit("Failed to save the rule.");
  const [host, setHost] = useState("");
  const [kind, setKind] = useState<NetworkListKind>("Whitelist");
  const [scope, setScope] = useState("");

  const list = entry.listKind === "Whitelist" ? "whitelist" : "blacklist";
  // Without a list mode nothing is in effect, and saying so on every row
  // is noise; the card says it once instead.
  const inEffect = listInEffect(mode, list);
  const subject = `${entry.host} rule`;

  const edit = () =>
    row.start(() => {
      setHost(entry.host);
      setKind(entry.listKind);
      setScope(entry.aiProviderId ?? "");
    });

  const save = () => {
    if (host.trim() === "") return;
    void row.save(() => onUpdate(entry.id, host, kind, scope || null));
  };

  return (
    <tr className={inEffect || row.editing ? "" : "net-row-idle"}>
      <td>
        {row.editing ? (
          <RuleKindPicker label={`Kind of the ${subject}`} value={kind} onChange={setKind} />
        ) : (
          <>
            <span className={`net-pill net-pill-${list}`}>
              {entry.listKind === "Whitelist" ? "Allow" : "Block"}
            </span>
            {!inEffect && <span className="net-idle-note">not in effect</span>}
          </>
        )}
      </td>
      <td>
        {row.editing ? (
          <>
            <input
              type="text"
              className="settings-input"
              value={host}
              onChange={(e) => setHost(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") save();
              }}
              aria-label={`Host pattern of the ${subject}`}
            />
            <span className="net-sub">{describePattern(host)}</span>
            {row.error && <span className="settings-error"> {row.error}</span>}
          </>
        ) : (
          <>
            <span className="net-host">{entry.host}</span>
            <span className="net-sub">{describePattern(entry.host)}</span>
          </>
        )}
      </td>
      <td className={row.editing ? undefined : "net-sub"}>
        {row.editing ? (
          <ProviderScopeSelect
            providers={providers}
            value={scope}
            onChange={setScope}
            label={`Scope of the ${subject}`}
          />
        ) : entry.aiProviderId ? (
          (providers.find((p) => p.id === entry.aiProviderId)?.name ?? "one provider")
        ) : (
          "All providers"
        )}
      </td>
      <td className="net-actions">
        {row.editing ? (
          <EditActions
            subject={subject}
            busy={row.busy}
            canSave={host.trim() !== ""}
            onSave={save}
            onCancel={row.cancel}
          />
        ) : (
          <>
            <button
              type="button"
              className="btn"
              onClick={edit}
              aria-label={`Edit ${entry.host} on the ${list}`}
            >
              Edit
            </button>
            <button
              type="button"
              className="btn"
              onClick={() => void onRemove(entry.id)}
              aria-label={`Remove ${entry.host} from the ${list}`}
            >
              Remove
            </button>
          </>
        )}
      </td>
    </tr>
  );
}

function RuleKindPicker({
  label,
  value,
  onChange,
}: {
  label: string;
  value: NetworkListKind;
  onChange: (kind: NetworkListKind) => void;
}) {
  return (
    <Segmented
      label={label}
      value={value}
      onChange={onChange}
      options={[
        { value: "Whitelist", label: "Allow" },
        { value: "Blacklist", label: "Block" },
      ]}
    />
  );
}

function ProviderScopeSelect({
  providers,
  value,
  onChange,
  label,
}: {
  providers: AiProvider[];
  value: string;
  onChange: (providerId: string) => void;
  label: string;
}) {
  return (
    <select
      className="settings-input"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label={label}
    >
      <option value="">All providers</option>
      {value !== "" && !providers.some((p) => p.id === value) && (
        <option value={value}>one provider</option>
      )}
      {providers.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
        </option>
      ))}
    </select>
  );
}

interface AddRuleRowProps {
  providers: AiProvider[];
  /** A host loaded from the log, to be edited before it becomes a rule. */
  draft: { host: string; kind: NetworkListKind } | null;
  onAdd: (host: string, kind: NetworkListKind, aiProviderId: string | null) => Promise<void>;
}

export function AddRuleRow({ providers, draft, onAdd }: AddRuleRowProps) {
  const { busy, error, setError, send } = useFormRequest("Failed to add the rule.");
  const [host, setHost] = useState("");
  const [kind, setKind] = useState<NetworkListKind>("Whitelist");
  const [scope, setScope] = useState("");
  const hostInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!draft) return;
    setHost(draft.host);
    setKind(draft.kind);
    setError(null);
    hostInput.current?.focus();
    hostInput.current?.select();
  }, [draft, setError]);

  const add = async () => {
    if (host.trim() === "") return;
    if (await send(() => onAdd(host, kind, scope || null))) setHost("");
  };

  return (
    <div className="net-add">
      <div className="net-add-row">
        <RuleKindPicker label="Rule kind" value={kind} onChange={setKind} />
        <input
          ref={hostInput}
          type="text"
          className="settings-input"
          value={host}
          onChange={(e) => setHost(e.target.value)}
          placeholder="api.example.com or .example.com"
          aria-label="Host pattern"
          onKeyDown={(e) => {
            if (e.key === "Enter") void add();
          }}
        />
        <ProviderScopeSelect
          providers={providers}
          value={scope}
          onChange={setScope}
          label="Scope for the new rule"
        />
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => void add()}
          disabled={busy || host.trim() === ""}
        >
          Add rule
        </button>
      </div>
      <p className="settings-row-help">
        {host.trim()
          ? describePattern(host)
          : "A bare host matches exactly; a leading dot covers the domain and every subdomain."}
        {error && <span className="settings-error"> {error}</span>}
      </p>
    </div>
  );
}
