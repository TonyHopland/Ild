import { useId, useState } from "react";
import type { NetworkForward } from "../../../types";
import { EditActions, useFormRequest, useRowEdit } from "./networkShared";

/**
 * Where a forward stands right now. The local port not being bound outranks
 * everything — nothing reaches the policy through a port that does not answer.
 */
function forwardState(forward: NetworkForward): {
  tone: "ok" | "warn" | "blocked";
  label: string;
  detail: string;
} {
  if (forward.listenError) {
    return { tone: "warn", label: "Local port unavailable", detail: forward.listenError };
  }
  if (forward.decision === "Blocked") {
    return {
      tone: "blocked",
      label: "Host not allowed by current mode",
      detail: `Connections are answered and refused at once; ${forward.host} is not reachable until the rules allow it.`,
    };
  }
  return {
    tone: "ok",
    label: "Listening",
    detail: `Point a client at 127.0.0.1:${forward.localPort}.`,
  };
}

/**
 * A typed port, or null when it is not one — blank, `5432x` and `0x10` all read
 * as null. Digits only: `Number` would take `1e3` and `0x10` as 1000 and 16,
 * sending a port nobody typed.
 */
function parsePort(value: string): number | null {
  const trimmed = value.trim();
  if (!/^\d+$/.test(trimmed)) return null;
  const port = Number(trimmed);
  return port >= 1 && port <= 65535 ? port : null;
}

/** What a forward form has typed so far: the parsed ports, whether it can be sent, and what is wrong with a port. */
function checkForwardDraft(name: string, host: string, port: string, localPort: string) {
  const destination = parsePort(port);
  const local = parsePort(localPort);
  const complete =
    name.trim() !== "" && host.trim() !== "" && destination !== null && local !== null;
  // Only once something has been typed: an empty form is incomplete, not wrong.
  const portProblem =
    (port.trim() !== "" && destination === null) || (localPort.trim() !== "" && local === null)
      ? "Both ports are whole numbers between 1 and 65535."
      : null;
  return { destination, local, complete, portProblem };
}

interface ForwardTableProps {
  forwards: NetworkForward[];
  onUpdate: (
    id: string,
    name: string,
    host: string,
    port: number,
    localPort: number,
  ) => Promise<void>;
  onRemove: (id: string) => Promise<void>;
  onWhitelist: (host: string) => Promise<void>;
}

export function ForwardTable({ forwards, onUpdate, onRemove, onWhitelist }: ForwardTableProps) {
  if (forwards.length === 0) {
    return <p className="settings-card-note">No forwards yet.</p>;
  }
  return (
    <table className="net-table" aria-label="Forwards">
      <thead>
        <tr>
          <th>Name</th>
          <th>Destination</th>
          <th>Local address</th>
          <th>State</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {forwards.map((forward) => (
          <ForwardRow
            key={forward.id}
            forward={forward}
            onUpdate={onUpdate}
            onRemove={onRemove}
            onWhitelist={onWhitelist}
          />
        ))}
      </tbody>
    </table>
  );
}

interface ForwardRowProps {
  forward: NetworkForward;
  onUpdate: ForwardTableProps["onUpdate"];
  onRemove: ForwardTableProps["onRemove"];
  onWhitelist: ForwardTableProps["onWhitelist"];
}

