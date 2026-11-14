import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { externalGrantService } from "../../../services/externalGrants";
import type {
  ExternalAgentGrant,
  ExternalAgentGrantDetail,
  ExternalAgentGrantScope,
  ExternalGrantProposal,
  IssuedExternalAgentGrant,
} from "../../../types";
import { errorMessage } from "../../../utils/errorMessage";
import {
  agentApiUrl,
  buildAccessInstructions,
  discoveryUrl,
  formatTimeLeft,
  mcpUrl,
} from "../../../utils/externalAccess";
import { formatRelativeTime } from "../../../utils/relativeTime";
import { SettingRow } from "../controls";

const LIFETIMES = [
  { minutes: 60, label: "1 hour" },
  { minutes: 480, label: "8 hours" },
  { minutes: 1440, label: "24 hours" },
  { minutes: 10080, label: "7 days" },
] as const;

const REFRESH_MS = 15_000;

/** Where a human decides what the grant proposed. */
function proposalLink(proposal: ExternalGrantProposal): string {
  switch (proposal.kind) {
    case "workItemEdit":
    case "humanInput":
      return `/taskboard/${encodeURIComponent(proposal.target)}`;
    case "loopTemplate":
      return `/loop-editor/${encodeURIComponent(proposal.target)}`;
    case "globalVariable":
      return "/global-variables";
  }
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    await navigator.clipboard.writeText(text);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 2000);
  };
  return (
    <button
      type="button"
      className="btn btn-sm btn-secondary"
      onClick={() => void copy()}
      aria-label={`Copy ${label}`}
    >
      {copied ? "Copied" : "Copy"}
    </button>
  );
}

function AccessField({ label, value }: { label: string; value: string }) {
  return (
    <SettingRow label={label}>
      <code className="external-access-value">{value}</code>
      <CopyButton text={value} label={label} />
    </SettingRow>
  );
}

/** Shown once, right after creation: the only time the token exists in readable form. */
function IssuedAccess({
  issued,
  baseUrl,
  onDone,
}: {
  issued: IssuedExternalAgentGrant;
  baseUrl: string;
  onDone: () => void;
}) {
  const { grant, token } = issued;
  const instructions = buildAccessInstructions({
    baseUrl,
    token,
    expiresAt: grant.expiresAt,
    scope: grant.scope,
  });
  return (
    <section className="settings-card" aria-label="New access">
      <div className="settings-card-header">
        <h3 className="settings-card-title">Access for {grant.name}</h3>
        <button type="button" className="btn btn-sm btn-ghost" onClick={onDone}>
          Done
        </button>
      </div>
      <p className="settings-card-note">
        Copy this now: the token is shown once and cannot be read again. Paste it into the AI
        session you are sharing access with.
      </p>
      <AccessField label="Base URL" value={baseUrl} />
      <AccessField label="Token" value={token} />
      <AccessField label="MCP endpoint" value={mcpUrl(baseUrl)} />
      <AccessField label="REST API" value={agentApiUrl(baseUrl)} />
      <AccessField label="Docs (no token needed)" value={discoveryUrl(baseUrl)} />
      <SettingRow label="Instructions for the AI">
        <textarea
          className="external-access-instructions"
          readOnly
          rows={9}
          value={instructions}
          aria-label="Instructions for the AI"
        />
      </SettingRow>
      <div className="external-access-actions">
        <CopyButton text={instructions} label="everything" />
      </div>
    </section>
  );
}

function GrantDetail({ detail, now }: { detail: ExternalAgentGrantDetail; now: number }) {
  return (
    <div className="external-grant-detail">
      <h4>Pending proposals</h4>
      {detail.pendingProposals.length === 0 ? (
        <p className="settings-row-help">None waiting for a decision.</p>
      ) : (
        <ul className="external-grant-list">
          {detail.pendingProposals.map((p) => (
            <li key={`${p.kind}-${p.id}`}>
              <Link to={proposalLink(p)}>{p.summary}</Link>
              {p.rationale && <div className="settings-row-help">{p.rationale}</div>}
              <div className="settings-row-help">{formatRelativeTime(p.createdAt, now)}</div>
            </li>
          ))}
        </ul>
      )}
      <h4>Recent activity</h4>
      {detail.activity.length === 0 ? (
        <p className="settings-row-help">Nothing yet.</p>
      ) : (
        <ul className="external-grant-list">
          {detail.activity.map((a) => (
            <li key={`${a.at}-${a.method}-${a.path}`}>
              <code>
                {a.method} {a.path}
              </code>{" "}
              <span className={a.statusCode >= 400 ? "external-grant-bad" : undefined}>
                {a.statusCode}
              </span>
              <span className="settings-row-help">
                {" "}
                {formatRelativeTime(a.at, now)}
                {a.from ? ` · ${a.from}` : ""}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function GrantRow({
  grant,
  now,
  onRevoke,
}: {
  grant: ExternalAgentGrant;
  now: number;
  onRevoke: (grant: ExternalAgentGrant) => void;
}) {
  const [open, setOpen] = useState(false);
  const [detail, setDetail] = useState<ExternalAgentGrantDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setDetail(await externalGrantService.get(grant.id));
      setError(null);
    } catch (err) {
      setError(errorMessage(err, "Failed to load the grant's details."));
    }
  }, [grant.id]);

  useEffect(() => {
    if (!open) return;
    void load();
    const timer = window.setInterval(() => void load(), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [open, load]);

  const live = grant.status === "active";
  return (
    <li className="external-grant">
      <div className="external-grant-head">
        <div>
          <span className="settings-row-label">{grant.name}</span>
          <span className={`settings-badge external-grant-${grant.status}`}>{grant.status}</span>
          {grant.scope === "ReadOnly" && <span className="settings-badge">read-only</span>}
          <div className="settings-row-help">
            {live
              ? formatTimeLeft(grant.expiresAt, now)
              : grant.status === "revoked"
                ? `Revoked ${formatRelativeTime(grant.revokedAt ?? grant.expiresAt, now)}`
                : `Expired ${formatRelativeTime(grant.expiresAt, now)}`}
            {" · "}
            {grant.lastUsedAt
              ? `last used ${formatRelativeTime(grant.lastUsedAt, now)}${grant.lastUsedFrom ? ` from ${grant.lastUsedFrom}` : ""}`
              : "never used"}
          </div>
        </div>
        <div className="external-grant-buttons">
          <button
            type="button"
            className="btn btn-sm btn-ghost"
            aria-expanded={open}
            onClick={() => setOpen((v) => !v)}
          >
            {open ? "Hide details" : "Details"}
          </button>
          {live && (
            <button
              type="button"
              className="btn btn-sm btn-secondary"
              onClick={() => onRevoke(grant)}
              aria-label={`Revoke ${grant.name}`}
            >
              Revoke
            </button>
          )}
        </div>
      </div>
      {open && error && <div className="settings-error">{error}</div>}
      {open && detail && <GrantDetail detail={detail} now={now} />}
    </li>
  );
}

/** Where the operator hands an AI session outside ILD temporary access to the agent API and MCP. */
export default function ExternalAccessSettings() {
  const [name, setName] = useState("");
  const [lifetime, setLifetime] = useState<number>(LIFETIMES[1].minutes);
  const [scope, setScope] = useState<ExternalAgentGrantScope>("Full");
  const [grants, setGrants] = useState<ExternalAgentGrant[]>([]);
  const [issued, setIssued] = useState<IssuedExternalAgentGrant | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const baseUrl = window.location.origin;

  const refresh = useCallback(async () => {
    try {
      setGrants(await externalGrantService.list());
      setError(null);
    } catch (err) {
      setError(errorMessage(err, "Failed to load the access grants."));
    }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => {
      setNow(Date.now());
      void refresh();
    }, REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [refresh]);

  const create = async () => {
    setCreating(true);
    try {
      setIssued(await externalGrantService.create(name.trim(), lifetime, scope));
      setName("");
      setError(null);
      await refresh();
    } catch (err) {
      setError(errorMessage(err, "Failed to create the access."));
    } finally {
      setCreating(false);
    }
  };

  const revoke = async (grant: ExternalAgentGrant) => {
    try {
      await externalGrantService.revoke(grant.id);
      await refresh();
    } catch (err) {
      setError(errorMessage(err, "Failed to revoke the access."));
    }
  };

  const revokeAll = async () => {
    try {
      await externalGrantService.revokeAll();
      await refresh();
    } catch (err) {
      setError(errorMessage(err, "Failed to revoke the access."));
    }
  };

  const anyLive = grants.some((g) => g.status === "active");

  return (
    <>
      <div className="settings-pane-header">
        <h2>Share access with an AI</h2>
        <p>
          Give an AI session running somewhere else temporary access to ILD&apos;s agent API and
          MCP. It works as a session of its own: it edits only the work items it creates and
          proposes changes to the rest, which you approve or reject.
        </p>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {issued && <IssuedAccess issued={issued} baseUrl={baseUrl} onDone={() => setIssued(null)} />}

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">New access</h3>
        </div>
        <SettingRow label="Name" help="Which AI session this is for." htmlFor="external-grant-name">
          <input
            id="external-grant-name"
            type="text"
            value={name}
            maxLength={200}
            placeholder="e.g. Claude Code on my laptop"
            onChange={(e) => setName(e.target.value)}
          />
        </SettingRow>
        <SettingRow
          label="Lasts"
          help="Then it stops working on its own. No access lasts longer than 7 days."
          htmlFor="external-grant-lifetime"
        >
          <select
            id="external-grant-lifetime"
            value={lifetime}
            onChange={(e) => setLifetime(Number(e.target.value))}
          >
            {LIFETIMES.map((l) => (
              <option key={l.minutes} value={l.minutes}>
                {l.label}
              </option>
            ))}
          </select>
        </SettingRow>
        <SettingRow
          label="Rights"
          help="Read-only can look but not create work items or propose."
          htmlFor="external-grant-scope"
        >
          <select
            id="external-grant-scope"
            value={scope}
            onChange={(e) => setScope(e.target.value as ExternalAgentGrantScope)}
          >
            <option value="Full">Read, create and propose</option>
            <option value="ReadOnly">Read-only</option>
          </select>
        </SettingRow>
        <div className="external-access-actions">
          <button
            type="button"
            className="btn btn-primary"
            disabled={creating || name.trim() === ""}
            onClick={() => void create()}
          >
            Create access
          </button>
        </div>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Access you have shared</h3>
          <button
            type="button"
            className="btn btn-sm btn-secondary"
            disabled={!anyLive}
            onClick={() => void revokeAll()}
          >
            Revoke all
          </button>
        </div>
        {grants.length === 0 ? (
          <p className="settings-card-note">Nothing shared right now.</p>
        ) : (
          <ul className="external-grant-list">
            {grants.map((g) => (
              <GrantRow key={g.id} grant={g} now={now} onRevoke={(grant) => void revoke(grant)} />
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