function ForwardRow({ forward, onUpdate, onRemove, onWhitelist }: ForwardRowProps) {
  const state = forwardState(forward);
  const row = useRowEdit("Failed to save the forward.");
  const [name, setName] = useState("");
  const [host, setHost] = useState("");
  const [port, setPort] = useState("");
  const [localPort, setLocalPort] = useState("");

  const { destination, local, complete, portProblem } = checkForwardDraft(
    name,
    host,
    port,
    localPort,
  );
  const subject = `${forward.name} forward`;

  const edit = () =>
    row.start(() => {
      setName(forward.name);
      setHost(forward.host);
      setPort(String(forward.port));
      setLocalPort(String(forward.localPort));
    });

  const save = () => {
    if (!complete || destination === null || local === null) return;
    void row.save(() => onUpdate(forward.id, name, host, destination, local));
  };

  const onEnter = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") save();
  };

  const label = (what: string) => `${what} of the ${subject}`;

  return (
    <tr className={state.tone === "ok" || row.editing ? "" : "net-row-idle"}>
      <td>
        {row.editing ? (
          <input
            type="text"
            className="settings-input"
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={onEnter}
            aria-label={label("Name")}
          />
        ) : (
          forward.name
        )}
      </td>
      <td>
        {row.editing ? (
          <div className="net-field-pair">
            <input
              type="text"
              className="settings-input net-host-input"
              value={host}
              onChange={(e) => setHost(e.target.value)}
              onKeyDown={onEnter}
              aria-label={label("Destination host")}
            />
            <span className="net-field-sep">:</span>
            <input
              type="text"
              inputMode="numeric"
              className="settings-input net-port-input"
              value={port}
              onChange={(e) => setPort(e.target.value)}
              onKeyDown={onEnter}
              aria-label={label("Destination port")}
            />
          </div>
        ) : (
          <span className="net-host">
            {forward.host}
            <span className="net-port">:{forward.port}</span>
          </span>
        )}
      </td>
      <td>
        {row.editing ? (
          <div className="net-field-pair">
            <span className="net-fixed-host">127.0.0.1</span>
            <span className="net-field-sep">:</span>
            <input
              type="text"
              inputMode="numeric"
              className="settings-input net-port-input"
              value={localPort}
              onChange={(e) => setLocalPort(e.target.value)}
              onKeyDown={onEnter}
              aria-label={label("Local port")}
            />
          </div>
        ) : (
          <span className="net-host">
            127.0.0.1<span className="net-port">:{forward.localPort}</span>
          </span>
        )}
      </td>
      <td>
        <span className={`net-decision net-forward-${state.tone}`}>{state.label}</span>
        <span className="net-sub">{state.detail}</span>
        {row.editing && portProblem && <span className="settings-error"> {portProblem}</span>}
        {row.editing && row.error && <span className="settings-error"> {row.error}</span>}
      </td>
      <td className="net-actions">
        {row.editing ? (
          <EditActions
            subject={subject}
            busy={row.busy}
            canSave={complete}
            onSave={save}
            onCancel={row.cancel}
          />
        ) : (
          <>
            {state.tone === "blocked" && (
              <button
                type="button"
                className="btn"
                onClick={() => void onWhitelist(forward.host)}
                aria-label={`Add ${forward.host} to whitelist`}
              >
                Allow
              </button>
            )}
            <button type="button" className="btn" onClick={edit} aria-label={`Edit the ${subject}`}>
              Edit
            </button>
            <button
              type="button"
              className="btn"
              onClick={() => void onRemove(forward.id)}
              aria-label={`Remove the ${subject}`}
            >
              Remove
            </button>
          </>
        )}
      </td>
    </tr>
  );
}

interface AddForwardRowProps {
  onAdd: (name: string, host: string, port: number, localPort: number) => Promise<void>;
}

export function AddForwardRow({ onAdd }: AddForwardRowProps) {
  const field = useId();
  const { busy, error, send } = useFormRequest("Failed to add the forward.");
  const [name, setName] = useState("");
  const [host, setHost] = useState("");
  const [port, setPort] = useState("");
  const [localPort, setLocalPort] = useState("");

  const { destination, local, complete, portProblem } = checkForwardDraft(
    name,
    host,
    port,
    localPort,
  );

  const add = async () => {
    if (!complete || destination === null || local === null) return;
    if (!(await send(() => onAdd(name, host, destination, local)))) return;
    setName("");
    setHost("");
    setPort("");
    setLocalPort("");
  };

  const onEnter = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") void add();
  };

  return (
    <div className="net-add">
      <div className="net-add-row net-forward-add">
        <div className="net-field">
          <label className="net-field-label" htmlFor={`${field}-name`}>
            Name
          </label>
          <input
            id={`${field}-name`}
            type="text"
            className="settings-input"
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={onEnter}
            placeholder="postgres"
          />
        </div>
        <div className="net-field net-field-destination">
          <span className="net-field-label" id={`${field}-destination`}>
            Destination
          </span>
          <div className="net-field-pair" role="group" aria-labelledby={`${field}-destination`}>
            <input
              type="text"
              className="settings-input net-host-input"
              value={host}
              onChange={(e) => setHost(e.target.value)}
              onKeyDown={onEnter}
              placeholder="db.example.com"
              aria-label="Destination host"
            />
            <span className="net-field-sep">:</span>
            <input
              type="text"
              inputMode="numeric"
              className="settings-input net-port-input"
              value={port}
              onChange={(e) => setPort(e.target.value)}
              onKeyDown={onEnter}
              placeholder="5432"
              aria-label="Destination port"
            />
          </div>
        </div>
        <div className="net-field net-field-local">
          <span className="net-field-label" id={`${field}-local`}>
            Local address
          </span>
          <div className="net-field-pair" role="group" aria-labelledby={`${field}-local`}>
            <span className="net-fixed-host">127.0.0.1</span>
            <span className="net-field-sep">:</span>
            <input
              type="text"
              inputMode="numeric"
              className="settings-input net-port-input"
              value={localPort}
              onChange={(e) => setLocalPort(e.target.value)}
              onKeyDown={onEnter}
              placeholder="15432"
              aria-label="Local port"
            />
          </div>
        </div>
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => void add()}
          disabled={busy || !complete}
        >
          Add forward
        </button>
      </div>
      <p className="settings-row-help">
        A destination is one host name or IP address — a pattern like <code>.example.com</code>{" "}
        covers a set and is not somewhere to connect.
        {portProblem && <span className="settings-error"> {portProblem}</span>}
        {error && <span className="settings-error"> {error}</span>}
      </p>
    </div>
  );
}
